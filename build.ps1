param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts/build.ps1') -OutputDirectory $OutputDirectory
