param([switch]$IncludeUi)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$framework = Get-LedgerFramework
$qaRoot = Join-Path $repoRoot ('artifacts/verification/' + [guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $qaRoot 'app'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $appRoot
$runners = @('CoreVerificationRunner')
if ($IncludeUi) { $runners += @('MaterialSortingRunner', 'AuctionProxyUiRunner') }
foreach ($runnerName in $runners) {
    $runnerPath = Join-Path $appRoot ($runnerName + '.exe')
    $compilerArgs = @('/nologo', '/target:exe', '/codepage:65001', ('/main:' + $runnerName), ('/out:' + $runnerPath), ('/reference:' + (Join-Path $appRoot 'MilesianLedger.exe')))
    foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll')) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
    $compilerArgs += Join-Path $repoRoot ('tests/' + $runnerName + '.cs')
    & (Join-Path $framework 'csc.exe') @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw ($runnerName + ' compilation failed.') }
    $caseRoot = Join-Path $qaRoot $runnerName
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    $stdout = Join-Path $caseRoot 'stdout.txt'; $stderr = Join-Path $caseRoot 'stderr.txt'
    $process = Start-Process -FilePath $runnerPath -ArgumentList ('"' + $caseRoot + '"') -WorkingDirectory $appRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $process.Handle | Out-Null
    $process.WaitForExit()
    Get-Content -LiteralPath $stdout
    if ($process.ExitCode -ne 0) { Get-Content -LiteralPath $stderr; throw ($runnerName + ' failed. Reports: ' + $caseRoot) }
}
Write-Output ('Verification passed. Offline reports: ' + $qaRoot)
