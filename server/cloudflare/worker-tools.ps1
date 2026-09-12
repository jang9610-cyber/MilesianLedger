param(
    [Parameter(Position = 0)]
    [ValidateSet('check', 'login', 'deploy', 'test', 'dev')]
    [string]$Command = 'check'
)
$ErrorActionPreference = 'Stop'
$taskExitCode = 1
$previousPath = $env:PATH
$previousLogPath = $env:WRANGLER_LOG_PATH
$previousMetrics = $env:WRANGLER_SEND_METRICS

try {
    $nodeCandidates = @()
    $installedNode = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($installedNode) { $nodeCandidates += $installedNode.Source }
    if ($env:USERPROFILE) {
        $nodeCandidates += Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
    }
    $taskNode = $null
    foreach ($candidate in ($nodeCandidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $versionText = & $candidate --version
        if ($LASTEXITCODE -eq 0 -and $versionText -match '^v(\d+)\.' -and [int]$Matches[1] -ge 22) {
            $taskNode = $candidate
            break
        }
    }
    if (-not $taskNode) {
        throw '사용 가능한 Node.js 22 이상을 찾지 못했습니다. Node.js LTS를 설치한 뒤 다시 실행해 주세요.'
    }
    $wranglerPath = Join-Path $PSScriptRoot 'node_modules/wrangler/bin/wrangler.js'
    if ($Command -ne 'test' -and -not (Test-Path -LiteralPath $wranglerPath -PathType Leaf)) {
        throw 'Wrangler 파일이 없습니다. 준비된 cloudflare-worker 폴더에서 실행해 주세요. 새 PC에서는 먼저 npm install이 필요합니다.'
    }

    # Process-local settings only; never alter the user's permanent PATH or credentials.
    $env:PATH = (Split-Path -Parent $taskNode) + [IO.Path]::PathSeparator + $previousPath
    $env:WRANGLER_LOG_PATH = Join-Path $PSScriptRoot '.wrangler/logs'
    $env:WRANGLER_SEND_METRICS = 'false'
    Push-Location -LiteralPath $PSScriptRoot
    try {
        Write-Output ('실행: ' + $Command + ' · npm/npx 없이 Node.js를 직접 사용합니다.')
        switch ($Command) {
            'check' { & $taskNode $wranglerPath deploy --dry-run --config wrangler.jsonc --outdir .dry-run }
            'login' { & $taskNode $wranglerPath login }
            'deploy' { & $taskNode $wranglerPath deploy --config wrangler.jsonc }
            'test' { & $taskNode --test worker.test.mjs market.test.mjs }
            'dev' { & $taskNode $wranglerPath dev --config wrangler.jsonc }
        }
        $taskExitCode = $LASTEXITCODE
    } finally { Pop-Location }
} catch {
    Write-Host ('실행하지 못했습니다: ' + $_.Exception.Message) -ForegroundColor Red
    $taskExitCode = 1
} finally {
    $env:PATH = $previousPath
    $env:WRANGLER_LOG_PATH = $previousLogPath
    $env:WRANGLER_SEND_METRICS = $previousMetrics
}
exit $taskExitCode
