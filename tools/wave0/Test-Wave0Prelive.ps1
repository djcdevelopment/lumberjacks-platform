<#
.SYNOPSIS
Run the full non-human Wave 0 pre-live audit.

.DESCRIPTION
Collects the evidence needed before asking for another two-client Valheim join:

- P7/OMEN/i5 release readiness.
- Public roadmap freshness against live P7 release truth.
- Mock live-gate fixture coverage.
- Mock auto-wait live-gate fixture coverage.
- Mock visual-seal fixture coverage.
- Mock named-defect packet fixture coverage.
- Human-test register coverage for the Derek-only return gates.
- Expected-result grid coverage for the live observation.
- Full strategy status fixture coverage.
- Wave 0 stop-rule coverage.
- Wave 0 stop-rule fixture coverage.
- Real no-client live-gate smoke, proving the gate stops before motion.
- Two-machine Companion capture/bundle smoke, proving OMEN+i5 evidence collection.
- Return packet generation from the newest valid receipts.
- Full strategy status packet generation from the current receipts.

This script does not move players. It is safe to run while no clients are joined.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$SkipSyntheticInNoClientGate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
if (-not $OutputDirectory) {
    $OutputDirectory = "captures/wave0-prelive/$stamp"
}
$outRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

function Invoke-Step {
    param(
        [string]$Name,
        [string]$Script,
        [string[]]$Arguments
    )

    Write-Host ("== {0}" -f $Name)
    $started = [DateTimeOffset]::UtcNow
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $finished = [DateTimeOffset]::UtcNow
    [ordered]@{
        name = $Name
        ok = $exitCode -eq 0
        exit_code = $exitCode
        started_utc = $started.ToString('o')
        finished_utc = $finished.ToString('o')
        duration_ms = [math]::Round(($finished - $started).TotalMilliseconds, 0)
        output_tail = @($output | Select-Object -Last 40)
    }
}

function Read-JsonOrNull {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}

$readinessPath = Join-Path $outRoot 'readiness.json'
$roadmapFreshnessPath = Join-Path $outRoot 'roadmap-freshness.json'
$fixturesRoot = Join-Path $outRoot 'fixtures'
$autoWaitFixturesRoot = Join-Path $outRoot 'auto-wait-fixtures'
$sealFixturesRoot = Join-Path $outRoot 'seal-fixtures'
$defectFixturesRoot = Join-Path $outRoot 'defect-fixtures'
$humanRegisterPath = Join-Path $outRoot 'human-test-register.json'
$expectedGridJson = Join-Path $outRoot 'expected-result-grid.json'
$expectedGridMarkdown = Join-Path $outRoot 'expected-result-grid.md'
$strategyStatusFixturesRoot = Join-Path $outRoot 'strategy-status-fixtures'
$stopRulePath = Join-Path $outRoot 'stop-rule.json'
$stopRuleFixturesRoot = Join-Path $outRoot 'stop-rule-fixtures'
$noClientRoot = Join-Path $outRoot 'no-client-live-gate'
$bundleSmokeRoot = Join-Path $outRoot 'bundle-smoke'
$returnPacketRoot = Join-Path $outRoot 'return-packet'
$strategyStatusRoot = Join-Path $outRoot 'strategy-status'
New-Item -ItemType Directory -Force -Path $fixturesRoot, $autoWaitFixturesRoot, $sealFixturesRoot, $defectFixturesRoot, $strategyStatusFixturesRoot, $noClientRoot, $bundleSmokeRoot, $returnPacketRoot, $strategyStatusRoot | Out-Null

$steps = @()
$steps += Invoke-Step `
    -Name 'readiness' `
    -Script (Join-Path $repoRoot 'tools/i5/Test-Wave0Readiness.ps1') `
    -Arguments @('-SummaryOnly', '-OutputJson', $readinessPath)

$steps += Invoke-Step `
    -Name 'roadmap-freshness' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0RoadmapFreshness.ps1') `
    -Arguments @('-OutputJson', $roadmapFreshnessPath)

$steps += Invoke-Step `
    -Name 'live-gate-fixtures' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0LiveGateFixtures.ps1') `
    -Arguments @('-OutputDirectory', $fixturesRoot)

