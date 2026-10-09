// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 lnkiai

#include "Pipe.h"

#include <sddl.h>

namespace meltype {
namespace {

// \\.\pipe\Meltype.Tip.<ユーザーの SID> (Meltype.exe の TipServer.PipeName と同じ)
std::wstring PipeName() {
    std::wstring name = L"\\\\.\\pipe\\Meltype.Tip.";
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return name + L"user";
    DWORD size = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    std::wstring sid = L"user";
    if (size > 0 && GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) {
        LPWSTR text = nullptr;
        if (ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text)) {
            sid = text;
            LocalFree(text);
        }
    }
    CloseHandle(token);
    return name + sid;
}

}  // namespace

// トークンの整合性レベル (SECURITY_MANDATORY_*_RID)。読めなければ 0
static DWORD IntegrityOf(HANDLE token) {
    DWORD size = 0;
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    if (size == 0 || !GetTokenInformation(token, TokenIntegrityLevel, buffer.data(), size, &size)) return 0;
    PSID sid = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer.data())->Label.Sid;
    return *GetSidSubAuthority(sid, *GetSidSubAuthorityCount(sid) - 1);
}

// 自分 (IME を読み込んだアプリ) の整合性レベル。読めなければ 0
static DWORD SelfIntegrity() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return 0;
    DWORD level = IntegrityOf(token);
    CloseHandle(token);
    return level;
}

// 自分が整合性レベル Low 以下 (ストアアプリ・サンドボックス) で動いているか
static bool SelfIsLowIntegrity() { return SelfIntegrity() < SECURITY_MANDATORY_MEDIUM_RID; }

// つないだパイプのサーバーが、自分と同じユーザーの、整合性レベル Medium 以上で、自分より低くないプロセスか。
// (Meltype.exe が動いていない間に、権限の低いプログラムが同じ名前のパイプを作って打鍵を受け取るのを防ぐ。
//  管理者として動いているアプリの打鍵を、ふつうの権限で動く Meltype.exe に渡さない。UAC を切っていて、
//  アプリも Meltype.exe も管理者として動いている PC では使える)
// ストアアプリなど自分が Low のときは、ほかのプロセスを調べられないので確かめない。このときは、Meltype.exe が動いていない間に
// 同じユーザーの Low のプロセスが同じ名前のパイプを作ると見分けられない (SECURITY.md。Meltype.exe は先に作られていたらログに書く)
// lower: 相手が自分より低い権限だったので断った (Meltype.exe を起動し直すまで変わらない)
static bool IsTrustedServer(HANDLE pipe, bool& lower) {
    lower = false;
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &pid)) return false;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (process == nullptr) return SelfIsLowIntegrity();
    bool trusted = false;
    HANDLE token = nullptr;
    if (OpenProcessToken(process, TOKEN_QUERY, &token)) {
        DWORD size = 0;
        GetTokenInformation(token, TokenUser, nullptr, 0, &size);
        std::vector<BYTE> user(size);
        HANDLE self = nullptr;
        if (size > 0 && GetTokenInformation(token, TokenUser, user.data(), size, &size) && OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &self)) {
            DWORD selfSize = 0;
            GetTokenInformation(self, TokenUser, nullptr, 0, &selfSize);
            std::vector<BYTE> selfUser(selfSize);
            if (selfSize > 0 && GetTokenInformation(self, TokenUser, selfUser.data(), selfSize, &selfSize)) {
                trusted = EqualSid(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, reinterpret_cast<TOKEN_USER*>(selfUser.data())->User.Sid) != FALSE;
            }
            CloseHandle(self);
        }
        DWORD level = trusted ? IntegrityOf(token) : 0;
        DWORD own = SelfIntegrity();
        trusted = level >= SECURITY_MANDATORY_MEDIUM_RID && level >= own;
        lower = level >= SECURITY_MANDATORY_MEDIUM_RID && level < own;
        CloseHandle(token);
    } else {
        // 相手の権限を調べられない (ほかのユーザーのプロセスか、このアプリより権限の高いプロセス。
        // UAC が有効な PC で Meltype.exe を管理者として動かすと、ふつうのアプリからはこうなる)。確かめられない相手にはつながない
    }
    CloseHandle(process);
    return trusted;
}

