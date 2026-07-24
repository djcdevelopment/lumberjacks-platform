<#
.SYNOPSIS
Generate the Wave 0 expected-result grid for the two-client visual gate.

.DESCRIPTION
The full roadmap strategy requires an expected-result grid before live testing.
This script writes a small JSON receipt and Markdown table that Derek can copy
or annotate before the live pass. It does not contact P7, OMEN, or i5.
#>
[CmdletBinding()]
param(
    [string]$OutputJson = 'captures/wave0-expected-result-grid.json',
    [string]$OutputMarkdown = 'captures/wave0-expected-result-grid.md'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function New-Row {
    param(
        [string]$RowId,
        [string]$Phase,
        [string]$Trigger,
        [string]$ExpectedMachineEvidence,
        [string]$ExpectedHumanObservation,
        [string]$StopIf
    )

    [ordered]@{
        id = $RowId
        phase = $Phase
        trigger = $Trigger
        expected_machine_evidence = $ExpectedMachineEvidence
        expected_human_observation = $ExpectedHumanObservation
        stop_if = $StopIf
        derek_bet = ''
        observed_result = ''
    }
}

function MdEscape {
    param([string]$Value)

    if ($null -eq $Value) { return '' }
    return $Value.Replace('|', '\|')
}

$rows = @()
$rows += New-Row `
        -RowId 'W0-1' `
        -Phase 'preflight' `
        -Trigger 'Run Test-Wave0Prelive.ps1 before live join.' `
        -ExpectedMachineEvidence 'ready_for_derek_two_client_join; P7/OMEN/i5 release and package hashes align; human-test register and this grid are current.' `
        -ExpectedHumanObservation 'No player observation required.' `
        -StopIf 'Any pre-live gate fails or release identities drift.'
$rows += New-Row `
        -RowId 'W0-2' `
        -Phase 'join' `
        -Trigger 'OMEN and i5 join P7 with the owned test accounts.' `
        -ExpectedMachineEvidence 'P7 live-gate receipt records fresh ready heartbeat with peer_count >= 2 and player names present if available.' `
        -ExpectedHumanObservation 'Both clients reach playable world state.' `
        -StopIf 'Peer count stays below 2, server full returns, or either client cannot enter world.'
$rows += New-Row `
        -RowId 'W0-3' `
        -Phase 'first_direction' `
        -Trigger 'Run Wait-Wave0LiveGate.ps1 -DesiredApplyClient omen.' `
        -ExpectedMachineEvidence 'Role preflight verifies OMEN apply=true and i5 apply=false; capture bundles are retained for both machines; bounded motion command completes.' `
        -ExpectedHumanObservation 'i5 observing screen follows OMEN-applied movement; straight/stutter quality is classifiable.' `
        -StopIf 'Both clients apply, neither applies, capture fails, or observed movement does not follow role selection.'
$rows += New-Row `
        -RowId 'W0-4' `
        -Phase 'role_reversal' `
        -Trigger 'Run Wait-Wave0LiveGate.ps1 -DesiredApplyClient i5.' `
        -ExpectedMachineEvidence 'Role preflight verifies i5 apply=true and OMEN apply=false; second capture bundle pair is retained; bounded motion command completes.' `
        -ExpectedHumanObservation 'OMEN observing screen follows i5-applied movement; result follows role, not machine/account.' `
        -StopIf 'Role split does not reverse, capture fails, or visual result contradicts first direction.'
$rows += New-Row `
        -RowId 'W0-5' `
        -Phase 'seal_or_defect' `
        -Trigger 'Annotate both directions, then run Seal-Wave0VisualEvidence.ps1.' `
        -ExpectedMachineEvidence 'Seal verifies distinct source receipts, valid annotated projections, role reversal, and allowed live-gate verdicts.' `
        -ExpectedHumanObservation 'If proof is inconclusive, classify a named defect instead of advancing.' `
        -StopIf 'Seal fails and Suggest-Wave0DefectPacket.ps1 cannot produce a named fallback.'

$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_expected_result_grid_ready'
    purpose = 'Expected-result grid required before Wave 0 two-client live testing.'
    rows = $rows
    commands = [ordered]@{
        prelive = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Test-Wave0Prelive.ps1 -OutputDirectory captures\wave0-prelive-current'
        first_direction = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient omen -OutputJson captures\wave0-live-gate\result.json'
        reversal = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient i5 -OutputJson captures\wave0-live-gate-reversal\result.json'
        seal = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Seal-Wave0VisualEvidence.ps1 -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -OutputJson captures\wave0-live-seal\visual-seal.json'
    }
}

$markdown = @()
$markdown += '# Wave 0 expected-result grid'
$markdown += ''
$markdown += "- Generated UTC: $($receipt.generated_utc)"
$markdown += "- Verdict: $($receipt.verdict)"
$markdown += ''
$markdown += 'Fill `Derek bet` before the live action if useful, then fill `Observed result` from the two screens. Machine receipts remain authoritative for transport facts.'
$markdown += ''
$markdown += '| ID | Phase | Trigger | Expected machine evidence | Expected human observation | Derek bet | Observed result | Stop if |'
$markdown += '|---|---|---|---|---|---|---|---|'
foreach ($row in $rows) {
    $markdown += "| $($row.id) | $(MdEscape $row.phase) | $(MdEscape $row.trigger) | $(MdEscape $row.expected_machine_evidence) | $(MdEscape $row.expected_human_observation) |  |  | $(MdEscape $row.stop_if) |"
}
$markdown += ''
$markdown += '## Commands'
$markdown += ''
$markdown += '```powershell'
$markdown += $receipt.commands.prelive
$markdown += $receipt.commands.first_direction
$markdown += $receipt.commands.reversal
$markdown += $receipt.commands.seal
$markdown += '```'

$jsonPath = Resolve-UnderRepo $OutputJson
$mdPath = Resolve-UnderRepo $OutputMarkdown
foreach ($path in @($jsonPath, $mdPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

[IO.File]::WriteAllText($jsonPath, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($mdPath, (($markdown -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 expected-result grid: {0}" -f $receipt.verdict)
Write-Host ("JSON: {0}" -f $jsonPath)
Write-Host ("Markdown: {0}" -f $mdPath)
