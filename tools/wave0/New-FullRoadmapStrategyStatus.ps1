<#
.SYNOPSIS
Generate a conservative status packet for the full roadmap working strategy.

.DESCRIPTION
This packet does not claim roadmap completion. It summarizes what current
evidence proves, what remains human-gated, and what is intentionally deferred by
the Wave 0 stop rule. It reads local strategy docs and the latest Wave 0 pre-live
receipt when available.
#>
[CmdletBinding()]
param(
    [string]$StrategyPath = 'plans/full-roadmap-working-strategy.md',
    [string]$HumanTestRegisterPath = 'plans/remaining-human-tests.md',
    [string]$PreliveSummaryJson = '',
    [string]$OutputJson = 'captures/full-roadmap-strategy-status.json',
    [string]$OutputMarkdown = 'captures/full-roadmap-strategy-status.md'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Read-TextFile {
    param([string]$Path)

    $full = Resolve-UnderRepo $Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        return [ordered]@{ present = $false; path = $full; sha256 = $null; text = '' }
    }
    [ordered]@{
        present = $true
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        text = Get-Content -LiteralPath $full -Raw
    }
}

function Read-JsonFile {
    param([string]$Path)

    $full = Resolve-UnderRepo $Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        return [ordered]@{ present = $false; path = $full; sha256 = $null; body = $null }
    }
    [ordered]@{
        present = $true
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        body = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
    }
}

function Find-LatestPreliveSummary {
    $capturesRoot = Resolve-UnderRepo 'captures'
    if (-not (Test-Path -LiteralPath $capturesRoot -PathType Container)) { return '' }

    $files = Get-ChildItem -LiteralPath $capturesRoot -Recurse -File -Filter 'summary.json' |
        Sort-Object LastWriteTimeUtc -Descending
    foreach ($file in $files) {
        try {
            $body = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        } catch {
            continue
        }
        if ($null -ne $body.receipts -and [string]$body.verdict -like 'ready_for_derek_two_client_join') {
            return $file.FullName
        }
    }
    return ''
}

function New-StatusRow {
    param(
        [string]$Area,
        [string]$Status,
        [string]$Evidence,
        [string]$NextAction
    )

    [ordered]@{
        area = $Area
        status = $Status
        evidence = $Evidence
        next_action = $NextAction
    }
}

function MdEscape {
    param([string]$Value)

    if ($null -eq $Value) { return '' }
    return $Value.Replace('|', '\|')
}

$strategy = Read-TextFile $StrategyPath
$humanRegister = Read-TextFile $HumanTestRegisterPath
if (-not $PreliveSummaryJson) { $PreliveSummaryJson = Find-LatestPreliveSummary }
$prelive = if ($PreliveSummaryJson) { Read-JsonFile $PreliveSummaryJson } else { [ordered]@{ present = $false; path = ''; sha256 = $null; body = $null } }

$preliveVerdict = if ($prelive.present) { [string]$prelive.body.verdict } else { 'missing' }
$expectedRelease = if ($prelive.present) { [string]$prelive.body.expected_release } else { '' }
$peerCount = if ($prelive.present) { $prelive.body.p7_peer_count } else { $null }
$returnPacket = if ($prelive.present -and $prelive.body.receipts) { [string]$prelive.body.receipts.return_packet_markdown } else { '' }
$expectedGrid = if ($prelive.present -and $prelive.body.receipts) { [string]$prelive.body.receipts.expected_result_grid_markdown } else { '' }
$stopRuleReceiptPath = if ($prelive.present -and $prelive.body.receipts) { [string]$prelive.body.receipts.stop_rule } else { '' }
$stopRuleReceipt = if ($stopRuleReceiptPath) { Read-JsonFile $stopRuleReceiptPath } else { [ordered]@{ present = $false; path = ''; sha256 = $null; body = $null } }

$wave0Ready = $prelive.present -and $preliveVerdict -eq 'ready_for_derek_two_client_join'
$humanRegisterReady = $humanRegister.present -and $humanRegister.text.Contains('H0-1') -and $humanRegister.text.Contains('H4-5')
$strategyStopRulePresent = $strategy.present -and $strategy.text.Contains('Do not begin M1/M2 expansion work until Wave 0')
$wave0ExitArtifactPresent = $stopRuleReceipt.present -and [bool]$stopRuleReceipt.body.exit_artifact_present

function New-AuditItem {
    param(
        [string]$Requirement,
        [string]$State,
        [string]$Evidence,
        [string]$Needed
    )

    [ordered]@{
        requirement = $Requirement
        state = $State
        evidence = $Evidence
        needed = $Needed
    }
}

$rows = @()
$rows += New-StatusRow `
    -Area 'Wave 0 non-human pre-live gate' `
    -Status ($(if ($wave0Ready) { 'ready_waiting_on_human_join' } else { 'not_ready' })) `
    -Evidence ($(if ($prelive.present) { "$preliveVerdict; release=$expectedRelease; peers=$peerCount; receipt=$($prelive.path)" } else { 'missing pre-live summary receipt' })) `
    -NextAction 'When both owned clients are joined, run Wait-Wave0LiveGate.ps1 for OMEN apply and then i5 apply reversal.'
