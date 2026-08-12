[CmdletBinding()]
param(
    [string[]]$Uri = @(),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$expectedRepository = 'djcdevelopment/lumberjacks-platform'

function Test-IdentityPayload([object]$Payload) {
    return ([string]$Payload.schema -eq 'comfy-repo-identity/v1' -and
        [string]$Payload.repository -eq $expectedRepository -and
        [string]$Payload.service -match '^lumberjacks-(?:gateway|companion)$' -and
        -not [string]::IsNullOrWhiteSpace([string]$Payload.revision) -and
        [string]$Payload.revision -ne 'unknown')
}

$sources = @(
    'Lumberjacks\src\Game.Gateway\Program.cs',
    'Lumberjacks\src\Game.Companion\Program.cs'
)
foreach ($relative in $sources) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $relative) -Raw
    if ($source -notmatch 'MapGet\("/identity"' -or
        $source -notmatch [regex]::Escape($expectedRepository) -or
        $source -notmatch 'comfy-repo-identity/v1') {
        throw "G4 identity endpoint contract is missing from $relative"
    }
}

foreach ($endpoint in $Uri) {
    $payload = Invoke-RestMethod -Method Get -Uri $endpoint -TimeoutSec 10
    if (-not (Test-IdentityPayload $payload)) {
        throw "G4 rejected the identity payload from ${endpoint}: $($payload | ConvertTo-Json -Compress)"
    }
}

if ($SelfTest) {
    $bad = [pscustomobject]@{
        schema = 'comfy-repo-identity/v1'
        repository = 'djcdevelopment/baseline'
        service = 'lumberjacks-gateway'
        revision = 'unknown'
    }
    if (Test-IdentityPayload $bad) { throw 'G4 self-test accepted a stale-repository fixture' }
}

[pscustomobject]@{
    Schema = 'lumberjacks-boundary-guard/v1'
    Guard = 'G4'
    Endpoints = $Uri.Count
    SelfTest = [bool]$SelfTest
    Verdict = 'passed'
} | ConvertTo-Json -Compress
