$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$framework = Get-LedgerFramework
$runRoot = Join-Path $repoRoot ('artifacts/live-auction/' + [guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $runRoot 'app'
Write-Output 'Live test: queries every supported auction item through the configured public proxy. Uses real server quota.'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $appRoot
$runner = Join-Path $appRoot 'AuctionLiveVerificationRunner.exe'
$compilerArgs = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + $runner), ('/reference:' + (Join-Path $appRoot 'MilesianLedger.exe')))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll')) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
$compilerArgs += Join-Path $repoRoot 'tests/AuctionLiveVerificationRunner.cs'
& (Join-Path $framework 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Live verification runner compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw ('Live auction verification failed. Inspect ' + (Join-Path $runRoot 'report.json')) }
