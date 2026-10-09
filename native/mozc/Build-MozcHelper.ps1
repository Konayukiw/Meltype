# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Yukishiro
#
# Mozc の変換ヘルパー (meltype_mozc_helper.exe) をビルドして native\mozc\bin に置く。
# Build-Package.ps1 / Install-Meltype.ps1 は、bin にヘルパーがあれば Meltype に同梱する (無ければ Microsoft IME だけで動く)。
# GitHub Actions (.github/workflows/build.yml の mozc) でも同じスクリプトでビルドする。
#
# 必要なもの (Mozc の docs/build_mozc_in_windows.md と同じ):
#   Visual Studio 2022 (「C++ によるデスクトップ開発」、Windows 11 SDK、C++ ATL)、Python 3.12 以降、Bazelisk、Git
#
#   .\native\mozc\Build-MozcHelper.ps1 -Python C:\tools\python\python.exe -Bazelisk C:\tools\bazelisk.exe
param(
    # Mozc のソースを置く場所 (無ければ取得する)。Windows のパスの長さの制限があるので短い場所がよい。
    [string]$MozcSource = (Join-Path $env:USERPROFILE 'mozc'),
    [string]$Python = 'python',
    [string]$Bazelisk = 'bazelisk',
    # Visual Studio の VC フォルダー。省略すると vswhere で探す (Mozc の自動検出は一部の構成で失敗するため)。
    [string]$VcPath = '',
    # Bazel の結果を保存しておく場所 (GitHub Actions でビルドを速くするため)。省略すると使わない。
    [string]$DiskCache = ''
)
$ErrorActionPreference = 'Stop'

# 外部のプログラムを実行する。進み具合を標準エラーに出すもの (git・Bazel) を、Windows PowerShell 5.1 が
# エラーとして扱って止まらないようにし、終了コードで成否を見る。
function Invoke-Native([string]$FilePath, [string[]]$Arguments, [string]$Failure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}
$here = $PSScriptRoot
$bin = Join-Path $here 'bin'
# ビルドする Mozc の版 (動作を確かめた commit に固定する)
$commit = (Get-Content -Raw -LiteralPath (Join-Path $here 'MOZC_COMMIT')).Trim()

if (-not $VcPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio (C++) が見つかりません。' }
    $VcPath = Join-Path $vs 'VC'
}

# ヘルパーが使う Visual C++ のランタイム (MSVCP140.dll など) は、ビルドに使った Visual Studio から横に置く。
# 「Visual C++ 再頒布可能パッケージ」が入っていない PC でも動くように (Microsoft が、アプリと一緒に配ることを認めているファイル)。
# ビルドに使った Visual Studio に入っている、いちばん新しいランタイムにする (古い版のランタイムでは、新しい版でビルドしたヘルパーが落ちることがある。
# ランタイムとビルドのツールは版の番号が一致しないので、番号では合わせない)。
# どの DLL が要るかは、ビルドしたヘルパーを dumpbin で調べて決める (Mozc やビルドの設定で変わるので決め打ちしない)。
# 無ければ配れないので、時間のかかるビルドの前に確かめて止める
# Redist\MSVC\<版>\x64\Microsoft.VC*.CRT なので、2 つ上のフォルダーの名前が版
$crt = Get-ChildItem (Join-Path $VcPath 'Redist\MSVC\*\x64\Microsoft.VC*.CRT') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { $v = $null; if ([version]::TryParse($_.Parent.Parent.Name, [ref]$v)) { $v } else { [version]'0.0' } } -Descending |
    Select-Object -First 1
