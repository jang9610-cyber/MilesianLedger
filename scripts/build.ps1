param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot 'dist/MilesianLedger' }
$sourceRoot = Join-Path $repoRoot 'src'
if ($outputRoot.TrimEnd('\', '/') -eq $repoRoot.TrimEnd('\', '/') -or $outputRoot.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot -eq $sourceRoot) {
    throw 'Build into an output folder, not the repository root or src folder.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$framework = Get-LedgerFramework
$compilerArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001', ('/out:' + (Join-Path $outputRoot 'MilesianLedger.exe')))
$icon = Join-Path $repoRoot 'assets/app-icon/barter-helper.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Application icon is missing.' }
$compilerArgs += '/win32icon:' + $icon
$compilerArgs += '/resource:' + $icon + ',MabinogiBarter.AppIcon.ico'
foreach ($number in 1..4) {
    $name = 'frame-{0:00}.png' -f $number
    $frame = Join-Path $repoRoot ('assets/loading-wagon-animation/frames/' + $name)
    if (-not (Test-Path -LiteralPath $frame)) { throw ('Loading frame is missing: ' + $name) }
    $compilerArgs += '/resource:' + $frame + ',MabinogiBarter.Loading.' + $name
}
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Net.Http.dll', 'System.Security.dll', 'System.Xaml.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll', 'WPF/UIAutomationProvider.dll', 'WPF/UIAutomationTypes.dll')) {
    $compilerArgs += '/reference:' + (Join-Path $framework $reference)
}
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -File -Filter '*.cs' | Sort-Object Name)
if ($sources.Count -eq 0) { throw 'No C# sources found.' }
$compilerArgs += $sources.FullName
& (Join-Path $framework 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Application build failed. Close a running copy if the output executable is locked.' }
foreach ($relative in Get-LedgerRuntimeFiles $repoRoot) {
    if ($relative -eq 'MilesianLedger.exe') { continue }
    $source = if ($relative -eq '사용방법.txt') { Join-Path $repoRoot 'docs/USER_GUIDE.md' } else { Join-Path $repoRoot $relative }
    $destination = Join-Path $outputRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
Write-Output ('Built MilesianLedger v' + (Get-LedgerVersion $repoRoot) + ': ' + (Join-Path $outputRoot 'MilesianLedger.exe'))
