$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFramework = Get-LedgerFramework
$taskOutput = Join-Path $taskRoot ('artifacts/market-insights-verification/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskRunner = Join-Path $taskOutput 'MarketInsightsVerificationRunner.exe'
$taskArgs = @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $taskRunner))
foreach ($taskReference in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll')) {
    $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference)
}
foreach ($taskSource in @('src/MarketSnapshot.cs','src/MarketInsights.cs','tests/MarketInsightsVerificationRunner.cs')) {
    $taskArgs += Join-Path $taskRoot $taskSource
}
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Market insights verification compilation failed.' }
& $taskRunner $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Market insights verification failed.' }
Write-Output ('Reports: ' + $taskOutput)
