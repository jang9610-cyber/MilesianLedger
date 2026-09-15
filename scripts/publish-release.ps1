$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = Get-LedgerVersion $taskRoot
$taskTag = 'v' + $taskVersion
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RELEASE_TAG -ne $taskTag) { throw 'Release publishing requires a matching tag in GitHub Actions.' }
$taskHead = (git rev-parse HEAD).Trim()
$taskTagged = (git rev-parse ($taskTag + '^{commit}')).Trim()
if ($LASTEXITCODE -ne 0 -or $taskHead -ne $taskTagged -or $taskHead -ne $env:RELEASE_COMMIT) { throw 'The checked-out source must match the release tag and run commit.' }
$taskZip = Join-Path $taskRoot ('dist/MilesianLedger-' + $taskTag + '.zip')
$taskChecksum = $taskZip + '.sha256'
$taskDigest = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath $taskChecksum -Raw).Split(' ')[0] -ne $taskDigest) { throw 'Package checksum mismatch.' }
$taskNotes = [IO.File]::ReadAllText((Join-Path $taskRoot ('docs/releases/' + $taskTag + '.md')))
$taskNotes += "`n`nBuild source: ``$taskHead```n`n[GitHub Actions build and tests]($env:RELEASE_RUN_URL)`n"
$taskBodyFile = Join-Path $taskRoot 'artifacts/release-body.md'
[IO.File]::WriteAllText($taskBodyFile, $taskNotes, (New-Object Text.UTF8Encoding($false)))
# Resume only a draft created by a previous interrupted run. Never replace public assets.
$taskReleases = gh api --paginate "repos/$env:GH_REPO/releases?per_page=100" --jq '.[] | [.tag_name, .draft] | @tsv'
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing releases.' }
$taskExisting = @($taskReleases | Where-Object { $_.Split([char]9)[0] -eq $taskTag })
if ($taskExisting.Count -gt 0 -and $taskExisting[0].Split([char]9)[1] -ne 'true') { throw 'This release is already public. Use a new version.' }
if ($taskExisting.Count -eq 0) {
    gh release create $taskTag --verify-tag --draft --title ('Milesian Ledger ' + $taskTag) --notes-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create draft release.' }
}
gh release upload $taskTag $taskZip $taskChecksum --clobber
if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed; release remains a draft.' }
$taskAssets = gh release view $taskTag --json assets --jq '.assets' | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or @($taskAssets).Count -ne 2) { throw 'Unexpected release assets; release remains a draft.' }
foreach ($taskFile in @($taskZip, $taskChecksum)) {
    $taskAsset = $taskAssets | Where-Object { $_.name -eq [IO.Path]::GetFileName($taskFile) }
    if (-not $taskAsset -or $taskAsset.size -ne (Get-Item -LiteralPath $taskFile).Length) { throw 'Asset size mismatch.' }
}
# Download the actual uploaded files before publication and compare both hashes.
$taskCheckDir = Join-Path $taskRoot ('artifacts/release-download-' + [Guid]::NewGuid().ToString('N'))
gh release download $taskTag --dir $taskCheckDir
if ($LASTEXITCODE -ne 0) { throw 'Release download verification failed.' }
foreach ($taskFile in @($taskZip, $taskChecksum)) {
    $taskDownloaded = Join-Path $taskCheckDir ([IO.Path]::GetFileName($taskFile))
    if ((Get-FileHash $taskFile).Hash -ne (Get-FileHash $taskDownloaded).Hash) { throw 'Uploaded file hash mismatch.' }
}
$taskPrerelease = if ($taskVersion.Contains('-')) { 'true' } else { 'false' }
gh release edit $taskTag --draft=false ("--prerelease=" + $taskPrerelease) --latest=false --notes-file $taskBodyFile
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
