param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js 22 or newer is required for server verification. Desktop builds do not require Node.js.' }
$tests = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'server/test') -File -Filter '*.test.mjs' | Sort-Object Name)
if ($tests.Count -eq 0) { throw 'No server tests found.' }
& $node.Source --test @($tests.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Offline server verification failed.' }
Write-Output 'Server verification passed. No real Nexon API requests were made.'