if (-not $crt -or -not (Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' -ErrorAction SilentlyContinue)) {
    throw "Visual C++ のランタイム (Redist\MSVC) が見つかりません ($VcPath)。Visual Studio Installer で「C++ によるデスクトップ開発」を入れ直してください。"
}
# Tools\MSVC\<版>\bin\Hostx64\x64\dumpbin.exe (いちばん新しいツール)
$dumpbin = Get-ChildItem (Join-Path $VcPath 'Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe') -ErrorAction SilentlyContinue |
    Sort-Object { $v = $null; if ([version]::TryParse($_.Directory.Parent.Parent.Parent.Name, [ref]$v)) { $v } else { [version]'0.0' } } -Descending |
    Select-Object -First 1
if (-not $dumpbin) { throw "dumpbin.exe が見つかりません ($VcPath)。Visual Studio Installer で「C++ によるデスクトップ開発」を入れ直してください。" }

# src の有無ではなく .git で見る (GitHub Actions のキャッシュが src\third_party_cache だけを先に戻すため)。
if (-not (Test-Path (Join-Path $MozcSource '.git'))) {
    New-Item -ItemType Directory -Force -Path $MozcSource | Out-Null
    Invoke-Native git @('-C', $MozcSource, 'init', '-q') 'git init に失敗しました。'
    Invoke-Native git @('-C', $MozcSource, 'remote', 'add', 'origin', 'https://github.com/google/mozc.git') 'git remote に失敗しました。'
    Invoke-Native git @('-C', $MozcSource, 'fetch', '-q', '--depth', '1', 'origin', $commit) "Mozc ($commit) を取得できませんでした。"
    Invoke-Native git @('-C', $MozcSource, 'checkout', '-q', 'FETCH_HEAD') 'git checkout に失敗しました。'
    Invoke-Native git @('-C', $MozcSource, 'submodule', 'update', '-q', '--init', '--recursive', '--depth', '1') 'Mozc のサブモジュールを取得できませんでした。'
}
$src = Join-Path $MozcSource 'src'

# ヘルパーのソースと BUILD の定義を Mozc の converter パッケージに入れる (エンジンを使えるのがこのパッケージのため)。
Copy-Item -LiteralPath (Join-Path $here 'meltype_mozc_helper.cc') -Destination (Join-Path $src 'converter') -Force
$build = Join-Path $src 'converter\BUILD.bazel'
if (-not (Select-String -LiteralPath $build -Pattern 'name = "meltype_mozc_helper"' -Quiet)) {
    Add-Content -LiteralPath $build -Value ("`n" + (Get-Content -Raw -LiteralPath (Join-Path $here 'BUILD.fragment'))) -Encoding utf8
}

Push-Location $src
try {
    # LLVM・MSYS2・Ninja (Qt・WiX・Android NDK は使わない)
    Invoke-Native $Python @('build_tools/update_deps.py', '--noqt', '--nowix', '--nondk') '依存関係を取得できませんでした。'
    $env:BAZEL_VC = $VcPath
    $options = @('build', '//converter:meltype_mozc_helper', '--config', 'release_build')
    if ($DiskCache) { $options += "--disk_cache=$DiskCache" }
    # シンボリックリンクは Windows の開発者モードか管理者権限が要るので使わない。
    Invoke-Native $Bazelisk (@('--nowindows_enable_symlinks') + $options) 'ビルドに失敗しました。'
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item -LiteralPath (Join-Path $src 'bazel-bin\converter\meltype_mozc_helper.exe') -Destination $bin -Force
# Bazel の出力は読み取り専用なので、上書きやアンインストールで困らないように外す。
(Get-Item -LiteralPath (Join-Path $bin 'meltype_mozc_helper.exe')).IsReadOnly = $false
# Mozc と辞書 (IPAdic など)・ライブラリのライセンス
Copy-Item -LiteralPath (Join-Path $src 'data\installer\credits_en.html') -Destination (Join-Path $bin 'MOZC-CREDITS.html') -Force
Copy-Item -LiteralPath (Join-Path $MozcSource 'LICENSE') -Destination (Join-Path $bin 'MOZC-LICENSE.txt') -Force
# ヘルパーが読み込む DLL のうち、Visual C++ のランタイムにあるものを横に置く。置いた DLL がさらに読み込むものも辿る。
# ランタイムにも Windows にも無い DLL を使っていたら、配っても動かないので止める
$helper = Join-Path $bin 'meltype_mozc_helper.exe'
$system = [Environment]::SystemDirectory
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$placed = New-Object 'System.Collections.Generic.List[string]'
$queue = New-Object 'System.Collections.Generic.Queue[string]'
$queue.Enqueue($helper)
while ($queue.Count -gt 0) {
    $file = $queue.Dequeue()
    $dependents = & $dumpbin.FullName /nologo /dependents $file | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[\w.-]+\.dll$' }
    if ($LASTEXITCODE -ne 0) { throw "dumpbin で $file を調べられませんでした。" }
    foreach ($dll in $dependents) {
        if (-not $seen.Add($dll)) { continue }
        # 名前の大文字・小文字は、ランタイムのファイルに合わせる (dumpbin は大文字で出すことがある)
        $runtime = Get-ChildItem -LiteralPath $crt.FullName -Filter $dll -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($runtime) {
            Copy-Item -LiteralPath $runtime.FullName -Destination $bin -Force
            $placed.Add($runtime.Name)
            $queue.Enqueue($runtime.FullName)
        }
        elseif ($dll -notlike 'api-ms-win-*' -and $dll -notlike 'ext-ms-*' -and -not (Test-Path -LiteralPath (Join-Path $system $dll))) {
            throw "ヘルパーが使う $dll が、Visual C++ のランタイム ($($crt.FullName)) にも Windows にもありません。"
        }
    }
}
if ($placed.Count -eq 0) { throw 'ヘルパーが Visual C++ のランタイムを使っていません (dumpbin の出力を読めなかった可能性があります)。' }
# 置いたランタイムの一覧 (Build-Package.ps1 が、全部そろっているかを確かめるのに使う)
Set-Content -LiteralPath (Join-Path $bin 'VC-RUNTIME.txt') -Value $placed -Encoding ASCII
Write-Host "Visual C++ のランタイムを置きました ($($crt.FullName)): $($placed -join ', ')"
Write-Host "作成しました: $bin"
