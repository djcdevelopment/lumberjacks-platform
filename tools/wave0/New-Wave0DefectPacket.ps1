<#
.SYNOPSIS
Create a named Wave 0 defect packet from failed or inconclusive live-gate evidence.

.DESCRIPTION
Wave 0 has two acceptable exits before Wave 1/M1/M2 work starts:

- a sealed two-direction visual evidence packet; or
- a named defect packet explaining why visual proof cannot be sealed.

This script creates the second artifact without editing the source receipts. It
indexes the live-gate receipt(s), visual annotation projection(s), and optional
seal receipt by path and SHA-256, then writes a JSON packet and Markdown summary.
It is useful when the live run fails, is inconclusive, does not reverse roles, or
the seal verifier reports wave0_visual_evidence_not_sealed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^wave0-[a-z0-9][a-z0-9-]{2,80}$')]
    [string]$DefectId,

    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'visual_did_not_follow_role',
        'visual_inconclusive',
        'role_reversal_failed',
        'transport_inconclusive',
        'capture_incomplete',
        'motion_command_failed',
        'other')]
    [string]$DefectKind,

    [Parameter(Mandatory = $true)]
    [string]$Summary,

    [string]$FirstReceiptJson = '',
    [string]$ReversalReceiptJson = '',
    [string]$FirstAnnotatedJson = '',
    [string]$ReversalAnnotatedJson = '',
    [string]$SealJson = '',
    [string]$Notes = '',
    [string]$OutputJson,
    [string]$OutputMarkdown
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Resolve-OptionalPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Read-Artifact {
    param([string]$Label, [string]$Path)

    $full = Resolve-OptionalPath $Path
    if (-not $full) {
        return [ordered]@{
            label = $Label
            present = $false
            path = ''
            sha256 = ''
            verdict = ''
            artifact_type = ''
            summary = 'not supplied'
        }
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label artifact not found: $full"
    }
    $body = $null
    try { $body = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json } catch { }
    $verdict = if ($body -and $body.verdict) { [string]$body.verdict }
        elseif ($body -and $body.final_gate_state) { [string]$body.final_gate_state }
        else { '' }
    $artifactType = if ($body -and $body.artifact_type) { [string]$body.artifact_type }
        elseif ($body -and $body.projection_type) { [string]$body.projection_type }
        elseif ($body -and $body.annotation_type) { [string]$body.annotation_type }
        else { '' }
    [ordered]@{
        label = $Label
        present = $true
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        verdict = $verdict
        artifact_type = $artifactType
        summary = if ($body -and $body.next_action) { [string]$body.next_action } else { '' }
    }
}

function MdEscape {
    param([string]$Value)
    if ($null -eq $Value) { return '' }
    return $Value.Replace('|', '\|')
}

if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    $OutputJson = "captures/wave0-defects/$DefectId/packet.json"
}
if ([string]::IsNullOrWhiteSpace($OutputMarkdown)) {
    $OutputMarkdown = "captures/wave0-defects/$DefectId/packet.md"
}

$artifacts = @(
    Read-Artifact 'first_live_gate_receipt' $FirstReceiptJson
    Read-Artifact 'reversal_live_gate_receipt' $ReversalReceiptJson
    Read-Artifact 'first_annotated_projection' $FirstAnnotatedJson
    Read-Artifact 'reversal_annotated_projection' $ReversalAnnotatedJson
    Read-Artifact 'visual_seal' $SealJson
)

$presentArtifacts = @($artifacts | Where-Object { $_.present })
if ($presentArtifacts.Count -eq 0) {
    throw 'at least one evidence artifact must be supplied'
}

$seal = @($artifacts | Where-Object { $_.label -eq 'visual_seal' })[0]
$evidenceVerdict = if ($seal.present -and $seal.verdict) { $seal.verdict }
    else { 'defect_packet_without_seal' }
if ($seal.present -and $evidenceVerdict -eq 'wave0_visual_evidence_sealed') {
    throw 'refusing to retain a Wave 0 defect packet from a sealed visual-evidence receipt; use the visual seal as the Wave 0 exit artifact instead'
}

$packet = [ordered]@{
    schema_version = 1
    artifact_type = 'wave0_named_defect_packet'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    defect_id = $DefectId
    defect_kind = $DefectKind
    summary = $Summary
    notes = $Notes
    verdict = 'wave0_named_defect_packet_retained'
    evidence_verdict = $evidenceVerdict
    artifacts = @($artifacts)
    next_action = 'Use this packet as the named Wave 0 defect input. Do not expand into Wave 1 until the defect is triaged or a new live visual proof is sealed.'
}

$jsonPath = Resolve-OptionalPath $OutputJson
$mdPath = Resolve-OptionalPath $OutputMarkdown
foreach ($path in @($jsonPath, $mdPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

$markdown = @()
$markdown += "# Wave 0 defect packet: $DefectId"
$markdown += ''
$markdown += "- Generated UTC: $($packet.generated_utc)"
$markdown += "- Defect kind: $DefectKind"
$markdown += "- Evidence verdict: $evidenceVerdict"
$markdown += "- Summary: $Summary"
if ($Notes) { $markdown += "- Notes: $Notes" }
$markdown += ''
$markdown += '## Evidence artifacts'
$markdown += ''
$markdown += '| Artifact | Present | Verdict/state | SHA-256 | Path |'
$markdown += '|---|---:|---|---|---|'
foreach ($artifact in $artifacts) {
    $markdown += "| $(MdEscape $artifact.label) | $($artifact.present) | $(MdEscape $artifact.verdict) | $($artifact.sha256) | $(MdEscape $artifact.path) |"
}
$markdown += ''
$markdown += '## Next action'
$markdown += ''
$markdown += $packet.next_action

[IO.File]::WriteAllText($jsonPath, (($packet | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($mdPath, (($markdown -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 defect packet: {0}" -f $DefectId)
Write-Host ("JSON: {0}" -f $jsonPath)
Write-Host ("Markdown: {0}" -f $mdPath)
