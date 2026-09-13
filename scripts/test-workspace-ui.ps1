param([switch]$SkipBuild, [string]$ApplicationDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRepo ('artifacts/workspace-ui/' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskOutput 'runtime'
if ($SkipBuild) {
    $taskSource = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $taskRepo 'dist/MilesianLedger' }
    foreach ($taskRelative in (Get-LedgerRuntimeFiles $taskRepo)) {
        $taskTarget = Join-Path $taskRuntime $taskRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskSource $taskRelative) -Destination $taskTarget
    }
} else { & (Join-Path $taskRepo 'build.ps1') -OutputDirectory $taskRuntime }
$taskFramework = Get-LedgerFramework
$taskRunner = Join-Path $taskRuntime 'WorkspaceUiVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskRunner), ('/reference:' + (Join-Path $taskRuntime 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) { $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference) }
$taskArgs += Join-Path $taskRepo 'tests/WorkspaceUiVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Workspace UI verification compilation failed.' }
$taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskRuntime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
$taskProcess.Handle | Out-Null
if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); throw 'Workspace UI verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.txt')
if ($taskProcess.ExitCode -ne 0) { Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.txt'); throw ('Workspace UI verification failed. Artifacts: ' + $taskOutput) }
Write-Output ('Workspace UI artifacts: ' + $taskOutput)
