<#
.SYNOPSIS
Attach Derek's visual observation to a Wave 0 live-gate receipt without editing history.

.DESCRIPTION
Reads a Start-Wave0LiveGate.ps1 receipt, writes an immutable sidecar annotation,
and writes a derived annotated projection. The original receipt is never modified.

Use this after a live two-client course. The machine receipt proves transport and
capture facts; this annotation records the one thing automation cannot see: which
client visibly applied Lumberjacks motion and how the motion looked.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReceiptJson,

    [ValidateSet('omen','i5','unknown')]
    [string]$ApplyClient = 'unknown',

    [ValidateSet('omen','i5','unknown')]
    [string]$ObserveClient = 'unknown',

    [ValidateSet('followed_role','did_not_follow_role','inconclusive','not_observed')]
    [string]$VisualResult = 'inconclusive',

    [ValidateSet('smooth','glidey','teleporting','mixed','not_tested')]
    [string]$StraightMovement = 'not_tested',

    [ValidateSet('smooth','glidey','teleporting','mixed','not_tested')]
    [string]$StutterMovement = 'not_tested',

    [ValidateSet('yes','no','not_run')]
    [string]$RoleReversalRun = 'not_run',

    [string]$Notes = '',

    [string]$AnnotationJson,

    [string]$ProjectionJson,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$receiptPath = [IO.Path]::GetFullPath($ReceiptJson)
if (-not (Test-Path -LiteralPath $receiptPath)) {
    throw "receipt not found: $receiptPath"
}

$receiptRaw = Get-Content -LiteralPath $receiptPath -Raw
$receipt = $receiptRaw | ConvertFrom-Json
if ($receipt.schema_version -ne 1 -or -not $receipt.run_id -or -not $receipt.verdict) {
    throw "not a recognized Wave 0 live-gate receipt: $receiptPath"
}

if ($ApplyClient -ne 'unknown' -and $ObserveClient -ne 'unknown' -and $ApplyClient -eq $ObserveClient) {
    throw "ApplyClient and ObserveClient must differ unless one is unknown"
}

if (-not $AnnotationJson) {
    $AnnotationJson = [IO.Path]::ChangeExtension($receiptPath, '.visual-observation.json')
}
if (-not $ProjectionJson) {
    $ProjectionJson = [IO.Path]::ChangeExtension($receiptPath, '.annotated.json')
}

$annotationPath = if ([IO.Path]::IsPathRooted($AnnotationJson)) {
    [IO.Path]::GetFullPath($AnnotationJson)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location) $AnnotationJson))
}
$projectionPath = if ([IO.Path]::IsPathRooted($ProjectionJson)) {
    [IO.Path]::GetFullPath($ProjectionJson)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location) $ProjectionJson))
}

foreach ($path in @($annotationPath, $projectionPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

if ((Test-Path -LiteralPath $annotationPath) -and -not $Force) {
    throw "annotation already exists: $annotationPath; pass -Force to replace the sidecar"
}
if ((Test-Path -LiteralPath $projectionPath) -and -not $Force) {
    throw "projection already exists: $projectionPath; pass -Force to replace the derived projection"
}

$receiptHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $receiptPath).Hash.ToLowerInvariant()
$annotation = [ordered]@{
    schema_version = 1
    annotation_type = 'wave0_visual_observation'
    annotated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    receipt = [ordered]@{
        path = $receiptPath
        sha256 = $receiptHash
        run_id = [string]$receipt.run_id
        verdict = [string]$receipt.verdict
    }
    observation = [ordered]@{
        apply_client = $ApplyClient
        observe_client = $ObserveClient
        visual_result = $VisualResult
        straight_movement = $StraightMovement
        stutter_movement = $StutterMovement
        role_reversal_run = $RoleReversalRun
        notes = $Notes
    }
    interpretation = if ($VisualResult -eq 'followed_role' -and $RoleReversalRun -eq 'yes') {
        'visual_apply_follows_selected_role'
    } elseif ($VisualResult -eq 'followed_role') {
        'first_pass_visual_apply_follows_selected_role_role_reversal_pending'
    } elseif ($VisualResult -eq 'did_not_follow_role') {
        'visual_apply_did_not_follow_selected_role'
    } else {
        'visual_result_inconclusive'
    }
}

$projection = [ordered]@{
    schema_version = 1
    projection_type = 'wave0_live_gate_annotated'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    source_receipt_sha256 = $receiptHash
    source_receipt = $receipt
    visual_observation = $annotation
    final_gate_state = if ($annotation.interpretation -eq 'visual_apply_follows_selected_role') {
        'first_role_and_reversal_visual_evidence_recorded'
    } elseif ($annotation.interpretation -eq 'first_pass_visual_apply_follows_selected_role_role_reversal_pending') {
        'role_reversal_required'
    } elseif ($annotation.interpretation -eq 'visual_apply_did_not_follow_selected_role') {
        'failed_visual_role_binding'
    } else {
        'visual_evidence_inconclusive'
    }
    next_action = if ($annotation.interpretation -eq 'first_pass_visual_apply_follows_selected_role_role_reversal_pending') {
        'Run Start-Wave0LiveGate.ps1 again after reversing apply/observe roles, then annotate the reversal.'
    } elseif ($annotation.interpretation -eq 'visual_apply_follows_selected_role') {
        'Use this annotated packet as Wave 0 visual evidence input; continue only if transport receipt also supports the verdict.'
    } else {
        'Inspect transport receipt and visual notes before rerunning the live course.'
    }
}

[IO.File]::WriteAllText(
    $annotationPath,
    (($annotation | ConvertTo-Json -Depth 14) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    $projectionPath,
    (($projection | ConvertTo-Json -Depth 18) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))

Write-Host ("Visual annotation: {0}" -f $annotation.interpretation)
Write-Host ("Annotation JSON: {0}" -f $annotationPath)
Write-Host ("Annotated projection: {0}" -f $projectionPath)
