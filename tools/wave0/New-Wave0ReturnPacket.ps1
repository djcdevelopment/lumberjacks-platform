<#
.SYNOPSIS
Generate the operator return packet for the remaining Wave 0 live gate.

.DESCRIPTION
Summarizes current non-human evidence and the exact Derek-only live steps left.
The packet references receipt paths and hashes instead of embedding raw
Companion/Gateway receipt bodies, because those may include private local
profile or diagnostic details.
#>
[CmdletBinding()]
param(
    [string]$SyntheticReceipt = '',
    [string]$ReadinessReceipt = '',
    [string]$RoadmapFreshnessReceipt = '',
    [string]$LiveGateReceipt = '',
    [string]$FixtureReceipt = '',
    [string]$AutoWaitFixtureReceipt = '',
    [string]$VisualSealFixtureReceipt = '',
    [string]$DefectPacketFixtureReceipt = '',
    [string]$HumanTestRegisterReceipt = '',
    [string]$ExpectedResultGridReceipt = '',
    [string]$BundleSmokeReceipt = '',
    [string]$OutputJson = 'captures/wave0-return-packet.json',
    [string]$OutputMarkdown = 'captures/wave0-return-packet.md'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Read-Receipt {
    param([string]$Path)

    $full = Resolve-UnderRepo $Path
    if (-not (Test-Path -LiteralPath $full)) {
        return [ordered]@{
            present = $false
            path = $full
            sha256 = $null
            body = $null
        }
    }

    [ordered]@{
        present = $true
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        body = (Get-Content -LiteralPath $full -Raw | ConvertFrom-Json)
    }
}

function Find-LatestReceipt {
    param(
        [string]$Label,
        [scriptblock]$Predicate
    )

    $capturesRoot = Resolve-UnderRepo 'captures'
    if (-not (Test-Path -LiteralPath $capturesRoot -PathType Container)) {
        return ''
    }

    $files = Get-ChildItem -LiteralPath $capturesRoot -Recurse -File -Filter '*.json' |
        Sort-Object LastWriteTimeUtc -Descending

    foreach ($file in $files) {
        try {
            $body = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        } catch {
            continue
        }
        if (& $Predicate $body) {
            Write-Host ("{0}: {1}" -f $Label, $file.FullName)
            return $file.FullName
        }
    }

    return ''
}

function Check-State {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail,
        [string]$ReceiptPath = '',
        [string]$ReceiptSha256 = ''
    )

    [ordered]@{
        name = $Name
        ok = $Ok
        detail = $Detail
        receipt_path = $ReceiptPath
        receipt_sha256 = $ReceiptSha256
    }
}

function MdEscape {
    param([string]$Value)

    if ($null -eq $Value) { return '' }
    return $Value.Replace('|', '\|')
}

