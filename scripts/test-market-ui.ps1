param([switch]$SkipBuild, [string]$ApplicationDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $taskRepo 'build.ps1') }
$taskFramework = Get-LedgerFramework
$taskSource = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $taskRepo 'dist/MilesianLedger' }
$taskOutput = Join-Path $taskRepo ('artifacts/market-ui/' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskOutput 'runtime'
New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
# Only assemblies are copied. Provider configuration, personal profiles, and
# production snapshot caches never enter this isolated local-server fixture.
Copy-Item -LiteralPath (Join-Path $taskSource 'MilesianLedger.exe') -Destination $taskRuntime
Get-ChildItem -LiteralPath $taskSource -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskRuntime }
$taskRunner = Join-Path $taskRuntime 'MarketUiVerificationRunner.exe'
$taskArgs = @('/nologo','/target:exe','/langversion:5','/codepage:65001',('/out:' + $taskRunner),('/reference:' + (Join-Path $taskRuntime 'MilesianLedger.exe')))
foreach ($taskReference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')) { $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference) }
$taskArgs += Join-Path $taskRepo 'tests/MarketUiVerificationRunner.cs'
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Market UI verification compilation failed.' }
$taskProcess = Start-Process -FilePath $taskRunner -ArgumentList ('"' + $taskOutput + '"') -WorkingDirectory $taskRuntime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt') -PassThru
$taskProcess.Handle | Out-Null
if (-not $taskProcess.WaitForExit(45000)) { $taskProcess.Kill(); throw 'Market UI verification timed out.' }
Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.txt')
if ($taskProcess.ExitCode -ne 0) { Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.txt'); throw ('Market UI verification failed: ' + $taskOutput) }
Write-Output ('Market UI artifacts: ' + $taskOutput)
