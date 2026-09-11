param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot ('artifacts/allowlist/' + [guid]::NewGuid().ToString('N') + '/app')
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $outputRoot
Add-Type -AssemblyName System.Web.Extensions
[Reflection.Assembly]::LoadFrom((Join-Path $outputRoot 'MilesianLedger.exe')) | Out-Null
$catalog = [MabinogiBarter.Catalog]::Load((Join-Path $outputRoot 'data/barter-data.json'))
$planner = New-Object MabinogiBarter.ProcurementPlanner($catalog)
$names = @($planner.GetAllQuoteNames())
$path = Join-Path $repoRoot 'server/item-names.json'
$workerPath = Join-Path $repoRoot 'server/cloudflare/worker.mjs'
$workerText = [IO.File]::ReadAllText($workerPath, [Text.Encoding]::UTF8)
$workerList = [regex]::Match($workerText, '(?s)const ITEM_NAMES = new Set\((\[.*?\])\);')
if (-not $workerList.Success) { throw 'Cannot find the embedded Cloudflare item allowlist.' }
$workerNames = @([regex]::Matches($workerList.Groups[1].Value, '''([^'']*)''|"((?:\\.|[^"\\])*)"') | ForEach-Object {
    if ($_.Groups[1].Success) { $_.Groups[1].Value } else { ConvertFrom-Json -InputObject $_.Value }
})
if ($Check) {
    [string[]]$existing = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8))
    $expected = [Collections.Generic.HashSet[string]]::new([string[]]$names, [StringComparer]::Ordinal)
    if ($existing.Count -ne $names.Count -or -not $expected.SetEquals([string[]]$existing)) {
        throw 'The server item allowlist differs from the application catalog. Run this script without -Check.'
    }
    if ($workerNames.Count -ne $names.Count -or -not $expected.SetEquals([string[]]$workerNames)) {
        throw 'The Cloudflare item allowlist differs from the application catalog. Run this script without -Check.'
    }
    Write-Output ('Node and Cloudflare item allowlists match the application: ' + $names.Count)
} else {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $jsonNames = ConvertTo-Json -InputObject $names
    [IO.File]::WriteAllText($path, $jsonNames + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $group = $workerList.Groups[1]
    $updatedWorker = $workerText.Substring(0, $group.Index) + $jsonNames + $workerText.Substring($group.Index + $group.Length)
    [IO.File]::WriteAllText($workerPath, $updatedWorker, [Text.UTF8Encoding]::new($false))
    Write-Output ('Updated Node and Cloudflare item allowlists: ' + $names.Count)
}