if (-not $SyntheticReceipt) {
    $SyntheticReceipt = Find-LatestReceipt 'synthetic receipt' {
        param($Body)
        [string]$Body.verdict -eq 'synthetic_motion_gate_passed' -and [bool]$Body.ok
    }
}
if (-not $ReadinessReceipt) {
    $ReadinessReceipt = Find-LatestReceipt 'readiness receipt' {
        param($Body)
        $null -ne $Body.ready_for_derek -and [string]$Body.verdict -in @('ready_for_two_client_gate', 'waiting_for_optional_i5_or_real_clients', 'blocked_by_failed_preflight')
    }
}
if (-not $RoadmapFreshnessReceipt) {
    $RoadmapFreshnessReceipt = Find-LatestReceipt 'roadmap freshness receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_roadmap_freshness_passed'
    }
}
if (-not $LiveGateReceipt) {
    $LiveGateReceipt = Find-LatestReceipt 'live gate wait receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wait_for_two_real_clients' -and
            $null -ne $Body.non_human_gates.synthetic_motion -and
            $null -ne $Body.non_human_gates.readiness
    }
}
if (-not $FixtureReceipt) {
    $FixtureReceipt = Find-LatestReceipt 'fixture summary receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_live_gate_fixture_checks_passed'
    }
}
if (-not $AutoWaitFixtureReceipt) {
    $AutoWaitFixtureReceipt = Find-LatestReceipt 'auto-wait fixture summary receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_auto_wait_fixture_checks_passed'
    }
}
if (-not $VisualSealFixtureReceipt) {
    $VisualSealFixtureReceipt = Find-LatestReceipt 'visual seal fixture summary receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_visual_seal_fixture_checks_passed'
    }
}
if (-not $DefectPacketFixtureReceipt) {
    $DefectPacketFixtureReceipt = Find-LatestReceipt 'defect packet fixture summary receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_defect_packet_fixture_checks_passed'
    }
}
if (-not $HumanTestRegisterReceipt) {
    $HumanTestRegisterReceipt = Find-LatestReceipt 'human-test register receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_human_test_register_current'
    }
}
if (-not $ExpectedResultGridReceipt) {
    $ExpectedResultGridReceipt = Find-LatestReceipt 'expected-result grid receipt' {
        param($Body)
        [string]$Body.verdict -eq 'wave0_expected_result_grid_ready'
    }
}
if (-not $BundleSmokeReceipt) {
    $BundleSmokeReceipt = Find-LatestReceipt 'bundle smoke receipt' {
        param($Body)
        $null -ne $Body.comparison -and $null -ne $Body.bundles
    }
}

$synthetic = Read-Receipt $SyntheticReceipt
$readiness = Read-Receipt $ReadinessReceipt
$roadmapFreshness = Read-Receipt $RoadmapFreshnessReceipt
$liveGate = Read-Receipt $LiveGateReceipt
$fixtures = Read-Receipt $FixtureReceipt
$autoWaitFixtures = Read-Receipt $AutoWaitFixtureReceipt
$visualSealFixtures = Read-Receipt $VisualSealFixtureReceipt
$defectPacketFixtures = Read-Receipt $DefectPacketFixtureReceipt
$humanTestRegister = Read-Receipt $HumanTestRegisterReceipt
$expectedResultGrid = Read-Receipt $ExpectedResultGridReceipt
$bundleSmoke = Read-Receipt $BundleSmokeReceipt

$checks = @()
$checks += Check-State `
    -Name 'synthetic_motion_gate' `
    -Ok ($synthetic.present -and [bool]$synthetic.body.ok -and [string]$synthetic.body.verdict -eq 'synthetic_motion_gate_passed') `
    -Detail ($(if ($synthetic.present) { "verdict=$($synthetic.body.verdict) total=$($synthetic.body.result.total) passed=$($synthetic.body.result.passed)" } else { 'missing synthetic motion receipt' })) `
    -ReceiptPath $synthetic.path `
    -ReceiptSha256 $synthetic.sha256
$checks += Check-State `
    -Name 'runtime_readiness_gate' `
    -Ok ($readiness.present -and [bool]$readiness.body.ready_for_derek -and [string]$readiness.body.verdict -eq 'ready_for_two_client_gate') `
    -Detail ($(if ($readiness.present) { "verdict=$($readiness.body.verdict) release=$($readiness.body.expected_release)" } else { 'missing readiness receipt' })) `
    -ReceiptPath $readiness.path `
    -ReceiptSha256 $readiness.sha256
$checks += Check-State `
    -Name 'roadmap_freshness_gate' `
    -Ok ($roadmapFreshness.present -and [string]$roadmapFreshness.body.verdict -eq 'wave0_roadmap_freshness_passed') `
    -Detail ($(if ($roadmapFreshness.present) { "verdict=$($roadmapFreshness.body.verdict) release=$($roadmapFreshness.body.expected_release) bootstrap=$($roadmapFreshness.body.expected_companion_bootstrap)" } else { 'missing roadmap freshness receipt' })) `
    -ReceiptPath $roadmapFreshness.path `
    -ReceiptSha256 $roadmapFreshness.sha256
$checks += Check-State `
    -Name 'live_gate_wait_state' `
    -Ok ($liveGate.present -and [string]$liveGate.body.verdict -eq 'wait_for_two_real_clients') `
    -Detail ($(if ($liveGate.present) { "verdict=$($liveGate.body.verdict) peer_count=$($liveGate.body.p7_peer_check.peer_count)" } else { 'missing live gate smoke receipt' })) `
    -ReceiptPath $liveGate.path `
    -ReceiptSha256 $liveGate.sha256
