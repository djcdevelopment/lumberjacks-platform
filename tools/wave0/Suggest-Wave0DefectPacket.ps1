<#
.SYNOPSIS
Classify failed Wave 0 visual evidence and emit the named-defect command to run.

.DESCRIPTION
Reads the first/reversal live-gate receipts, annotated visual projections, and
optional visual seal. It writes a recommendation JSON/Markdown artifact with the
defect kind, defect id, summary, and exact New-Wave0DefectPacket.ps1 command.

This script does not create the defect packet itself. It removes judgment from
the fallback path so an agent can produce a consistent named defect after a live
run fails or remains inconclusive.
#>
[CmdletBinding()]
param(
    [string]$FirstReceiptJson = 'captures/wave0-live-gate/result.json',
    [string]$ReversalReceiptJson = 'captures/wave0-live-gate-reversal/result.json',
    [string]$FirstAnnotatedJson = 'captures/wave0-live-gate/result.annotated.json',
    [string]$ReversalAnnotatedJson = 'captures/wave0-live-gate-reversal/result.annotated.json',
    [string]$SealJson = 'captures/wave0-live-seal/visual-seal.json',
    [string]$OutputJson = 'captures/wave0-defect-suggestion.json',
    [string]$OutputMarkdown = 'captures/wave0-defect-suggestion.md'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Resolve-OptionalPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Read-OptionalJson {
    param([string]$Label, [string]$Path)

    $full = Resolve-OptionalPath $Path
    if (-not $full -or -not (Test-Path -LiteralPath $full -PathType Leaf)) {
        return [ordered]@{
            label = $Label
            present = $false
            path = $full
            sha256 = ''
            body = $null
        }
    }

    [ordered]@{
        label = $Label
        present = $true
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        body = (Get-Content -LiteralPath $full -Raw | ConvertFrom-Json)
    }
}

function Quote-Arg {
    param([string]$Value)
    if ($null -eq $Value) { return "''" }
    return "'" + $Value.Replace("'", "''") + "'"
}

function Slug {
    param([string]$Value)
    $slug = ($Value.ToLowerInvariant() -replace '[^a-z0-9]+', '-' -replace '^-|-$', '')
    if ($slug.Length -gt 48) { $slug = $slug.Substring(0, 48).TrimEnd('-') }
    if (-not $slug) { $slug = 'unclassified' }
    $slug
}

function ReceiptVerdict {
    param($Artifact)
    if ($Artifact.present -and $Artifact.body -and $Artifact.body.verdict) { return [string]$Artifact.body.verdict }
    return ''
}

function AnnotationInterpretation {
    param($Artifact)
    if ($Artifact.present -and $Artifact.body -and $Artifact.body.visual_observation) {
        return [string]$Artifact.body.visual_observation.interpretation
    }
    return ''
}

function AnnotationVisualResult {
    param($Artifact)
    if ($Artifact.present -and $Artifact.body -and $Artifact.body.visual_observation) {
        return [string]$Artifact.body.visual_observation.observation.visual_result
    }
    return ''
}

$firstReceipt = Read-OptionalJson 'first_live_gate_receipt' $FirstReceiptJson
$reversalReceipt = Read-OptionalJson 'reversal_live_gate_receipt' $ReversalReceiptJson
$firstAnnotated = Read-OptionalJson 'first_annotated_projection' $FirstAnnotatedJson
$reversalAnnotated = Read-OptionalJson 'reversal_annotated_projection' $ReversalAnnotatedJson
$seal = Read-OptionalJson 'visual_seal' $SealJson

$artifacts = @($firstReceipt, $reversalReceipt, $firstAnnotated, $reversalAnnotated, $seal)
$present = @($artifacts | Where-Object { $_.present })
if ($present.Count -eq 0) { throw 'at least one Wave 0 evidence artifact must be present' }

$failedChecks = if ($seal.present -and $seal.body.failed_checks) { @($seal.body.failed_checks | ForEach-Object { [string]$_ }) } else { @() }
$receiptVerdicts = @((ReceiptVerdict $firstReceipt), (ReceiptVerdict $reversalReceipt)) | Where-Object { $_ }
$interpretations = @((AnnotationInterpretation $firstAnnotated), (AnnotationInterpretation $reversalAnnotated)) | Where-Object { $_ }
$visualResults = @((AnnotationVisualResult $firstAnnotated), (AnnotationVisualResult $reversalAnnotated)) | Where-Object { $_ }

$defectKind = 'other'
$basis = 'No specific classifier matched; inspect the evidence artifacts.'
if ('source_receipts_are_distinct' -in $failedChecks) {
    $defectKind = 'other'
    $basis = 'Both visual projections reference the same source receipt; the live evidence cannot prove role reversal.'
} elseif ('apply_role_reversed' -in $failedChecks -or 'observe_role_reversed' -in $failedChecks -or 'reversal_marked_yes' -in $failedChecks) {
    $defectKind = 'role_reversal_failed'
    $basis = 'The visual seal failed role-reversal checks.'
} elseif ('first_visual_followed_role' -in $failedChecks -or 'reversal_visual_followed_role' -in $failedChecks) {
    if ('did_not_follow_role' -in $visualResults -or 'visual_apply_did_not_follow_selected_role' -in $interpretations) {
        $defectKind = 'visual_did_not_follow_role'
        $basis = 'At least one visual annotation says motion did not follow the selected apply/observe role.'
    } else {
        $defectKind = 'visual_inconclusive'
        $basis = 'At least one required visual role-binding observation is inconclusive or missing.'
    }
} elseif ($receiptVerdicts | Where-Object { $_ -match 'capture|telemetry' }) {
    $defectKind = 'capture_incomplete'
    $basis = 'A live-gate receipt points at capture or telemetry incompleteness.'
} elseif ($receiptVerdicts | Where-Object { $_ -match 'motion_command|apply_role_command' }) {
    $defectKind = 'motion_command_failed'
    $basis = 'A live-gate receipt indicates the bounded motion/apply command path failed.'
} elseif ($receiptVerdicts | Where-Object { $_ -match 'inconclusive|native_motion_only|motion_ready_no_gateway_delta' }) {
    $defectKind = 'transport_inconclusive'
    $basis = 'Transport evidence did not establish the expected Lumberjacks motion path.'
} elseif ($visualResults | Where-Object { $_ -in @('inconclusive','not_observed') }) {
    $defectKind = 'visual_inconclusive'
    $basis = 'A visual annotation is inconclusive or not observed.'
}

$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$defectId = "wave0-$($stamp)-$(Slug $defectKind)"
$summary = switch ($defectKind) {
    'visual_did_not_follow_role' { 'Visual movement did not follow the selected Wave 0 apply/observe role.' }
    'visual_inconclusive' { 'Visual Wave 0 proof was inconclusive or not observed.' }
    'role_reversal_failed' { 'Wave 0 role reversal could not be sealed from the supplied evidence.' }
    'transport_inconclusive' { 'Wave 0 transport evidence was inconclusive for the expected motion path.' }
    'capture_incomplete' { 'Wave 0 capture or telemetry evidence was incomplete.' }
    'motion_command_failed' { 'Wave 0 bounded motion or apply-role command failed.' }
    default { 'Wave 0 visual proof could not be sealed; inspect the indexed evidence.' }
}

$cmdParts = @(
    'tools\wave0\New-Wave0DefectPacket.ps1',
    '-DefectId', (Quote-Arg $defectId),
    '-DefectKind', $defectKind,
    '-Summary', (Quote-Arg $summary)
)
if ($firstReceipt.present) { $cmdParts += @('-FirstReceiptJson', (Quote-Arg $firstReceipt.path)) }
if ($reversalReceipt.present) { $cmdParts += @('-ReversalReceiptJson', (Quote-Arg $reversalReceipt.path)) }
if ($firstAnnotated.present) { $cmdParts += @('-FirstAnnotatedJson', (Quote-Arg $firstAnnotated.path)) }
if ($reversalAnnotated.present) { $cmdParts += @('-ReversalAnnotatedJson', (Quote-Arg $reversalAnnotated.path)) }
if ($seal.present) { $cmdParts += @('-SealJson', (Quote-Arg $seal.path)) }
$cmdParts += @('-Notes', (Quote-Arg $basis))

$suggestion = [ordered]@{
    schema_version = 1
    artifact_type = 'wave0_defect_packet_suggestion'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_defect_packet_suggested'
    defect_id = $defectId
    defect_kind = $defectKind
    summary = $summary
    basis = $basis
    failed_checks = $failedChecks
    receipt_verdicts = $receiptVerdicts
    visual_interpretations = $interpretations
    visual_results = $visualResults
    command = ($cmdParts -join ' ')
    artifacts = @($artifacts | ForEach-Object {
        [ordered]@{
            label = $_.label
            present = $_.present
            path = $_.path
            sha256 = $_.sha256
        }
    })
}

$jsonPath = Resolve-OptionalPath $OutputJson
$mdPath = Resolve-OptionalPath $OutputMarkdown
foreach ($path in @($jsonPath, $mdPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

$markdown = @()
$markdown += "# Wave 0 defect suggestion: $defectId"
$markdown += ''
$markdown += "- Defect kind: $defectKind"
$markdown += "- Summary: $summary"
$markdown += "- Basis: $basis"
$markdown += ''
$markdown += '## Command'
$markdown += ''
$markdown += '```powershell'
$markdown += $suggestion.command
$markdown += '```'
$markdown += ''
$markdown += '## Evidence'
$markdown += ''
$markdown += '| Artifact | Present | SHA-256 | Path |'
$markdown += '|---|---:|---|---|'
foreach ($artifact in $suggestion.artifacts) {
    $markdown += "| $($artifact.label) | $($artifact.present) | $($artifact.sha256) | $($artifact.path) |"
}

[IO.File]::WriteAllText($jsonPath, (($suggestion | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($mdPath, (($markdown -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 defect suggestion: {0}" -f $defectKind)
Write-Host ("Suggestion JSON: {0}" -f $jsonPath)
Write-Host ("Suggestion Markdown: {0}" -f $mdPath)
