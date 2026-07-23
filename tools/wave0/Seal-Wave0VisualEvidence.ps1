<#
.SYNOPSIS
Verify and seal the two annotated Wave 0 live-gate visual evidence packets.

.DESCRIPTION
Reads the annotated projections written by Add-Wave0VisualObservation.ps1 for
the first pass and the role reversal. It does not edit either source receipt or
annotation. It writes a derived seal receipt that answers one question:

Did both directions record visual apply/observe evidence, and did the selected
apply role reverse between OMEN and i5?

The real Wave 0 gate expects both source machine receipts to be live movement
receipts with verdict transport_evidence_collected_human_visual_pending. Use
-AllowMockReceipts only for fixture/smoke tests that stop at role preflight.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FirstAnnotatedJson,

    [Parameter(Mandatory = $true)]
    [string]$ReversalAnnotatedJson,

    [string]$OutputJson,

    [switch]$AllowMockReceipts
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Resolve-ExistingPath {
    param([string]$Path)
    $full = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    } else {
        [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
    }
    if (-not (Test-Path -LiteralPath $full)) { throw "file not found: $full" }
    $full
}

function Read-Projection {
    param([string]$Path, [string]$Label)

    $full = Resolve-ExistingPath $Path
    $body = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
    if ($body.schema_version -ne 1 -or $body.projection_type -ne 'wave0_live_gate_annotated') {
        throw "$Label is not a Wave 0 annotated projection: $full"
    }
    if (-not $body.source_receipt -or -not $body.visual_observation) {
        throw "$Label projection is missing source_receipt or visual_observation: $full"
    }
    if (-not $body.source_receipt_sha256) {
        throw "$Label projection is missing source_receipt_sha256: $full"
    }
    [ordered]@{
        label = $Label
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        body = $body
    }
}

function Add-Check {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [bool]$Ok,
        [string]$Detail
    )
    $Checks.Add([ordered]@{
        name = $Name
        ok = $Ok
        detail = $Detail
    }) | Out-Null
}

function ReceiptVerdictAllowed {
    param([string]$Verdict, [bool]$AllowMock)
    if ($Verdict -eq 'transport_evidence_collected_human_visual_pending') { return $true }
    if ($AllowMock -and $Verdict -eq 'role_preflight_passed_stopped_before_motion') { return $true }
    return $false
}

function Observation {
    param($Projection)
    $Projection.body.visual_observation.observation
}

function Interpretation {
    param($Projection)
    [string]$Projection.body.visual_observation.interpretation
}

function SourceReceipt {
    param($Projection)
    $Projection.body.source_receipt
}

$first = Read-Projection $FirstAnnotatedJson 'first_pass'
$reversal = Read-Projection $ReversalAnnotatedJson 'role_reversal'
$checks = [System.Collections.Generic.List[object]]::new()

$firstObs = Observation $first
$reversalObs = Observation $reversal
$firstReceipt = SourceReceipt $first
$reversalReceipt = SourceReceipt $reversal

Add-Check $checks 'first_visual_followed_role' `
    ((Interpretation $first) -in @('first_pass_visual_apply_follows_selected_role_role_reversal_pending','visual_apply_follows_selected_role')) `
    ("first interpretation: {0}" -f (Interpretation $first))

Add-Check $checks 'reversal_visual_followed_role' `
    ((Interpretation $reversal) -eq 'visual_apply_follows_selected_role') `
    ("reversal interpretation: {0}" -f (Interpretation $reversal))

Add-Check $checks 'first_role_reversal_pending_or_not_run' `
    ([string]$firstObs.role_reversal_run -in @('no','not_run')) `
    ("first role_reversal_run: {0}" -f $firstObs.role_reversal_run)

Add-Check $checks 'reversal_marked_yes' `
    ([string]$reversalObs.role_reversal_run -eq 'yes') `
    ("reversal role_reversal_run: {0}" -f $reversalObs.role_reversal_run)

Add-Check $checks 'apply_clients_are_known' `
    ([string]$firstObs.apply_client -in @('omen','i5') -and [string]$reversalObs.apply_client -in @('omen','i5')) `
    ("first apply={0}; reversal apply={1}" -f $firstObs.apply_client, $reversalObs.apply_client)

Add-Check $checks 'observe_clients_are_known' `
    ([string]$firstObs.observe_client -in @('omen','i5') -and [string]$reversalObs.observe_client -in @('omen','i5')) `
    ("first observe={0}; reversal observe={1}" -f $firstObs.observe_client, $reversalObs.observe_client)