$checks += Check-State `
    -Name 'live_gate_fixture_roles' `
    -Ok ($fixtures.present -and [string]$fixtures.body.verdict -eq 'wave0_live_gate_fixture_checks_passed') `
    -Detail ($(if ($fixtures.present) { "verdict=$($fixtures.body.verdict) cases=$(@($fixtures.body.cases).Count)" } else { 'missing live gate fixture summary' })) `
    -ReceiptPath $fixtures.path `
    -ReceiptSha256 $fixtures.sha256
$checks += Check-State `
    -Name 'auto_wait_fixture_gate' `
    -Ok ($autoWaitFixtures.present -and [string]$autoWaitFixtures.body.verdict -eq 'wave0_auto_wait_fixture_checks_passed') `
    -Detail ($(if ($autoWaitFixtures.present) { "verdict=$($autoWaitFixtures.body.verdict) cases=$(@($autoWaitFixtures.body.cases).Count)" } else { 'missing auto-wait fixture summary' })) `
    -ReceiptPath $autoWaitFixtures.path `
    -ReceiptSha256 $autoWaitFixtures.sha256
$checks += Check-State `
    -Name 'visual_seal_fixture_gate' `
    -Ok ($visualSealFixtures.present -and [string]$visualSealFixtures.body.verdict -eq 'wave0_visual_seal_fixture_checks_passed') `
    -Detail ($(if ($visualSealFixtures.present) { "verdict=$($visualSealFixtures.body.verdict)" } else { 'missing visual seal fixture summary' })) `
    -ReceiptPath $visualSealFixtures.path `
    -ReceiptSha256 $visualSealFixtures.sha256
$checks += Check-State `
    -Name 'defect_packet_fixture_gate' `
    -Ok ($defectPacketFixtures.present -and [string]$defectPacketFixtures.body.verdict -eq 'wave0_defect_packet_fixture_checks_passed') `
    -Detail ($(if ($defectPacketFixtures.present) { "verdict=$($defectPacketFixtures.body.verdict)" } else { 'missing defect packet fixture summary' })) `
    -ReceiptPath $defectPacketFixtures.path `
    -ReceiptSha256 $defectPacketFixtures.sha256
$checks += Check-State `
    -Name 'human_test_register_gate' `
    -Ok ($humanTestRegister.present -and [string]$humanTestRegister.body.verdict -eq 'wave0_human_test_register_current') `
    -Detail ($(if ($humanTestRegister.present) { "verdict=$($humanTestRegister.body.verdict) checks=$(@($humanTestRegister.body.checks).Count)" } else { 'missing human-test register receipt' })) `
    -ReceiptPath $humanTestRegister.path `
    -ReceiptSha256 $humanTestRegister.sha256
$checks += Check-State `
    -Name 'expected_result_grid_gate' `
    -Ok ($expectedResultGrid.present -and [string]$expectedResultGrid.body.verdict -eq 'wave0_expected_result_grid_ready' -and @($expectedResultGrid.body.rows).Count -ge 5) `
    -Detail ($(if ($expectedResultGrid.present) { "verdict=$($expectedResultGrid.body.verdict) rows=$(@($expectedResultGrid.body.rows).Count)" } else { 'missing expected-result grid receipt' })) `
    -ReceiptPath $expectedResultGrid.path `
    -ReceiptSha256 $expectedResultGrid.sha256
