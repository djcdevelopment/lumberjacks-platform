#Requires -Version 5.1
<#
.SYNOPSIS
Fail closed unless the C10a mount manifest proves both riders and forced disconnect reclaim.

.DESCRIPTION
The clients advance independently. This verifier requires paired rendered drive/observe
legs, an in-saddle Lumberjacks socket abort with a separate observer, an explicit recovery
settle, a reverse-rider leg, release gates, and proof holds. It runs before remote state is
changed.
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
$omen = @($scenario.actions | Where-Object client -eq 'omen')
$i5 = @($scenario.actions | Where-Object client -eq 'i5')

function Get-Action([object[]] $Actions, [string] $Id) {
    @($Actions | Where-Object id -eq $Id | Select-Object -First 1)[0]
}

function Get-Index([object[]] $Actions, [string] $Id) {
    for ($index = 0; $index -lt $Actions.Count; $index++) {
        if ([string]$Actions[$index].id -eq $Id) { return $index }
    }
    return -1
}

$omenObserveOne = Get-Action $omen 'omen-c10a-mount-observe-i5'
$i5DriveOne = Get-Action $i5 'i5-c10a-mount-drive-omen'
$omenReleaseOne = Get-Action $omen 'omen-c10a-mount-first-release'
$omenReclaim = Get-Action $omen 'omen-c10a-mount-disconnect-reclaim'
$i5ObserveReclaim = Get-Action $i5 'i5-c10a-mount-observe-reclaim'
$omenReclaimSettle = Get-Action $omen 'omen-c10a-mount-reclaim-settle'
$i5ReclaimSettle = Get-Action $i5 'i5-c10a-mount-reclaim-settle'
$omenDriveTwo = Get-Action $omen 'omen-c10a-mount-drive-i5'
$i5ObserveTwo = Get-Action $i5 'i5-c10a-mount-observe-omen'
$i5ReleaseTwo = Get-Action $i5 'i5-c10a-mount-second-release'
$omenHold = Get-Action $omen 'omen-c10a-mount-proof-hold'
$i5Hold = Get-Action $i5 'i5-c10a-mount-proof-hold'

$checks = [ordered]@{
    profile_is_c10a_mount = [string]$scenario.profile -eq 'c10a-mount'
    run_id_matches = [string]$scenario.run_id -eq $RunId
    action_ids_unique_per_client = @(
        $scenario.actions |
            Group-Object { "$($_.client):$($_.id)" } |
            Where-Object Count -gt 1).Count -eq 0
    deterministic_spawn_and_two_replica_waits =
        $null -ne (Get-Action $omen 'omen-c10a-mount-spawn') -and
        $null -ne (Get-Action $omen 'omen-c10a-mount-wait') -and
        $null -ne (Get-Action $i5 'i5-c10a-mount-wait')
    first_leg_pair_present =
        $null -ne $omenObserveOne -and [string]$omenObserveOne.kind -eq 'saddle_observe' -and
        $null -ne $i5DriveOne -and [string]$i5DriveOne.kind -eq 'saddle_drive'
    first_leg_observer_covers_driver =
        $null -ne $omenObserveOne -and $null -ne $i5DriveOne -and
        [double]$omenObserveOne.duration_seconds -ge [double]$i5DriveOne.duration_seconds
    first_release_gate_present =
        $null -ne $omenReleaseOne -and
        [string]$omenReleaseOne.kind -eq 'saddle_wait_released' -and
        [double]$omenReleaseOne.deadline_seconds -ge 15
    forced_disconnect_reclaim_is_paired =
        $null -ne $omenReclaim -and
        [string]$omenReclaim.kind -eq 'saddle_disconnect_reclaim' -and
        $null -ne $i5ObserveReclaim -and
        [string]$i5ObserveReclaim.kind -eq 'saddle_observe_reclaim' -and
        [double]$omenReclaim.duration_seconds -ge 5 -and
        [double]$omenReclaim.deadline_seconds -ge 45 -and
        [double]$i5ObserveReclaim.deadline_seconds -ge 45
    reclaim_follows_first_release =
        (Get-Index $omen 'omen-c10a-mount-disconnect-reclaim') -gt
        (Get-Index $omen 'omen-c10a-mount-first-release')
    recovery_settle_precedes_reverse_leg =
        $null -ne $omenReclaimSettle -and $null -ne $i5ReclaimSettle -and
        [double]$omenReclaimSettle.duration_seconds -ge 5 -and
        [double]$i5ReclaimSettle.duration_seconds -ge 5 -and
        (Get-Index $omen 'omen-c10a-mount-drive-i5') -eq
        ((Get-Index $omen 'omen-c10a-mount-reclaim-settle') + 1) -and
        (Get-Index $i5 'i5-c10a-mount-observe-omen') -eq
        ((Get-Index $i5 'i5-c10a-mount-reclaim-settle') + 1)
    reverse_rider_leg_present =
        $null -ne $omenDriveTwo -and [string]$omenDriveTwo.kind -eq 'saddle_drive' -and
        $null -ne $i5ObserveTwo -and [string]$i5ObserveTwo.kind -eq 'saddle_observe'
    reverse_leg_observer_covers_driver =
        $null -ne $omenDriveTwo -and $null -ne $i5ObserveTwo -and
        [double]$i5ObserveTwo.duration_seconds -ge [double]$omenDriveTwo.duration_seconds
    second_release_gate_present =
        $null -ne $i5ReleaseTwo -and
        [string]$i5ReleaseTwo.kind -eq 'saddle_wait_released' -and
        [double]$i5ReleaseTwo.deadline_seconds -ge 15
    proof_holds_cover_disconnect_skew =
        $null -ne $omenHold -and $null -ne $i5Hold -and
        [string]$omenHold.kind -eq 'wait' -and [string]$i5Hold.kind -eq 'wait' -and
        [double]$omenHold.duration_seconds -ge 10 -and
        [double]$i5Hold.duration_seconds -ge 10
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_mount_scenario_coverage'
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
