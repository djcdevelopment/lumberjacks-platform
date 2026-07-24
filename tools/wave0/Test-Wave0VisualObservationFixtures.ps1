<#
.SYNOPSIS
Fixture-test Wave 0 visual observation sidecar behavior.

.DESCRIPTION
Exercises Add-Wave0VisualObservation.ps1 without Valheim clients. It proves:

- the source machine receipt is not modified;
- the annotation and projection are written as sidecars;
- duplicate annotation writes fail unless -Force is provided;
- an observation cannot name the same apply and observe client.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-visual-observation-fixtures'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
$caseRoot = Join-Path $outRoot ('case-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null

$receiptPath = Join-Path $caseRoot 'fixture-live-gate.json'
$annotationPath = Join-Path $caseRoot 'fixture-live-gate.visual-observation.json'
$projectionPath = Join-Path $caseRoot 'fixture-live-gate.annotated.json'

$receipt = [ordered]@{
    schema_version = 1
    run_id = 'fixture-visual-observation'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'transport_evidence_collected_human_visual_pending'
    desired_apply_client = 'omen'
    pattern = 'straight_north'
    motion_duration_seconds = 10
    capture_duration_seconds = 30
    p7_peer_check = [ordered]@{
        peer_count = 2
        players = @('fixture-omen', 'fixture-i5')
    }
}
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
$receiptHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $receiptPath).Hash.ToLowerInvariant()

function Invoke-Annotation {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [int]$ExpectedExitCode
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Add-Wave0VisualObservation.ps1') @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne $ExpectedExitCode) {
        throw "$Name expected exit code $ExpectedExitCode but got $exitCode`n$((@($output) | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)"
    }
    [ordered]@{
        name = $Name
        exit_code = $exitCode
        output_tail = @($output | Select-Object -Last 8 | ForEach-Object { [string]$_ })
    }
}

$cases = @()
$common = @(
    '-ReceiptJson', $receiptPath,
    '-AnnotationJson', $annotationPath,
    '-ProjectionJson', $projectionPath,
    '-ApplyClient', 'omen',
    '-ObserveClient', 'i5',
    '-VisualResult', 'followed_role',
    '-StraightMovement', 'smooth',
    '-StutterMovement', 'mixed',
    '-RoleReversalRun', 'no'
)

$cases += Invoke-Annotation -Name 'first-write' -Arguments $common -ExpectedExitCode 0
$cases += Invoke-Annotation -Name 'duplicate-rejected' -Arguments $common -ExpectedExitCode 1
$cases += Invoke-Annotation -Name 'force-rewrite' -Arguments (@($common) + @('-Force')) -ExpectedExitCode 0
$cases += Invoke-Annotation -Name 'same-client-rejected' -Arguments @(
    '-ReceiptJson', $receiptPath,
    '-AnnotationJson', (Join-Path $caseRoot 'same-client.visual-observation.json'),
    '-ProjectionJson', (Join-Path $caseRoot 'same-client.annotated.json'),
    '-ApplyClient', 'omen',
    '-ObserveClient', 'omen',
    '-VisualResult', 'followed_role'
) -ExpectedExitCode 1

$receiptHashAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $receiptPath).Hash.ToLowerInvariant()
if ($receiptHashBefore -ne $receiptHashAfter) {
    throw "source receipt hash changed: before=$receiptHashBefore after=$receiptHashAfter"
}
if (-not (Test-Path -LiteralPath $annotationPath -PathType Leaf)) { throw "annotation sidecar missing: $annotationPath" }
if (-not (Test-Path -LiteralPath $projectionPath -PathType Leaf)) { throw "annotated projection missing: $projectionPath" }

$annotation = Get-Content -LiteralPath $annotationPath -Raw | ConvertFrom-Json
$projection = Get-Content -LiteralPath $projectionPath -Raw | ConvertFrom-Json
if ([string]$annotation.annotation_type -ne 'wave0_visual_observation') { throw "unexpected annotation_type: $($annotation.annotation_type)" }
if ([string]$projection.projection_type -ne 'wave0_live_gate_annotated') { throw "unexpected projection_type: $($projection.projection_type)" }
if ([string]$projection.source_receipt_sha256 -ne $receiptHashBefore) { throw 'projection does not preserve source receipt hash' }
if ([string]$projection.final_gate_state -ne 'role_reversal_required') { throw "unexpected final_gate_state: $($projection.final_gate_state)" }

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_visual_observation_fixture_checks_passed'
    output_directory = $outRoot
    case_directory = $caseRoot
    receipt_json = $receiptPath
    receipt_sha256 = $receiptHashBefore
    annotation_json = $annotationPath
    projection_json = $projectionPath
    cases = $cases
}
$summaryPath = Join-Path $outRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 visual observation fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