Add-Check $checks 'roles_are_complements_first_pass' `
    ([string]$firstObs.apply_client -ne [string]$firstObs.observe_client) `
    ("first apply={0}; observe={1}" -f $firstObs.apply_client, $firstObs.observe_client)

Add-Check $checks 'roles_are_complements_reversal' `
    ([string]$reversalObs.apply_client -ne [string]$reversalObs.observe_client) `
    ("reversal apply={0}; observe={1}" -f $reversalObs.apply_client, $reversalObs.observe_client)

Add-Check $checks 'apply_role_reversed' `
    ([string]$firstObs.apply_client -ne [string]$reversalObs.apply_client) `
    ("first apply={0}; reversal apply={1}" -f $firstObs.apply_client, $reversalObs.apply_client)

Add-Check $checks 'observe_role_reversed' `
    ([string]$firstObs.observe_client -ne [string]$reversalObs.observe_client) `
    ("first observe={0}; reversal observe={1}" -f $firstObs.observe_client, $reversalObs.observe_client)

Add-Check $checks 'first_receipt_verdict_allowed' `
    (ReceiptVerdictAllowed ([string]$firstReceipt.verdict) ([bool]$AllowMockReceipts)) `
    ("first receipt verdict: {0}" -f $firstReceipt.verdict)

Add-Check $checks 'reversal_receipt_verdict_allowed' `
    (ReceiptVerdictAllowed ([string]$reversalReceipt.verdict) ([bool]$AllowMockReceipts)) `
    ("reversal receipt verdict: {0}" -f $reversalReceipt.verdict)

Add-Check $checks 'source_receipts_are_distinct' `
    ([string]$first.body.source_receipt_sha256 -ne [string]$reversal.body.source_receipt_sha256) `
    ("first receipt sha={0}; reversal receipt sha={1}" -f $first.body.source_receipt_sha256, $reversal.body.source_receipt_sha256)

$failed = @($checks | Where-Object { -not $_.ok })
$verdict = if ($failed.Count -eq 0) {
    if ($AllowMockReceipts) { 'wave0_visual_seal_fixture_passed' } else { 'wave0_visual_evidence_sealed' }
} else {
    'wave0_visual_evidence_not_sealed'
}

if (-not $OutputJson) {
    $firstDir = Split-Path -Parent $first.path
    $OutputJson = Join-Path $firstDir 'wave0-visual-seal.json'
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputJson)) {
    [IO.Path]::GetFullPath($OutputJson)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputJson))
}
$outputDir = Split-Path -Parent $outputPath
if ($outputDir) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

$seal = [ordered]@{
    schema_version = 1
    artifact_type = 'wave0_visual_evidence_seal'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = $verdict
    allow_mock_receipts = [bool]$AllowMockReceipts
    first_pass = [ordered]@{
        annotated_projection = $first.path
        annotated_projection_sha256 = $first.sha256
        source_receipt_sha256 = [string]$first.body.source_receipt_sha256
        source_receipt_verdict = [string]$firstReceipt.verdict
        apply_client = [string]$firstObs.apply_client
        observe_client = [string]$firstObs.observe_client
        visual_result = [string]$firstObs.visual_result
        straight_movement = [string]$firstObs.straight_movement
        stutter_movement = [string]$firstObs.stutter_movement
    }
    role_reversal = [ordered]@{
        annotated_projection = $reversal.path
        annotated_projection_sha256 = $reversal.sha256
        source_receipt_sha256 = [string]$reversal.body.source_receipt_sha256
        source_receipt_verdict = [string]$reversalReceipt.verdict
        apply_client = [string]$reversalObs.apply_client
        observe_client = [string]$reversalObs.observe_client
        visual_result = [string]$reversalObs.visual_result
        straight_movement = [string]$reversalObs.straight_movement
        stutter_movement = [string]$reversalObs.stutter_movement
    }
    checks = @($checks)
    failed_checks = @($failed | ForEach-Object { $_.name })
    next_action = if ($failed.Count -eq 0) {
        'Use this seal as the Wave 0 visual evidence index, then confirm the transport receipts support the same verdict.'
    } else {
        'Fix or rerun the failed visual evidence side before treating Wave 0 as sealed.'
    }
}

[IO.File]::WriteAllText($outputPath, (($seal | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 visual seal: {0}" -f $verdict)
Write-Host ("Seal JSON: {0}" -f $outputPath)
if ($failed.Count -gt 0) {
    Write-Host ("Failed checks: {0}" -f (($failed | ForEach-Object { $_.name }) -join ', '))
    exit 1
}
exit 0
