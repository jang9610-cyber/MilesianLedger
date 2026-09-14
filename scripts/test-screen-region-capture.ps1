$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFramework = Get-LedgerFramework
$taskOutput = Join-Path $taskRoot ('artifacts/screen-region-capture/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskRunner = Join-Path $taskOutput 'ScreenRegionCaptureVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskRunner))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
foreach ($taskSource in @('src/ScreenRegionCapture.cs', 'tests/ScreenRegionCaptureVerificationRunner.cs')) {
    $taskArgs += Join-Path $taskRoot $taskSource
}
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Screen region capture verification compilation failed.' }
# Synthetic pixels and coordinates only. No desktop capture or input automation.
& $taskRunner 2>&1 | Tee-Object -FilePath (Join-Path $taskOutput 'report.txt')
if ($LASTEXITCODE -ne 0) { throw 'Screen region capture verification failed.' }
Write-Output ('Reports: ' + $taskOutput)