$rows += New-StatusRow `
    -Area 'Human-only test register' `
    -Status ($(if ($humanRegisterReady) { 'current' } else { 'missing_or_incomplete' })) `
    -Evidence ($(if ($humanRegister.present) { "$($humanRegister.path); sha256=$($humanRegister.sha256)" } else { 'missing remaining-human-tests.md' })) `
    -NextAction 'Use the register to avoid asking Derek for file transfer, deployment, or log-gathering work.'
$rows += New-StatusRow `
    -Area 'Expected-result grid' `
    -Status ($(if ($expectedGrid) { 'generated_by_prelive' } else { 'missing' })) `
    -Evidence ($(if ($expectedGrid) { $expectedGrid } else { 'Run New-Wave0ExpectedResultGrid.ps1 or Test-Wave0Prelive.ps1.' })) `
    -NextAction 'Use the grid for pre-run bets and observed visual results.'
$rows += New-StatusRow `
    -Area 'Wave 0 stop rule' `
    -Status ($(if ($strategyStopRulePresent -and $wave0ExitArtifactPresent) { 'exit_artifact_present' } elseif ($strategyStopRulePresent) { 'enforced_no_exit_artifact' } else { 'missing_from_strategy' })) `
    -Evidence ($(if ($stopRuleReceipt.present) { "$($stopRuleReceipt.path); verdict=$($stopRuleReceipt.body.verdict); exit_artifact_present=$($stopRuleReceipt.body.exit_artifact_present)" } elseif ($strategy.present) { "$($strategy.path); sha256=$($strategy.sha256)" } else { 'missing strategy doc' })) `
    -NextAction 'Do not start M1/M2 runtime expansion until Wave 0 has a sealed visual packet or named defect.'
$rows += New-StatusRow `
    -Area 'Wave 1 admission / turnkey updates' `
    -Status 'blocked_by_wave0_exit' `
    -Evidence 'Strategy requires Wave 0 sealed visual proof or named defect before M1/M2 expansion.' `
    -NextAction 'Prepare fixtures/docs only; defer runtime admission changes.'
$rows += New-StatusRow `
    -Area 'M7 authority discovery' `
    -Status 'discovery_active_p7_promotion_gated' `
    -Evidence 'M7 authority experiment program and execution handoff authorize synthetic, replay, local-lab, and zero-behavior-change shadow work.' `
    -NextAction 'Run M7-E00 through M7-E03 before native capture; do not promote P7 authority.'
$rows += New-StatusRow `
    -Area 'Wave 2 durable evidence / recipient correctness' `
    -Status 'not_started_in_current_slice' `
    -Evidence 'Strategy sequences this after M1/M2 and Wave 0 exit.' `
    -NextAction 'Use synthetic contracts later: duplicate, isolation, reconnect, lease takeover, restart, WAL replay.'
$rows += New-StatusRow `
    -Area 'Wave 3 real clients / external canary' `
    -Status 'human_gated' `
    -Evidence 'Remaining human register lists dense movement, stutter, fallback, restart, and external canary gates.' `
    -NextAction 'Complete owned two-client Wave 0 first; then run measured Wave 3 canaries.'
$rows += New-StatusRow `
    -Area 'Wave 4 replay / lab / projection' `
    -Status 'future_scope' `
    -Evidence 'Strategy defers replay, turnkey lab, contribution, soak, and signed aggregate work until earlier gates produce sealed evidence.' `
    -NextAction 'Do not claim completion until runnable proof receipts exist for each area.'

$completionVerdict = if ($wave0Ready -and $humanRegisterReady -and $strategyStopRulePresent) {
    'strategy_active_wave0_human_gated'
} else {
    'strategy_status_incomplete'
}

$completionAudit = @()
$completionAudit += New-AuditItem `
    -Requirement 'Wave 0 non-human pre-live evidence exists.' `
    -State ($(if ($wave0Ready) { 'proven_ready_for_join' } else { 'missing_or_failed' })) `
    -Evidence ($(if ($prelive.present) { "$preliveVerdict; $($prelive.path)" } else { 'missing prelive summary' })) `
    -Needed 'Keep prelive green immediately before live join.'
$completionAudit += New-AuditItem `
    -Requirement 'Wave 0 exit artifact exists: sealed two-direction visual evidence or retained named defect.' `
    -State ($(if ($wave0ExitArtifactPresent) { 'exit_artifact_present' } else { 'not_achieved' })) `
    -Evidence ($(if ($stopRuleReceipt.present) { "$($stopRuleReceipt.body.verdict); exit_artifact_present=$($stopRuleReceipt.body.exit_artifact_present)" } else { 'missing stop-rule receipt' })) `
    -Needed 'Join OMEN and i5, run both directions, then seal visual evidence or retain named defect.'
$completionAudit += New-AuditItem `
    -Requirement 'M1/M2 admission and turnkey update work may start.' `
    -State ($(if ($wave0ExitArtifactPresent) { 'eligible_after_operator_review' } else { 'blocked_by_wave0_stop_rule' })) `
    -Evidence 'Strategy stop rule requires Wave 0 exit artifact before M1/M2 expansion.' `
    -Needed 'Do not start runtime admission/update expansion until Wave 0 exits.'
