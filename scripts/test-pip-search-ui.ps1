param([switch]$SkipBuild, [string]$ApplicationDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $taskRepo 'build.ps1') }
$taskFramework = Get-LedgerFramework
$taskSource = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $taskRepo 'dist/MilesianLedger' }
$taskOutput = Join-Path $taskRepo ('artifacts/pip-search-ui/' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskOutput 'runtime'
New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
# A standalone PIP needs only the assembly. Keep all fixtures and settings away
# from packaged profiles, provider configuration, and shared snapshot caches.
Copy-Item -LiteralPath (Join-Path $taskSource 'MilesianLedger.exe') -Destination $taskRuntime
Get-ChildItem -LiteralPath $taskSource -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskRuntime }
$taskRunner = Join-Path $taskRuntime 'PipSearchUiVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + $taskRunner), ('/reference:' + (Join-Path $taskRuntime 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
$taskArgs += Join-Path $taskRepo 'tests/PipSearchUiVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'PIP search UI verification compilation failed.' }
$taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskRuntime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
$taskProcess.Handle | Out-Null
if (-not $taskProcess.WaitForExit(45000)) { $taskProcess.Kill(); throw 'PIP search UI verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.txt')
if ($taskProcess.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.txt')
    throw ('PIP search UI verification failed. Artifacts: ' + $taskOutput)
}
Write-Output ('PIP search UI artifacts: ' + $taskOutput)
