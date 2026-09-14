$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskFramework = Get-LedgerFramework
$taskOutput = Join-Path $taskRepo ('artifacts/pip-capture-hotkey/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskRunner = Join-Path $taskOutput 'PipCaptureHotkeyVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskRunner))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
$taskArgs += Join-Path $taskRepo 'src/PipCaptureHotkey.cs'
$taskArgs += Join-Path $taskRepo 'tests/PipCaptureHotkeyVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'PIP capture hotkey verification compilation failed.' }
# Registration is replaced by a fake. Fixture HWNDs stay hidden; no keyboard input is generated.
$taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskOutput -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
$taskProcess.Handle | Out-Null
if (-not $taskProcess.WaitForExit(30000)) { $taskProcess.Kill(); throw 'PIP capture hotkey verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.txt')
if ($taskProcess.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.txt')
    throw ('PIP capture hotkey verification failed. Artifacts: ' + $taskOutput)
}
Write-Output ('PIP capture hotkey artifacts: ' + $taskOutput)