$steps += Invoke-Step `
    -Name 'auto-wait-fixtures' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0AutoWaitFixtures.ps1') `
    -Arguments @('-OutputDirectory', $autoWaitFixturesRoot)

$steps += Invoke-Step `
    -Name 'visual-seal-fixtures' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0SealFixtures.ps1') `
    -Arguments @('-OutputDirectory', $sealFixturesRoot)

$steps += Invoke-Step `
    -Name 'defect-packet-fixtures' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0DefectPacketFixtures.ps1') `
    -Arguments @('-OutputDirectory', $defectFixturesRoot)

$steps += Invoke-Step `
    -Name 'human-test-register' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0HumanTestRegister.ps1') `
    -Arguments @('-OutputJson', $humanRegisterPath)

$steps += Invoke-Step `
    -Name 'expected-result-grid' `
    -Script (Join-Path $repoRoot 'tools/wave0/New-Wave0ExpectedResultGrid.ps1') `
    -Arguments @('-OutputJson', $expectedGridJson, '-OutputMarkdown', $expectedGridMarkdown)

$steps += Invoke-Step `
    -Name 'strategy-status-fixtures' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-FullRoadmapStrategyStatusFixtures.ps1') `
    -Arguments @('-OutputDirectory', $strategyStatusFixturesRoot)

$steps += Invoke-Step `
    -Name 'stop-rule' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0StopRule.ps1') `
    -Arguments @('-OutputJson', $stopRulePath)

$steps += Invoke-Step `
    -Name 'stop-rule-fixtures' `
    -Script (Join-Path $repoRoot 'tools/wave0/Test-Wave0StopRuleFixtures.ps1') `
    -Arguments @('-OutputDirectory', $stopRuleFixturesRoot)

$noClientArgs = @(
    '-DesiredApplyClient', 'omen',
    '-OutputJson', (Join-Path $noClientRoot 'result.json')
)
if ($SkipSyntheticInNoClientGate) { $noClientArgs += '-SkipSynthetic' }
$steps += Invoke-Step `
    -Name 'no-client-live-gate' `
    -Script (Join-Path $repoRoot 'tools/wave0/Start-Wave0LiveGate.ps1') `
    -Arguments $noClientArgs

$steps += Invoke-Step `
    -Name 'bundle-lane-smoke' `
    -Script (Join-Path $repoRoot 'tools/i5/Start-TwoClientCapture.ps1') `
    -Arguments @(
        '-DurationSeconds', '5',
        '-IntervalSeconds', '1',
        '-Label', 'wave0-prelive-bundle-smoke',
        '-BundleDirectory', (Join-Path $bundleSmokeRoot 'bundles'),
        '-OutputJson', (Join-Path $bundleSmokeRoot 'result.json'),
        '-SummaryOnly'
    )

$steps += Invoke-Step `
    -Name 'return-packet' `
    -Script (Join-Path $repoRoot 'tools/wave0/New-Wave0ReturnPacket.ps1') `
    -Arguments @(
        '-SyntheticReceipt', (Join-Path $noClientRoot 'synthetic-motion.json'),
        '-ReadinessReceipt', $readinessPath,
        '-RoadmapFreshnessReceipt', $roadmapFreshnessPath,
        '-LiveGateReceipt', (Join-Path $noClientRoot 'result.json'),
        '-FixtureReceipt', (Join-Path $fixturesRoot 'summary.json'),
        '-AutoWaitFixtureReceipt', (Join-Path $autoWaitFixturesRoot 'summary.json'),
        '-VisualSealFixtureReceipt', (Join-Path $sealFixturesRoot 'summary.json'),
        '-DefectPacketFixtureReceipt', (Join-Path $defectFixturesRoot 'summary.json'),
        '-HumanTestRegisterReceipt', $humanRegisterPath,
        '-ExpectedResultGridReceipt', $expectedGridJson,
        '-StopRuleReceipt', $stopRulePath,
        '-StopRuleFixtureReceipt', (Join-Path $stopRuleFixturesRoot 'summary.json'),
        '-BundleSmokeReceipt', (Join-Path $bundleSmokeRoot 'result.json'),
        '-OutputJson', (Join-Path $returnPacketRoot 'packet.json'),
        '-OutputMarkdown', (Join-Path $returnPacketRoot 'packet.md')
    )

