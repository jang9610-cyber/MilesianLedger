param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = Get-LedgerVersion $repoRoot
$distRoot = Join-Path $repoRoot 'dist'
$archivePath = Join-Path $distRoot ('MilesianLedger-v' + $version + '.zip')
if ((Test-Path -LiteralPath $archivePath) -and -not $Force) { throw 'This release ZIP exists. Use -Force to replace this generated archive.' }
# Package a fresh build from static source files; never package the user's running copy.
$packageRoot = Join-Path $repoRoot ('artifacts/package/' + [guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $packageRoot 'MilesianLedger'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $appRoot
$expected = @(Get-LedgerRuntimeFiles $repoRoot)
$actual = @(Get-ChildItem -LiteralPath $appRoot -Recurse -File | ForEach-Object { $_.FullName.Substring($appRoot.Length + 1).Replace('\', '/') })
if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw 'Unexpected files in the release staging folder.' }
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$temporaryZip = Join-Path $repoRoot ('artifacts/package/' + [guid]::NewGuid().ToString('N') + '.zip')
$zipOutput = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $expected) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipOutput, (Join-Path $appRoot $relative), ('MilesianLedger/' + $relative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zipOutput.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
try {
    $entries = @($archive.Entries | Where-Object { $_.Name })
    $expectedEntries = @($expected | ForEach-Object { 'MilesianLedger/' + $_ })
    if (@(Compare-Object ($expectedEntries | Sort-Object) ($entries.FullName | Sort-Object)).Count -ne 0) { throw 'Release ZIP inventory mismatch.' }
} finally { $archive.Dispose() }
Copy-Item -LiteralPath $temporaryZip -Destination $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archivePath + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archivePath) + [Environment]::NewLine, [Text.Encoding]::ASCII)
Write-Output ('Packaged ' + $archivePath + ' (' + $actual.Count + ' runtime files; no player profile or API key).')
