param([switch]$SkipBuild, [string]$ApplicationDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $taskRepo 'build.ps1') }
$taskFramework = Get-LedgerFramework
$taskSource = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $taskRepo 'dist/MilesianLedger' }
$taskOutput = Join-Path $taskRepo ('artifacts/auction-settlement-ui/' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskOutput 'runtime'
New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
# Copy assembly dependencies only; fixtures, reports and appearance settings stay
# in this new artifact directory. Never copy/read packaged profiles or providers.
Copy-Item -LiteralPath (Join-Path $taskSource 'MilesianLedger.exe') -Destination $taskRuntime
Get-ChildItem -LiteralPath $taskSource -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskRuntime }
$taskRunner = Join-Path $taskRuntime 'AuctionSettlementUiVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskRunner), ('/reference:' + (Join-Path $taskRuntime 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
$taskArgs += Join-Path $taskRepo 'tests/AuctionSettlementUiVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Auction settlement UI verification compilation failed.' }
$taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskRuntime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
$taskProcess.Handle | Out-Null
if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); throw 'Auction settlement UI verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.txt')
if ($taskProcess.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.txt')
    throw ('Auction settlement UI verification failed. Artifacts: ' + $taskOutput)
}
Write-Output ('Auction settlement UI artifacts: ' + $taskOutput)

# Exercise notifications from another open view with the same app assembly.
$taskPublicationOutput = Join-Path $taskOutput 'shared-publication'
New-Item -ItemType Directory -Path $taskPublicationOutput -Force | Out-Null
$taskPublicationRunner = Join-Path $taskRuntime 'MarketSnapshotPublicationVerificationRunner.exe'
$taskPublicationArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskPublicationRunner), ('/reference:' + (Join-Path $taskRuntime 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) {
    $taskPublicationArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
$taskPublicationArgs += Join-Path $taskRepo 'tests/MarketSnapshotPublicationVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskPublicationArgs
if ($LASTEXITCODE -ne 0) { throw 'Snapshot publication verification compilation failed.' }
$taskPublicationProcess = Start-Process -FilePath $taskPublicationRunner -ArgumentList ('"' + $taskPublicationOutput + '"') -WorkingDirectory $taskRuntime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskPublicationOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskPublicationOutput 'stderr.txt') -PassThru
$taskPublicationProcess.Handle | Out-Null
if (-not $taskPublicationProcess.WaitForExit(30000)) { $taskPublicationProcess.Kill(); throw 'Snapshot publication verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskPublicationOutput 'stdout.txt')
if ($taskPublicationProcess.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $taskPublicationOutput 'stderr.txt')
    throw ('Snapshot publication verification failed. Artifacts: ' + $taskPublicationOutput)
}