$bundleCount = if ($bundleSmoke.present -and $bundleSmoke.body.bundles) { @($bundleSmoke.body.bundles).Count } else { 0 }
$bundleDetail = if ($bundleSmoke.present) {
    $comparisonLevel = [string]$bundleSmoke.body.comparison.level
    $headline = [string]$bundleSmoke.body.comparison.headline
    if (-not $comparisonLevel) { $comparisonLevel = 'unknown' }
    if (-not $headline) { $headline = 'no comparison headline' }
    "level=$comparisonLevel bundles=$bundleCount; $headline"
} else {
    'missing two-machine bundle smoke receipt'
}
$checks += Check-State `
    -Name 'two_machine_bundle_smoke' `
    -Ok ($bundleSmoke.present -and $bundleCount -ge 2 -and $null -ne $bundleSmoke.body.comparison) `
    -Detail $bundleDetail `
    -ReceiptPath $bundleSmoke.path `
    -ReceiptSha256 $bundleSmoke.sha256

$failed = @($checks | Where-Object { -not $_.ok })
$expectedRelease = if ($readiness.present) { [string]$readiness.body.expected_release } else { '' }
$currentPeerCount = if ($liveGate.present -and $liveGate.body.p7_peer_check) { [int]$liveGate.body.p7_peer_check.peer_count } else { $null }
$observationMarkdown = if ($liveGate.present -and $liveGate.body.observation_markdown) { [string]$liveGate.body.observation_markdown } else { '' }
$expectedGridMarkdown = ''
if ($expectedResultGrid.present) {
    $expectedGridMarkdown = [IO.Path]::ChangeExtension($expectedResultGrid.path, '.md')
}

$packet = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    objective = 'Wave 0 return packet: non-human evidence plus Derek-only live steps.'
    expected_release = $expectedRelease
    verdict = if ($failed.Count -eq 0) { 'ready_for_derek_two_client_join' } else { 'prelive_packet_incomplete' }
    checks = $checks
    current_state = [ordered]@{
        p7_peer_count = $currentPeerCount
        live_gate_waiting_for = 'two_real_clients_joined'
        machine_commands_ready = $true
        original_receipts_left_immutable = $true
        observation_markdown = $observationMarkdown
        expected_result_grid = $expectedGridMarkdown
    }
    live_prerequisites = @(
        [ordered]@{
            name = 'expected_result_grid'
            status = if ($expectedResultGrid.present) { 'ready' } else { 'missing' }
            evidence = $expectedGridMarkdown
        },
        [ordered]@{
            name = 'preflight_and_release_alignment'
            status = if ($readiness.present -and [bool]$readiness.body.ready_for_derek) { 'ready' } else { 'missing_or_failed' }
            evidence = $readiness.path
        },
        [ordered]@{
            name = 'bounded_command_with_timeout_and_auto_stop'
            status = 'ready'
            evidence = 'Wait-Wave0LiveGate.ps1 waits up to 600s by default; Start-Wave0LiveGate.ps1 sends bounded Companion motion with MotionDurationSeconds and capture duration limits.'
        },
        [ordered]@{
            name = 'capture_locations'
            status = 'ready'
            evidence = 'Live receipts: captures\wave0-live-gate\ and captures\wave0-live-gate-reversal\; bundle directories default beside each receipt under bundles\; Companion dashboards retain per-machine bundles.'
        },
        [ordered]@{
            name = 'dashboard_urls'
            status = 'ready'
            evidence = 'OMEN Companion http://127.0.0.1:8080/ ; i5 Companion http://127.0.0.1:8080/ on the i5; P7 public community https://comfy-p7.duckdns.org/community ; P7 trace https://comfy-p7.duckdns.org/trace ; P7 roadmap https://comfy-p7.duckdns.org/roadmap'
        },
        [ordered]@{
            name = 'rollback_or_stop_path'
            status = 'ready'
            evidence = 'Stop on any failed gate; use Companion rollback latest on OMEN/i5 for client package rollback; do not advance past Wave 0 unless Seal-Wave0VisualEvidence.ps1 succeeds or New-Wave0DefectPacket.ps1 retains a named defect.'
        },
        [ordered]@{
            name = 'human_observation_boundary'
            status = 'ready'
            evidence = 'Only visual follow/quality/role-reversal judgment is human-owned; all deployment, role switch, movement, capture, annotation, sealing, and defect packet generation are agent-owned.'
        }
    )
    run_when_back = @(
        [ordered]@{
            step = 1
            actor = 'derek'
            action = 'Join OMEN and i5 to P7 with the two player accounts.'
            expected = 'P7 peer_count reaches at least 2.'
        },
        [ordered]@{
            step = 2
            actor = 'agent'
            action = 'Run tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient omen -OutputJson captures\wave0-live-gate\result.json'
            expected = 'Machine receipt records role command, verified OMEN apply / i5 observe split, capture, and bounded motion command.'
        },
        [ordered]@{
            step = 3
            actor = 'derek'
            action = 'Observe both screens during the bounded movement.'
            expected = 'Record whether Lumberjacks-applied movement followed the selected apply/observe role and movement quality.'
        },
        [ordered]@{
            step = 4
            actor = 'agent'
            action = 'Run tools\wave0\Add-Wave0VisualObservation.ps1 against the live-gate receipt.'
            expected = 'Sidecar visual observation and annotated projection are written without editing the original receipt.'
        },
        [ordered]@{
            step = 5
            actor = 'derek-plus-agent'
            action = 'Run tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient i5 -OutputJson captures\wave0-live-gate-reversal\result.json, then repeat steps 3-4.'
            expected = 'The visual result follows role selection rather than machine/account.'
        },
        [ordered]@{
            step = 6
            actor = 'agent'
            action = 'Run tools\wave0\Seal-Wave0VisualEvidence.ps1 against the two annotated projections.'
            expected = 'One derived seal receipt verifies both directions, role reversal, distinct source receipts, and allowed live-gate verdicts.'
        }
    )
    stop_conditions = @(
        'Any non-human gate fails.',
        'P7 peer_count stays below 2.',
        'Capture comparison has incomplete telemetry.',
        'Motion command fails on either Companion.',
        'Visual result does not follow the selected apply/observe role.',
        'Role reversal contradicts the first run.'
    )
    commands = [ordered]@{
        precheck = 'tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient omen -OutputJson captures\wave0-live-gate\result.json'
        role_reversal = 'tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient i5 -OutputJson captures\wave0-live-gate-reversal\result.json'
        annotate_first_pass = 'tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson captures\wave0-live-gate\result.json -ApplyClient omen -ObserveClient i5 -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun no'
        annotate_role_reversal = 'tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson captures\wave0-live-gate-reversal\result.json -ApplyClient i5 -ObserveClient omen -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun yes'
        seal_visual_evidence = 'tools\wave0\Seal-Wave0VisualEvidence.ps1 -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -OutputJson captures\wave0-live-seal\visual-seal.json'
        suggest_named_defect = 'tools\wave0\Suggest-Wave0DefectPacket.ps1 -FirstReceiptJson captures\wave0-live-gate\result.json -ReversalReceiptJson captures\wave0-live-gate-reversal\result.json -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -SealJson captures\wave0-live-seal\visual-seal.json -OutputJson captures\wave0-defects\suggestion.json -OutputMarkdown captures\wave0-defects\suggestion.md'
        retain_named_defect = 'tools\wave0\New-Wave0DefectPacket.ps1 -DefectId wave0-visual-proof-not-sealed -DefectKind visual_inconclusive -Summary "Visual proof could not be sealed; inspect annotations and seal failure." -FirstReceiptJson captures\wave0-live-gate\result.json -ReversalReceiptJson captures\wave0-live-gate-reversal\result.json -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -SealJson captures\wave0-live-seal\visual-seal.json'
    }
}

$markdownLines = @()
$markdownLines += '# Wave 0 return packet'
$markdownLines += ''
$markdownLines += "- Generated UTC: $($packet.generated_utc)"
$markdownLines += "- Verdict: $($packet.verdict)"
if ($expectedRelease) { $markdownLines += "- Expected release: $expectedRelease" }
if ($null -ne $currentPeerCount) { $markdownLines += "- Current P7 peer count: $currentPeerCount" }
if ($observationMarkdown) { $markdownLines += "- Observation worksheet: $observationMarkdown" }
if ($expectedGridMarkdown) { $markdownLines += "- Expected-result grid: $expectedGridMarkdown" }
$markdownLines += ''
$markdownLines += '## Live prerequisites'
$markdownLines += ''
$markdownLines += '| Required before live test | Status | Evidence / location |'
$markdownLines += '|---|---|---|'
foreach ($prereq in $packet.live_prerequisites) {
    $markdownLines += "| $(MdEscape $prereq.name) | $(MdEscape $prereq.status) | $(MdEscape $prereq.evidence) |"
}
$markdownLines += ''
$markdownLines += '## Non-human evidence'
$markdownLines += ''
$markdownLines += '| Check | OK | Detail | Receipt SHA-256 |'
$markdownLines += '|---|---:|---|---|'
foreach ($check in $checks) {
    $markdownLines += "| $(MdEscape $check.name) | $($check.ok) | $(MdEscape $check.detail) | $($check.receipt_sha256) |"
}
$markdownLines += ''
$markdownLines += '## Run when back'
$markdownLines += ''
if ($observationMarkdown) {
    $markdownLines += "Use the observation worksheet during the live pass: $observationMarkdown"
    $markdownLines += ''
}
foreach ($step in $packet.run_when_back) {
    $markdownLines += "$($step.step). **$($step.actor)** - $($step.action)"
    $markdownLines += "   - Expected: $($step.expected)"
}
$markdownLines += ''
$markdownLines += '## Stop conditions'
$markdownLines += ''
foreach ($condition in $packet.stop_conditions) {
    $markdownLines += "- $condition"
}
$markdownLines += ''
$markdownLines += '## Commands'
$markdownLines += ''
$markdownLines += '```powershell'
$markdownLines += $packet.commands.precheck
$markdownLines += ''
$markdownLines += '# After observing the first pass, fill these values with what actually happened:'
$markdownLines += $packet.commands.annotate_first_pass
$markdownLines += ''
$markdownLines += '# Role reversal live run:'
$markdownLines += $packet.commands.role_reversal
$markdownLines += ''
$markdownLines += '# After observing role reversal:'
$markdownLines += $packet.commands.annotate_role_reversal
$markdownLines += ''
$markdownLines += '# Seal both annotated visual projections:'
$markdownLines += $packet.commands.seal_visual_evidence
$markdownLines += ''
$markdownLines += '# If the seal fails or visual proof is inconclusive, classify the defect first:'
$markdownLines += $packet.commands.suggest_named_defect
$markdownLines += ''
$markdownLines += '# Then run the New-Wave0DefectPacket.ps1 command printed in the suggestion.'
$markdownLines += '# Manual fallback if the suggestion cannot be generated:'
$markdownLines += $packet.commands.retain_named_defect
$markdownLines += '```'
$markdownLines += ''
$markdownLines += 'The original machine receipts remain immutable. Visual observations are sidecars, and the seal is a derived index over both directions.'

$jsonPath = Resolve-UnderRepo $OutputJson
$mdPath = Resolve-UnderRepo $OutputMarkdown
foreach ($path in @($jsonPath, $mdPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

[IO.File]::WriteAllText($jsonPath, (($packet | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($mdPath, (($markdownLines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 return packet: {0}" -f $packet.verdict)
Write-Host ("JSON: {0}" -f $jsonPath)
Write-Host ("Markdown: {0}" -f $mdPath)
if ($failed.Count -gt 0) {
    Write-Host 'Incomplete checks:'
    foreach ($check in $failed) { Write-Host ("- {0}: {1}" -f $check.name, $check.detail) }
}
