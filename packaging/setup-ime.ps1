# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 MuNeNICK
#
# setup.exe から、元のユーザーの権限で実行する。
# IME の登録に失敗しても、本体はキーボード方式で使える。

param([switch]$Ask)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'Meltype-setup-ime.log'

try {
    & {
        . (Join-Path $PSScriptRoot 'meltype-ime.ps1')
        # チェックが OFF でも、登録済みの DLL は本体と一緒に更新する。
        if (($Ask -or (Test-MeltypeImeRegistered)) -and
            -not (Install-MeltypeIme -Source (Join-Path $PSScriptRoot 'tip') -Ask:$Ask)) {
            Write-Host 'Meltype IME を登録できませんでした。Meltype キーボード方式は引き続き使えます。'
        }
    } *> $log
}
catch {
    Add-Content -LiteralPath $log -Value "Meltype IME の登録に失敗しました: $($_.Exception.Message)" -Encoding UTF8
    exit 1
}
