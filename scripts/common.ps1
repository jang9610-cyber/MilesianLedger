$ErrorActionPreference = 'Stop'

function Get-LedgerFramework {
    foreach ($relative in @('Microsoft.NET/Framework64/v4.0.30319', 'Microsoft.NET/Framework/v4.0.30319')) {
        $candidate = Join-Path $env:WINDIR $relative
        if (Test-Path -LiteralPath (Join-Path $candidate 'csc.exe')) { return $candidate }
    }
    throw '.NET Framework compiler not found. Install the Windows .NET Framework 4.x components.'
}

function Get-LedgerVersion([string]$RepoRoot) {
    $assembly = [IO.File]::ReadAllText((Join-Path $RepoRoot 'src/AssemblyInfo.cs'))
    $informational = [regex]::Matches($assembly, '(?m)^\s*\[assembly:\s*AssemblyInformationalVersion\s*\(\s*"([^"]*)"\s*\)\s*\]')
    if ($informational.Count -gt 1) { throw 'The application informational version is declared more than once.' }
    if ($informational.Count -eq 1) {
        $version = $informational[0].Groups[1].Value
        # SemVer identifiers also keep the version safe to use in a release filename.
        $identifier = '(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)'
        $semver = '\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-' + $identifier + '(?:\.' + $identifier + ')*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'
        if (-not [regex]::IsMatch($version, $semver)) { throw 'The application informational version must be a valid semantic version.' }
        return $version
    }
    # Older source snapshots have only a numeric file version.
    $match = [regex]::Match($assembly, 'AssemblyFileVersion\s*\(\s*"(\d+\.\d+\.\d+)\.\d+"\s*\)')
    if (-not $match.Success) { throw 'Cannot read the application version.' }
    return $match.Groups[1].Value
}

function Get-LedgerRuntimeFiles([string]$RepoRoot) {
    @('MilesianLedger.exe', '사용방법.txt', 'data/barter-data.json', 'data/item-acquisition.json', 'data/trade-planning.json', 'data/auction-proxy.json')
    foreach ($name in @('manifest.json', 'README.md', 'index.html')) { 'assets/item-icons/' + $name }
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'assets/item-icons/png') -File -Filter '*.png' |
        Sort-Object Name | ForEach-Object { 'assets/item-icons/png/' + $_.Name }
}
