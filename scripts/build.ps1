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
# Windows supplies the OCR implementation and metadata; no screenshot helper or OCR models are shipped.
$windowsSdkMetadataRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/UnionMetadata'
$windowsMetadata = @(Get-ChildItem -LiteralPath $windowsSdkMetadataRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'Windows.winmd')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1)
if ($windowsMetadata.Count -eq 0) { throw 'Windows 10/11 SDK UnionMetadata is required to compile Windows OCR support. The SDK is not required to run the app.' }
$runtimeFacade = Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_MSIL/System.Runtime/v4.0_4.0.0.0__b03f5f7f11d50a3a/System.Runtime.dll'
foreach ($reference in @((Join-Path $framework 'System.Runtime.WindowsRuntime.dll'), $runtimeFacade)) {
    if (-not (Test-Path -LiteralPath $reference)) { throw ('Windows OCR build reference is missing: ' + $reference) }
    $compilerArgs += '/reference:' + $reference
}
$compilerArgs += '/reference:' + (Join-Path $windowsMetadata[0].FullName 'Windows.winmd')
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