$readiness = Read-JsonOrNull $readinessPath
$roadmapFreshness = Read-JsonOrNull $roadmapFreshnessPath
$fixtures = Read-JsonOrNull (Join-Path $fixturesRoot 'summary.json')
$autoWaitFixtures = Read-JsonOrNull (Join-Path $autoWaitFixturesRoot 'summary.json')
$sealFixtures = Read-JsonOrNull (Join-Path $sealFixturesRoot 'summary.json')
$defectFixtures = Read-JsonOrNull (Join-Path $defectFixturesRoot 'summary.json')
$humanRegister = Read-JsonOrNull $humanRegisterPath
$expectedGrid = Read-JsonOrNull $expectedGridJson
$strategyStatusFixtures = Read-JsonOrNull (Join-Path $strategyStatusFixturesRoot 'summary.json')
$stopRule = Read-JsonOrNull $stopRulePath
$stopRuleFixtures = Read-JsonOrNull (Join-Path $stopRuleFixturesRoot 'summary.json')
$noClient = Read-JsonOrNull (Join-Path $noClientRoot 'result.json')
$bundleSmoke = Read-JsonOrNull (Join-Path $bundleSmokeRoot 'result.json')
$packet = Read-JsonOrNull (Join-Path $returnPacketRoot 'packet.json')

$bundleCount = if ($bundleSmoke -and $bundleSmoke.bundles) { @($bundleSmoke.bundles).Count } else { 0 }
$failedSteps = @($steps | Where-Object { -not $_.ok })
$verdict =
    if ($failedSteps.Count -gt 0) { 'prelive_audit_failed' }
    elseif (-not $readiness -or [string]$readiness.verdict -ne 'ready_for_two_client_gate') { 'prelive_readiness_not_ready' }
    elseif (-not $roadmapFreshness -or [string]$roadmapFreshness.verdict -ne 'wave0_roadmap_freshness_passed') { 'prelive_roadmap_not_fresh' }
    elseif (-not $fixtures -or [string]$fixtures.verdict -ne 'wave0_live_gate_fixture_checks_passed') { 'prelive_fixtures_not_ready' }
    elseif (-not $autoWaitFixtures -or [string]$autoWaitFixtures.verdict -ne 'wave0_auto_wait_fixture_checks_passed') { 'prelive_auto_wait_fixtures_not_ready' }
    elseif (-not $sealFixtures -or [string]$sealFixtures.verdict -ne 'wave0_visual_seal_fixture_checks_passed') { 'prelive_visual_seal_fixtures_not_ready' }
    elseif (-not $defectFixtures -or [string]$defectFixtures.verdict -ne 'wave0_defect_packet_fixture_checks_passed') { 'prelive_defect_packet_fixtures_not_ready' }
    elseif (-not $humanRegister -or [string]$humanRegister.verdict -ne 'wave0_human_test_register_current') { 'prelive_human_test_register_not_current' }
    elseif (-not $expectedGrid -or [string]$expectedGrid.verdict -ne 'wave0_expected_result_grid_ready') { 'prelive_expected_result_grid_not_ready' }
    elseif (-not $strategyStatusFixtures -or [string]$strategyStatusFixtures.verdict -ne 'full_roadmap_strategy_status_fixture_checks_passed') { 'prelive_strategy_status_fixtures_not_ready' }
    elseif (-not $stopRule -or [string]$stopRule.verdict -notin @('wave0_stop_rule_holds_no_exit_artifact', 'wave0_exit_artifact_present')) { 'prelive_stop_rule_not_ready' }
    elseif (-not $stopRuleFixtures -or [string]$stopRuleFixtures.verdict -ne 'wave0_stop_rule_fixture_checks_passed') { 'prelive_stop_rule_fixtures_not_ready' }
    elseif (-not $noClient -or [string]$noClient.verdict -ne 'wait_for_two_real_clients') { 'prelive_no_client_gate_unexpected' }
    elseif (-not $bundleSmoke -or $bundleCount -lt 2) { 'prelive_bundle_collection_not_ready' }
    elseif (-not $packet -or [string]$packet.verdict -ne 'ready_for_derek_two_client_join') { 'prelive_return_packet_not_ready' }
    else { 'ready_for_derek_two_client_join' }

