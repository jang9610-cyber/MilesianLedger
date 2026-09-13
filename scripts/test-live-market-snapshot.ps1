# Run explicitly from the repository root:
# powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-live-market-snapshot.ps1
# Reads the public manifest and compressed common data only. Does not call
# administrator endpoints, use secrets, or request individual Nexon items.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskFramework = Get-LedgerFramework
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot ('artifacts/market-live-snapshot/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskRunner = Join-Path $taskOutput 'MarketSnapshotLiveVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + $taskRunner))
foreach ($taskReference in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
foreach ($taskSource in @('src/MarketSnapshot.cs','src/AuctionCore.cs','src/AuctionProxyConfig.cs','tests/MarketSnapshotLiveVerificationRunner.cs')) {
    $taskArgs += Join-Path $taskRoot $taskSource
}
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Live snapshot verification compilation failed.' }
& $taskRunner $taskOutput (Join-Path $taskRoot 'server/item-names.json')
if ($LASTEXITCODE -ne 0) { throw ('Live snapshot verification failed: ' + $taskOutput) }
Write-Output ('Live snapshot report: ' + (Join-Path $taskOutput 'report.json'))
