#Requires -Version 5.1
<#
.SYNOPSIS
Fail closed unless a C6 manifest preserves the two-client motion ordering contract.

.DESCRIPTION
C6 clients advance their own action lists independently. This verifier requires
the observer-alignment action immediately before OMEN's gap drive so i5 begins
measuring the correlated gap/resync before the sender emits it. It is read-only
except for the optional local JSON receipt and runs before remote state is armed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ScenarioPath,

    [Parameter(Mandatory)]
    [string] $RunId,

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$scenario = Get-Content -LiteralPath $ScenarioPath -Raw -Encoding utf8 |
    ConvertFrom-Json
$omenActions = @($scenario.actions | Where-Object client -eq 'omen')
$i5Actions = @($scenario.actions | Where-Object client -eq 'i5')

function Get-Action([object[]] $Actions, [string] $Id) {
    @($Actions | Where-Object id -eq $Id | Select-Object -First 1)[0]
}

function Get-ActionIndex([object[]] $Actions, [string] $Id) {
    for ($index = 0; $index -lt $Actions.Count; $index++) {
        if ([string]$Actions[$index].id -eq $Id) { return $index }
    }
    return -1
}

$align = Get-Action $omenActions 'omen-c6-gap-observer-align'
$driveGap = Get-Action $omenActions 'omen-c6-drive-gap'
$observeGap = Get-Action $i5Actions 'i5-c6-observe-gap'
$alignIndex = Get-ActionIndex $omenActions 'omen-c6-gap-observer-align'
$driveGapIndex = Get-ActionIndex $omenActions 'omen-c6-drive-gap'

$checks = [ordered]@{
    profile_is_c6 = [string]$scenario.profile -eq 'c6'
    run_id_matches = [string]$scenario.run_id -eq $RunId
    action_ids_unique_per_client = @(
        $scenario.actions |
            Group-Object { "$($_.client):$($_.id)" } |
            Where-Object Count -gt 1).Count -eq 0
    gap_observer_alignment_present =
        $null -ne $align -and [string]$align.kind -eq 'wait'
    gap_observer_alignment_bounded =
        $null -ne $align -and
        [double]$align.duration_seconds -ge 4.0 -and
        [double]$align.deadline_seconds -ge [double]$align.duration_seconds
    gap_observer_alignment_immediately_precedes_drive =
        $alignIndex -ge 0 -and $driveGapIndex -eq ($alignIndex + 1)
    gap_pair_present =
        $null -ne $driveGap -and
        [string]$driveGap.kind -eq 'motion_drive_gap' -and
        $null -ne $observeGap -and
        [string]$observeGap.kind -eq 'motion_observe_gap'
    gap_pair_correlation_matches =
        $null -ne $driveGap -and
        $null -ne $observeGap -and
        -not [string]::IsNullOrWhiteSpace([string]$driveGap.target_tag) -and
        [string]$driveGap.target_tag -eq [string]$observeGap.target_tag
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c6_scenario_coverage'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    scenario_sha256 =
        (Get-FileHash -LiteralPath $ScenarioPath -Algorithm SHA256).Hash.ToLowerInvariant()
    action_count = @($scenario.actions).Count
    checks = $checks
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    failed_checks = @($failed | ForEach-Object Key)
}

$json = $receipt | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
    $directory = Split-Path -Parent $absoluteOutput
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $absoluteOutput,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}
$json
if ($failed.Count -gt 0) { exit 1 }