$completionAudit += New-AuditItem `
    -Requirement 'M3/M4a durable evidence and recipient correctness are closed.' `
    -State 'not_achieved' `
    -Evidence 'No sealed N=2/N=10 durable-recipient receipts are in scope for current Wave 0 packet.' `
    -Needed 'After M1/M2, prove duplicate, isolation, reconnect, lease takeover, restart, WAL replay, loss, and double-apply cases.'
$completionAudit += New-AuditItem `
    -Requirement 'M4b/M5 real-client and external canary are closed.' `
    -State 'not_achieved' `
    -Evidence 'Owned two-client Wave 0 live visual proof is still waiting on peer_count >= 2; external canary not started.' `
    -Needed 'Complete owned-client proof, then Companion-only trusted external canary.'
$completionAudit += New-AuditItem `
    -Requirement 'M6/A1-A6 adoption, replay, lab, widening, and signed aggregate work are closed.' `
    -State 'not_achieved' `
    -Evidence 'Strategy defers these until earlier sealed evidence exists.' `
    -Needed 'Produce separate sealed receipts for replay, local lab, contribution path, cohort soak, and signed read-only aggregate.'
$completionAudit += New-AuditItem `
    -Requirement 'M7 network-authority expansion.' `
    -State 'discovery_active_p7_promotion_gated' `
    -Evidence 'M7 discovery is authorized by the experiment program and handoff; behavior-changing P7 promotion remains claim-gated.' `
    -Needed 'Run E00-E03, then native/local shadow evidence; do not include P7 promotion in this completion claim.'

$packet = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = $completionVerdict
    objective = 'Conservative current-state packet for the full roadmap working strategy.'
    strategy = [ordered]@{
        present = $strategy.present
        path = $strategy.path
        sha256 = $strategy.sha256
    }
    human_test_register = [ordered]@{
        present = $humanRegister.present
        path = $humanRegister.path
        sha256 = $humanRegister.sha256
    }
    wave0_prelive = [ordered]@{
        present = $prelive.present
        path = $prelive.path
        sha256 = $prelive.sha256
        verdict = $preliveVerdict
        expected_release = $expectedRelease
        p7_peer_count = $peerCount
        return_packet = $returnPacket
        expected_result_grid = $expectedGrid
        stop_rule_receipt = if ($stopRuleReceipt.present) { $stopRuleReceipt.path } else { '' }
        wave0_exit_artifact_present = [bool]$wave0ExitArtifactPresent
    }
    rows = $rows
    completion_audit = $completionAudit
    full_objective_complete = $false
    human_required_next = @(
        'Join OMEN and i5 to P7 with owned player accounts.',
        'Observe whether the non-applying screen follows the selected applying player.',
        'Classify straight and stutter movement quality.',
        'Repeat after role reversal.'
    )
}

$markdown = @()
$markdown += '# Full roadmap strategy status packet'
$markdown += ''
$markdown += "- Generated UTC: $($packet.generated_utc)"
$markdown += "- Verdict: $($packet.verdict)"
if ($expectedRelease) { $markdown += "- Current expected release: $expectedRelease" }
if ($returnPacket) { $markdown += "- Wave 0 return packet: $returnPacket" }
if ($expectedGrid) { $markdown += "- Expected-result grid: $expectedGrid" }
$markdown += ''
$markdown += '## Current status'
$markdown += ''
$markdown += '| Area | Status | Evidence | Next action |'
$markdown += '|---|---|---|---|'
foreach ($row in $rows) {
    $markdown += "| $(MdEscape $row.area) | $(MdEscape $row.status) | $(MdEscape $row.evidence) | $(MdEscape $row.next_action) |"
}
$markdown += ''
$markdown += '## Human required next'
$markdown += ''
foreach ($item in $packet.human_required_next) {
    $markdown += "- $item"
}
$markdown += ''
$markdown += '## Completion audit'
$markdown += ''
$markdown += '| Requirement | State | Evidence | Needed |'
$markdown += '|---|---|---|---|'
foreach ($item in $completionAudit) {
    $markdown += "| $(MdEscape $item.requirement) | $(MdEscape $item.state) | $(MdEscape $item.evidence) | $(MdEscape $item.needed) |"
}
$markdown += ''
$markdown += 'This packet is intentionally conservative. `ready_waiting_on_human_join` means the non-human pre-live gate passed; it is not proof that the full roadmap objective is complete.'

$jsonPath = Resolve-UnderRepo $OutputJson
$mdPath = Resolve-UnderRepo $OutputMarkdown
foreach ($path in @($jsonPath, $mdPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

[IO.File]::WriteAllText($jsonPath, (($packet | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($mdPath, (($markdown -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Full roadmap strategy status: {0}" -f $packet.verdict)
Write-Host ("JSON: {0}" -f $jsonPath)
Write-Host ("Markdown: {0}" -f $mdPath)
