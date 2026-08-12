[CmdletBinding()]
param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$entrypoints = @(
    'infra/gcp/p7/scripts/New-ReleaseCut.ps1',
    'infra/gcp/p7/scripts/New-GatewayReleaseCut.ps1',
    'infra/gcp/p7/scripts/Promote-GatewayImage.ps1',
    'infra/gcp/p7/scripts/Publish-CompanionBootstrap.ps1',
    'infra/gcp/p7/scripts/Publish-Modpack.ps1',
    'infra/gcp/p7/scripts/deploy-gateway.ps1',
    'infra/gcp/p7/scripts/deploy-network-sense.ps1',
    'infra/gcp/p7/scripts/rollback-network-sense.ps1',
    'infra/gcp/p7/scripts/run-promotion-drill.ps1',
    'infra/gcp/p7/scripts/new-player-invite.ps1',
    'infra/gcp/p7/scripts/start-direct-session.ps1',
    'infra/gcp/p7/scripts/start-primary-session.ps1',
    'fieldlab/scripts/Invoke-HeadlessValheimLab.ps1',
    'fieldlab/scripts/Capture-TransportTruth.ps1',
    'fieldlab/scripts/Invoke-NativeValheimCutoverScenario.ps1',
    'fieldlab/scripts/Invoke-LabStateRootMigration.ps1',
    'fieldlab/scripts/Invoke-NativeValheimClient.ps1',
    'fieldlab/scripts/Invoke-ValheimServerRuntimeControl.ps1',
    'tools/p7/Invoke-C10bCandidateProof.ps1',
    'tools/p7/Invoke-C10bPairPromotion.ps1',
    'tools/p7/Invoke-P7BootDeterminism.ps1',
    'tools/wave0/Start-Wave0LiveGate.ps1',
    'Lumberjacks/tools/companion/New-CompanionBootstrap.ps1',
    'Lumberjacks/tools/companion/Start-LocalCompanion.ps1',
    'Lumberjacks/tools/companion/Deploy-ToI5.ps1',
    'Lumberjacks/tools/companion/Start-I5Companion.ps1',
    'Lumberjacks/tools/companion/Sync-I5Companion.ps1'
)

function Test-GuardedText([string]$Text) {
    return $Text -match '(?m)^\s*Assert-RepoIdentity\b'
}

$missing = @()
foreach ($relative in $entrypoints) {
    $path = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        -not (Test-GuardedText (Get-Content -LiteralPath $path -Raw))) {
        $missing += $relative
    }
}
if ($missing.Count -gt 0) {
    throw "G2 identity guard missing from: $($missing -join ', ')"
}

if ($SelfTest -and (Test-GuardedText 'docker compose up -d')) {
    throw 'G2 self-test accepted an unguarded state-changing fixture'
}

[pscustomobject]@{
    Schema = 'lumberjacks-boundary-guard/v1'
    Guard = 'G2'
    Entrypoints = $entrypoints.Count
    SelfTest = [bool]$SelfTest
    Verdict = 'passed'
} | ConvertTo-Json -Compress