bool PipeClient::Connect() {
    if (pipe_ != INVALID_HANDLE_VALUE) return true;
    if (GetTickCount64() < retryAfter_) return false;
    static const std::wstring name = PipeName();
    // GENERIC_WRITE には FILE_APPEND_DATA (パイプでは「パイプを増やす」権限) が含まれ、ストアアプリにはその権限を渡していないので
    // 開けなくなる。読み書きと、メッセージ単位の読み取りにする (SetNamedPipeHandleState) のに要る権限だけにする
    const DWORD access = GENERIC_READ | FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES;
    // サーバーには、こちらの身元を確かめることだけを許す (なりすまして動くことはさせない)
    const DWORD flags = FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION;
    pipe_ = CreateFileW(name.c_str(), access, 0, nullptr, OPEN_EXISTING, flags, nullptr);
    if (pipe_ == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY && WaitNamedPipeW(name.c_str(), 100)) {
        pipe_ = CreateFileW(name.c_str(), access, 0, nullptr, OPEN_EXISTING, flags, nullptr);
    }
    if (pipe_ == INVALID_HANDLE_VALUE) {
        // 同じ理由でつながらないことが続いても (Meltype.exe が動いていない・つなげないサンドボックス)、ログは理由が変わったときだけ
        DWORD error = GetLastError();
        if (error != lastConnectError_) TipLog(L"パイプにつながりません: %lu", error);
        lastConnectError_ = error;
        retryAfter_ = GetTickCount64() + 2000;
        return false;
    }
    lastConnectError_ = 0;
    bool lower = false;
    if (!IsTrustedServer(pipe_, lower)) {
        // ログは理由ごとに 1 回だけ (つなぎ直すたびに書かない)
        if (!refusalLogged_) {
            TipLog(lower ? L"このアプリは Meltype.exe より高い権限 (管理者など) で動いているので、つなぎません"
                         : L"パイプの相手が Meltype.exe と確かめられないので、つなぎません (ほかのユーザーのプロセスか、このアプリより高い権限のプロセス)");
        }
        refusalLogged_ = true;
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
        // 権限の違いは Meltype.exe を起動し直すまで変わらないので、しばらくつなぎ直さない
        retryAfter_ = GetTickCount64() + (lower ? 60000 : 5000);
        return false;
    }
    refusalLogged_ = false;
    DWORD mode = PIPE_READMODE_MESSAGE;
    SetNamedPipeHandleState(pipe_, &mode, nullptr, nullptr);
    if (event_ == nullptr) event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    return true;
}

void PipeClient::Close() {
    if (pipe_ != INVALID_HANDLE_VALUE) {
        CancelIoEx(pipe_, nullptr);
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    if (event_ != nullptr) {
        CloseHandle(event_);
        event_ = nullptr;
    }
}

bool PipeClient::Wait(OVERLAPPED& overlapped, DWORD timeoutMs, DWORD& transferred) {
    if (WaitForSingleObject(event_, timeoutMs) != WAIT_OBJECT_0) {
        CancelIoEx(pipe_, &overlapped);
        GetOverlappedResult(pipe_, &overlapped, &transferred, TRUE);
        return false;
    }
    return GetOverlappedResult(pipe_, &overlapped, &transferred, FALSE) != FALSE || GetLastError() == ERROR_MORE_DATA;
}

bool PipeClient::Transact(const std::string& request, std::string& response, DWORD timeoutMs) {
    response.clear();
    if (!Connect()) return false;
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    auto remaining = [&]() -> DWORD {
        ULONGLONG now = GetTickCount64();
        return now >= deadline ? 0 : static_cast<DWORD>(deadline - now);
    };

    OVERLAPPED overlapped = {};
    overlapped.hEvent = event_;
    ResetEvent(event_);
    DWORD transferred = 0;
    if (!WriteFile(pipe_, request.data(), static_cast<DWORD>(request.size()), nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING) {
        TipLog(L"パイプに書けません: %lu", GetLastError());
        Close();
        return false;
    }
    if (!Wait(overlapped, remaining(), transferred)) {
        // 読み取りが時間内に終わらないときと同じく、応答しない Meltype.exe をしばらく待たない
        TipLog(L"パイプへの書き込みが終わりません");
        Close();
        retryAfter_ = GetTickCount64() + 3000;
        return false;
    }

    char buffer[16 * 1024];
    while (true) {
        overlapped = {};
        overlapped.hEvent = event_;
        ResetEvent(event_);
        BOOL ok = ReadFile(pipe_, buffer, sizeof(buffer), nullptr, &overlapped);
        DWORD error = ok ? ERROR_SUCCESS : GetLastError();
        if (!ok && error != ERROR_IO_PENDING && error != ERROR_MORE_DATA) {
            // 切られた (Meltype.exe が終わった・つながりが多すぎて断られた): すぐにはつなぎ直さない
            TipLog(L"パイプから読めません: %lu", error);
            Close();
            retryAfter_ = GetTickCount64() + 2000;
            return false;
        }
        if (!Wait(overlapped, remaining(), transferred)) {
            // 応答しない Meltype.exe を毎回待つと、どのアプリでも打鍵のたびに止まるので、しばらくつながない
            TipLog(L"Meltype.exe の応答が時間内に来ません");
            Close();
            retryAfter_ = GetTickCount64() + 3000;
            return false;
        }
        response.append(buffer, transferred);
        if (response.size() > kMaxResponse) {
            TipLog(L"応答が大きすぎます");
            Close();
            return false;
        }
        // メッセージの続きがあれば ERROR_MORE_DATA
        if (GetOverlappedResult(pipe_, &overlapped, &transferred, FALSE)) return true;
        if (GetLastError() != ERROR_MORE_DATA) {
            Close();
            return false;
        }
    }
}

}  // namespace meltype
