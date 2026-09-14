param([switch]$SkipBuild, [string]$ApplicationDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $taskRepo 'build.ps1') }
$taskFramework = Get-LedgerFramework
$taskSource = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $taskRepo 'dist/MilesianLedger' }
$taskOutput = Join-Path $taskRepo ('artifacts/local-ocr/' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskOutput 'runtime'
New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskSource 'MilesianLedger.exe') -Destination $taskRuntime
$taskRunner = Join-Path $taskRuntime 'LocalOcrVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskRunner), ('/reference:' + (Join-Path $taskRuntime 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
$taskArgs += Join-Path $taskRepo 'tests/LocalOcrVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Local OCR verification compilation failed.' }
# This runner creates its own Korean text bitmaps. It does not capture the user's desktop.
$taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskRuntime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
$taskProcess.Handle | Out-Null
if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); throw 'Local OCR verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.txt')
if ($taskProcess.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.txt')
    throw ('Local OCR verification failed. Artifacts: ' + $taskOutput)
}
Write-Output ('Local OCR artifacts: ' + $taskOutput)