$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = $verdict
    output_directory = $outRoot
    expected_release = if ($readiness) { [string]$readiness.expected_release } else { $null }
    p7_peer_count = if ($noClient -and $noClient.p7_peer_check) { $noClient.p7_peer_check.peer_count } else { $null }
    bundle_count = $bundleCount
    steps = $steps
    receipts = [ordered]@{
        readiness = $readinessPath
        roadmap_freshness = $roadmapFreshnessPath
        fixtures = Join-Path $fixturesRoot 'summary.json'
        auto_wait_fixtures = Join-Path $autoWaitFixturesRoot 'summary.json'
        visual_seal_fixtures = Join-Path $sealFixturesRoot 'summary.json'
        defect_packet_fixtures = Join-Path $defectFixturesRoot 'summary.json'
        human_test_register = $humanRegisterPath
        expected_result_grid_json = $expectedGridJson
        expected_result_grid_markdown = $expectedGridMarkdown
        strategy_status_fixtures = Join-Path $strategyStatusFixturesRoot 'summary.json'
        stop_rule = $stopRulePath
        stop_rule_fixtures = Join-Path $stopRuleFixturesRoot 'summary.json'
        no_client_live_gate = Join-Path $noClientRoot 'result.json'
        bundle_smoke = Join-Path $bundleSmokeRoot 'result.json'
        return_packet_json = Join-Path $returnPacketRoot 'packet.json'
        return_packet_markdown = Join-Path $returnPacketRoot 'packet.md'
        strategy_status_json = Join-Path $strategyStatusRoot 'packet.json'
        strategy_status_markdown = Join-Path $strategyStatusRoot 'packet.md'
    }
    next_action = if ($verdict -eq 'ready_for_derek_two_client_join') {
        'Start Wait-Wave0LiveGate.ps1 with DesiredApplyClient omen, then join OMEN and i5 to P7.'
    } else {
        'Inspect failed step output_tail and receipt paths before asking for a live join.'
    }
}

$receiptPath = Join-Path $outRoot 'summary.json'
$preStrategyReceiptPath = Join-Path $outRoot 'summary.pre-strategy.json'
$receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $preStrategyReceiptPath -Encoding UTF8

$strategyStep = Invoke-Step `
    -Name 'strategy-status' `
    -Script (Join-Path $repoRoot 'tools/wave0/New-FullRoadmapStrategyStatus.ps1') `
    -Arguments @(
        '-PreliveSummaryJson', $preStrategyReceiptPath,
        '-OutputJson', (Join-Path $strategyStatusRoot 'packet.json'),
        '-OutputMarkdown', (Join-Path $strategyStatusRoot 'packet.md')
    )
$steps += $strategyStep
$strategyStatus = Read-JsonOrNull (Join-Path $strategyStatusRoot 'packet.json')

if (-not $strategyStep.ok) {
    $receipt['verdict'] = 'prelive_audit_failed'
} elseif ($receipt['verdict'] -eq 'ready_for_derek_two_client_join' -and (-not $strategyStatus -or [string]$strategyStatus.verdict -ne 'strategy_active_wave0_human_gated')) {
    $receipt['verdict'] = 'prelive_strategy_status_not_ready'
}
$receipt['steps'] = $steps
$receipt['next_action'] = if ($receipt['verdict'] -eq 'ready_for_derek_two_client_join') {
    'Start Wait-Wave0LiveGate.ps1 with DesiredApplyClient omen, then join OMEN and i5 to P7.'
} else {
    'Inspect failed step output_tail and receipt paths before asking for a live join.'
}

$receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $receiptPath -Encoding UTF8

Write-Host ("Wave 0 prelive audit: {0}" -f $receipt['verdict'])
Write-Host ("Summary JSON: {0}" -f $receiptPath)
Write-Host ("Return packet: {0}" -f $receipt.receipts.return_packet_markdown)
if ($receipt['verdict'] -ne 'ready_for_derek_two_client_join') { exit 1 }
