param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $taskRepo 'build.ps1') }
$taskFramework = Get-LedgerFramework
$taskApp = Join-Path $taskRepo 'dist/MilesianLedger'
$taskRunner = Join-Path $taskApp 'MarketUiVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + $taskRunner), ('/reference:' + (Join-Path $taskApp 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll','System.Core.dll','System.Xaml.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
$taskArgs += Join-Path $taskRepo 'tests/MarketUiVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Market UI verification compilation failed.' }
$taskOutput = Join-Path $taskRepo 'artifacts/market-ui'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskConfig = Join-Path $taskApp 'data/auction-proxy.json'
$taskOriginal = [IO.File]::ReadAllBytes($taskConfig)
try {
    $taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskApp -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
    $taskProcess.Handle | Out-Null
    if (-not $taskProcess.WaitForExit(45000)) { $taskProcess.Kill(); throw 'Market UI verification timed out.' }
    Get-Content (Join-Path $taskOutput 'stdout.txt')
    if ($taskProcess.ExitCode -ne 0) { Get-Content (Join-Path $taskOutput 'stderr.txt'); throw 'Market UI verification failed.' }
} finally { [IO.File]::WriteAllBytes($taskConfig, $taskOriginal) }
