# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 MuNeNICK

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('meltype-setup-test-' + [guid]::NewGuid())
$fixture = Join-Path $testRoot "app with spaces and 'quotes'"
$originalTemp = $env:TEMP
$powershell = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\packaging\setup-ime.ps1') -Destination $fixture
    $env:TEMP = $testRoot
    $helper = Join-Path $fixture 'meltype-ime.ps1'
    $log = Join-Path $testRoot 'Meltype-setup-ime.log'
    foreach ($case in @(
        @{ Ask = $true; Registered = '$false'; Result = '$true' },
        @{ Ask = $true; Registered = '$false'; Result = '$false' },
        @{ Ask = $true; Registered = '$true'; Result = '$true' },
        @{ Ask = $false; Registered = '$true'; Result = '$true' },
        @{ Ask = $false; Registered = '$false'; Result = '$true' }
    )) {
        $stub = @'
function Test-MeltypeImeRegistered { return REGISTERED }
function Install-MeltypeIme([string]$Source, [switch]$Ask) {
    if ([bool]$Ask -ne EXPECTEDASK) { throw 'Incorrect -Ask value' }
    if ($Source -ne (Join-Path $PSScriptRoot 'tip')) { throw 'Incorrect source directory' }
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'called') -Value 'called'
    Write-Host 'Registration attempted'
    return RESULT
}
'@
        $expectedAsk = if ($case.Ask) { '$true' } else { '$false' }
        Set-Content -LiteralPath $helper -Value $stub.Replace('RESULT', $case.Result).Replace('REGISTERED', $case.Registered).Replace('EXPECTEDASK', $expectedAsk) -Encoding UTF8
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $fixture 'setup-ime.ps1'))
        if ($case.Ask) { $arguments += '-Ask' }
        & $powershell @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Registration interrupted setup' }
        $expectedCall = $case.Ask -or $case.Registered -eq '$true'
        $called = Join-Path $fixture 'called'
        if ((Test-Path -LiteralPath $called) -ne $expectedCall) { throw 'Incorrect registration decision' }
        $text = Get-Content -LiteralPath $log -Raw
        if ($expectedCall -and $text -notmatch 'Registration attempted') { throw 'Registration output was not logged' }
        if ($case.Result -eq '$false' -and $text -notmatch 'Meltype キーボード方式') { throw 'Missing fallback message' }
        if ($expectedCall) { Remove-Item -LiteralPath $called }
        Write-Host "PASS: Ask=$($case.Ask), Registered=$($case.Registered), Result=$($case.Result)"
    }
    Set-Content -LiteralPath $helper -Value "throw 'Registration fixture failure'" -Encoding UTF8
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'setup-ime.ps1')
    if ($LASTEXITCODE -ne 1 -or (Get-Content -LiteralPath $log -Raw) -notmatch 'Registration fixture failure') {
        throw 'Unexpected registration error was not reported'
    }
    Write-Host 'PASS: unexpected registration error is logged with exit code 1'
    Remove-Item -LiteralPath $helper
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'setup-ime.ps1')
    if ($LASTEXITCODE -ne 1) { throw 'Missing registration helper was not reported' }
    Write-Host 'PASS: missing registration helper is reported'
    # The last child intentionally failed; do not propagate its exit code to CI.
    $global:LASTEXITCODE = 0
}
finally {
    $env:TEMP = $originalTemp
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRoot) -notlike 'meltype-setup-test-*') {
        throw 'Refusing to remove an unexpected test directory'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
