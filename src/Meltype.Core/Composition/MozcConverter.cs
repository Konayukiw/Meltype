// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Meltype.Composition;

/// <summary>
/// Mozc (Google 日本語入力のオープンソース版、BSD-3-Clause) の変換エンジン。
/// 同梱の変換ヘルパー (meltype_mozc_helper) を別のプロセスで動かし、標準入出力で変換を頼む。
///
/// やり取り (UTF-8、1 行ずつ): 「C[TAB]前の文字列[TAB]読み」を送ると、文節ごとに「読み[US]候補1[US]候補2…」を [RS] でつないだ 1 行が返る。
/// ヘルパーが無い・起動できない・応答しないときは null を返すので、呼び出し側で別の変換エンジンを使う。
/// </summary>
public sealed class MozcConverter : IKanjiConverter, ILearningConverter, IDisposable
{
    private const char UnitSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';
    private const int TimeoutMs = 1500;
    private const int StartTimeoutMs = 10000;

    private readonly string _helperPath;
    private readonly string? _profileDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Reading, IReadOnlyList<string> Candidates)>?> _conversions = new(StringComparer.Ordinal);
    private Process? _process;
    private int _failures;

    // 要る DLL が無くて起動できない (Visual C++ のランタイムが無いなど)。起動し直しても同じなので、もう試さない
    private bool _missingDependency;

    // Windows: 子プロセスを起動するあいだ、DLL が見つからないときのエラーの画面を出さない (子プロセスはこの設定を引き継ぐ)。
    // 子プロセスが引き継ぐのはプロセス全体の設定だけなので、SetThreadErrorMode では効かない。起動したらすぐ戻す
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    // STATUS_DLL_NOT_FOUND / STATUS_ENTRYPOINT_NOT_FOUND / STATUS_INVALID_IMAGE_FORMAT (起動のときに DLL を読み込めなかった)
    private static bool IsLoaderFailure(int exitCode) => exitCode is unchecked((int)0xC0000135) or unchecked((int)0xC0000139) or unchecked((int)0xC000007B);

    /// <param name="helperPath">meltype_mozc_helper の実行ファイル。</param>
    /// <param name="profileDirectory">Mozc の学習データの保存先 (null なら Mozc の既定)。</param>
    public MozcConverter(string helperPath, string? profileDirectory)
    {
        _helperPath = helperPath;
        _profileDirectory = profileDirectory;
    }

    /// <summary>ヘルパーがあるか (起動できるかは使ってみるまで分からない)。</summary>
    public bool IsInstalled => File.Exists(_helperPath);

    /// <summary>使えるか。何度も失敗したら諦める (毎回起動し直して入力が遅くなるのを避ける)。</summary>
    public bool IsAvailable => IsInstalled && _failures < 3 && !_missingDependency;

    public string? Convert(string hiragana) =>
        ConvertClauses(hiragana) is { Count: > 0 } clauses ? string.Concat(clauses.Select(c => c.Text)) : null;

    public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null)
    {
        if (Request(context ?? "", hiragana) is not { Count: > 0 } segments) return null;
        return segments.Select(s => new ConversionClause(s.Reading, s.Candidates.FirstOrDefault() ?? s.Reading)).ToList();
    }

    /// <summary>読みの変換候補 (多い順)。直前の変換で同じ読みの文節があればその候補、無ければその読みだけで変換する。</summary>
    public IReadOnlyList<string> Candidates(string reading)
    {
        lock (_gate)
        {
            if (_candidates.TryGetValue(reading, out var cached)) return cached;
        }
        var segments = Request("", reading);
        if (segments is null) return [];
        // 1 文節ならその候補。複数の文節に分かれたなら、最初の候補をつなげたものだけ。
        return segments.Count == 1 ? segments[0].Candidates : [string.Concat(segments.Select(s => s.Candidates.FirstOrDefault() ?? s.Reading))];
    }

    // 学習データを保存する間隔 (覚えさせた回数)。Meltype を終了するときにも保存する。
    private const int SaveEvery = 5;
    private int _learnedSinceSave;

    /// <summary>確定した文節を Mozc に覚えさせる (Mozc の候補に無い文字列の文節があれば覚えない)。</summary>
    public void Learn(string? context, IReadOnlyList<ConversionClause> clauses)
    {
        if (clauses.Count == 0 || !IsAvailable) return;
        var records = string.Join(RecordSeparator, clauses.Select(c => Clean(c.Reading).Replace(UnitSeparator, ' ') + UnitSeparator + Clean(c.Text).Replace(UnitSeparator, ' ')));
        lock (_gate)
        {
            // 覚えさせると候補の順番が変わるので、候補の控えを捨てる。
            _candidates.Clear();
            _conversions.Clear();
            var result = Send($"L\t{Clean(context ?? "")}\t{records}");
            if (result == "OK" && ++_learnedSinceSave >= SaveEvery)
            {
                _learnedSinceSave = 0;
                Send("S");
            }
        }
    }

    /// <summary>1 行送って 1 行受け取る (_gate の中で呼ぶ)。失敗したら null。</summary>
    private string? Send(string request)
    {
        try
        {
            var process = Start();
            process.StandardInput.Write(request + "\n");
            process.StandardInput.Flush();
            var line = ReadLine(process, TimeoutMs);
            if (line is null)
            {
                Diagnostics.Log.Warn($"Mozc が応答しませんでした ({TimeoutMs}ms)。");
                Fail();
                return null;
            }
            _failures = 0;
            return line;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Mozc を使えませんでした: {ex.Message}");
            Fail();
            return null;
        }
    }

    private List<(string Reading, IReadOnlyList<string> Candidates)>? Request(string context, string reading)
    {
        if (string.IsNullOrEmpty(reading) || !IsAvailable) return null;
        context = Clean(context);
        reading = Clean(reading);
        lock (_gate)
        {
            // ライブ変換は 1 キーごとに同じ読みを何度も変換し直すので、同じ頼み方の結果は使い回す (覚えさせたら捨てる)。
            var key = context + "\t" + reading;
            if (_conversions.TryGetValue(key, out var known))
            {
                // 候補の控えは「直前の変換」のものを使うので、使い回したときも控えを入れ直す
                foreach (var (segmentReading, candidates) in known ?? []) _candidates[segmentReading] = candidates;
                return known;
            }
            try
            {
                var line = Send($"C\t{context}\t{reading}");
                if (line is null) return null;
                if (line.Length == 0) return Remember(key, null);
                var segments = new List<(string, IReadOnlyList<string>)>();
                foreach (var record in line.Split(RecordSeparator))
                {
                    var fields = record.Split(UnitSeparator);
                    if (fields[0].Length == 0) continue;
                    var candidates = fields.Skip(1).Where(c => c.Length > 0).Distinct().ToList();
                    segments.Add((fields[0], candidates));
                    if (_candidates.Count > 512) _candidates.Clear();
                    _candidates[fields[0]] = candidates;
                }
                return Remember(key, segments);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn($"Mozc を使えませんでした: {ex.Message}");
                Fail();
                return null;
            }
        }
    }

    /// <summary>候補の控えと変換の結果の控えを捨てる (前の入力の控えを次の入力に持ち越さない。テストで文ごとに使う)。</summary>
    public void ClearCache()
    {
        lock (_gate)
        {
            _candidates.Clear();
            _conversions.Clear();
        }
    }

    /// <summary>変換の結果を覚えておく (_gate の中で呼ぶ)。</summary>
    private List<(string Reading, IReadOnlyList<string> Candidates)>? Remember(string key, List<(string Reading, IReadOnlyList<string> Candidates)>? segments)
    {
        if (_conversions.Count > 512) _conversions.Clear();
        _conversions[key] = segments;
        return segments;
    }

    // タブ・改行は区切りに使うので空白にする。
    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private Process Start()
    {
        if (_process is { HasExited: false } running) return running;
        _process?.Dispose();
        var info = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(_helperPath) ?? "",
        };
        if (_profileDirectory is not null)
        {
            Directory.CreateDirectory(_profileDirectory);
            info.ArgumentList.Add(_profileDirectory);
        }
        Process process;
        // 今の設定に足す (GetErrorMode で読む。SetErrorMode(0) で読むと、一瞬プロセス全体の設定が 0 になる)
        var previousMode = 0u;
        if (OperatingSystem.IsWindows())
        {
            previousMode = GetErrorMode();
            SetErrorMode(previousMode | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
        }
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("起動できませんでした");
        }
        finally
        {
            if (OperatingSystem.IsWindows()) SetErrorMode(previousMode);
        }
        // Mozc のログ (標準エラー) は読み捨てる (溜まるとヘルパーが止まる)。
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var ready = ReadLine(process, StartTimeoutMs);
        if (ready != "READY")
        {
            if (process.WaitForExit(1000) && IsLoaderFailure(process.ExitCode))
            {
                _missingDependency = true;
                Diagnostics.Log.Warn($"Mozc の変換ヘルパーを起動できません (DLL を読み込めない: 0x{process.ExitCode:X8}。同梱の Visual C++ のランタイムが無いか壊れている)。Microsoft IME だけで変換します。");
            }
            try { process.Kill(); } catch { }
            process.Dispose();
            throw new InvalidOperationException($"起動に失敗しました: {ready ?? "応答なし"}");
        }
        Diagnostics.Log.Info("Mozc の変換エンジンを起動しました。");
        _process = process;
        return process;
    }

    private static string? ReadLine(Process process, int timeoutMs)
    {
        var task = process.StandardOutput.ReadLineAsync();
        return task.Wait(timeoutMs) ? task.Result : null;
    }

    private void Fail()
    {
        _failures++;
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch
        {
        }
        _process?.Dispose();
        _process = null;
    }

    /// <summary>起動しておく (最初の変換で待たないように)。</summary>
    public void WarmUp() => ThreadPool.QueueUserWorkItem(_ => Request("", "あ"));

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false } process)
                {
                    process.StandardInput.Write("Q\n");
                    process.StandardInput.Flush();
                    if (!process.WaitForExit(1000)) process.Kill();
                }
            }
            catch
            {
            }
            _process?.Dispose();
            _process = null;
        }
    }
}
