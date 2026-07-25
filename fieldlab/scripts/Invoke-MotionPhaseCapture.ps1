<#
.SYNOPSIS
Run and summarize a bounded motion-phase capture through the local Companion.

.DESCRIPTION
Starts the existing Companion transport-truth capture, downloads its raw JSONL,
and invokes Summarize-MotionPhaseCapture.ps1. This script controls collection only;
it does not launch Valheim, move a player, or change transport switches.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 600)]
    [int] $DurationSeconds = 60,

    [ValidateRange(1, 30)]
    [int] $IntervalSeconds = 1,

    [string] $Label = 'cre-e06-motion-phase',

    [string] $CompanionUrl = 'http://127.0.0.1:8080',

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $OutputDirectory = Join-Path $repoRoot "fieldlab\runs\motion-phase\$stamp"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$body = @{
    duration_seconds = $DurationSeconds
    interval_seconds = $IntervalSeconds
    label = $Label
} | ConvertTo-Json

Write-Host "capturing $DurationSeconds seconds through $CompanionUrl"
$summary = Invoke-RestMethod `
    -Method Post `
    -Uri "$($CompanionUrl.TrimEnd('/'))/api/v0/companion/transport-capture" `
    -ContentType 'application/json' `
    -Body $body `
    -TimeoutSec ([Math]::Max(60, $DurationSeconds + 45))
if ($null -eq $summary -or [string]::IsNullOrWhiteSpace([string] $summary.run_id)) {
    throw 'Companion did not return a capture run_id'
}

$summaryPath = Join-Path $OutputDirectory 'companion-summary.json'
$samplesPath = Join-Path $OutputDirectory 'samples.jsonl'
$phasePath = Join-Path $OutputDirectory 'motion-phase-summary.json'
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8

$runId = [Uri]::EscapeDataString([string] $summary.run_id)
$samplesUrl = "$($CompanionUrl.TrimEnd('/'))/api/v0/companion/transport-capture/$runId/samples.jsonl"
Invoke-WebRequest -UseBasicParsing -Uri $samplesUrl -OutFile $samplesPath -TimeoutSec 60

& (Join-Path $PSScriptRoot 'Summarize-MotionPhaseCapture.ps1') `
    -SamplesPath $samplesPath `
    -OutputPath $phasePath | Out-Null

$receipt = [ordered] @{
    schema_version = 1
    event_type = 'motion_phase.capture_complete'
    run_id = $summary.run_id
    output_directory = (Resolve-Path -LiteralPath $OutputDirectory).Path
    companion_summary = $summaryPath
    samples = $samplesPath
    motion_phase_summary = $phasePath
}
$receipt | ConvertTo-Json -Depth 4
