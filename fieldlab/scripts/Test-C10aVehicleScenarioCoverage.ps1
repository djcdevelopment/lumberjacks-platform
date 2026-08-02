#Requires -Version 5.1
<#
.SYNOPSIS
Fail closed unless the C10a ship manifest gates both ownership transitions on a proven helm release.

.DESCRIPTION
The two clients advance independently. This verifier prevents the owner from transferring
the ship while the remote driver's release is still in flight, and requires the same release
proof after the reverse-direction leg. It runs before any remote state is changed.
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

$omenObserveOne = Get-Action $omen 'omen-c10a-ship-observe-i5'
$i5DriveOne = Get-Action $i5 'i5-c10a-ship-drive-omen'
$omenReleaseOne = Get-Action $omen 'omen-c10a-ship-release-i5'
$omenSettleOne = Get-Action $omen 'omen-c10a-ship-first-leg-settle'
$omenTransfer = Get-Action $omen 'omen-c10a-ship-transfer-i5'
$i5WaitOwner = Get-Action $i5 'i5-c10a-ship-owner'
$omenDriveTwo = Get-Action $omen 'omen-c10a-ship-drive-i5'
$i5ObserveTwo = Get-Action $i5 'i5-c10a-ship-observe-omen'
$i5ReleaseTwo = Get-Action $i5 'i5-c10a-ship-release-omen'
$omenHold = Get-Action $omen 'omen-c10a-ship-proof-hold'
$i5Hold = Get-Action $i5 'i5-c10a-ship-proof-hold'

$releaseOneIndex = Get-Index $omen 'omen-c10a-ship-release-i5'
$settleOneIndex = Get-Index $omen 'omen-c10a-ship-first-leg-settle'
$transferIndex = Get-Index $omen 'omen-c10a-ship-transfer-i5'
$observeOneIndex = Get-Index $omen 'omen-c10a-ship-observe-i5'
$observeTwoIndex = Get-Index $i5 'i5-c10a-ship-observe-omen'
$releaseTwoIndex = Get-Index $i5 'i5-c10a-ship-release-omen'
$holdTwoIndex = Get-Index $i5 'i5-c10a-ship-proof-hold'

$checks = [ordered]@{
    profile_is_c10a_vehicle = [string]$scenario.profile -eq 'c10a-vehicle'
    run_id_matches = [string]$scenario.run_id -eq $RunId
    action_ids_unique_per_client = @(
        $scenario.actions |
            Group-Object { "$($_.client):$($_.id)" } |
            Where-Object Count -gt 1).Count -eq 0
    first_leg_pair_present =
        $null -ne $omenObserveOne -and [string]$omenObserveOne.kind -eq 'ship_observe' -and
        $null -ne $i5DriveOne -and [string]$i5DriveOne.kind -eq 'ship_drive'
    first_leg_observer_covers_driver =
        $null -ne $omenObserveOne -and $null -ne $i5DriveOne -and
        [double]$omenObserveOne.duration_seconds -ge [double]$i5DriveOne.duration_seconds
    first_release_gate_present =
        $null -ne $omenReleaseOne -and
        [string]$omenReleaseOne.kind -eq 'ship_wait_released' -and
        [double]$omenReleaseOne.deadline_seconds -ge 15
    transfer_follows_release_and_settle =
        $observeOneIndex -ge 0 -and
        $releaseOneIndex -eq ($observeOneIndex + 1) -and
        $settleOneIndex -eq ($releaseOneIndex + 1) -and
        $transferIndex -eq ($settleOneIndex + 1) -and
        $null -ne $omenSettleOne -and
        [double]$omenSettleOne.duration_seconds -ge 3
    explicit_owner_handoff_pair_present =
        $null -ne $omenTransfer -and [string]$omenTransfer.kind -eq 'ship_transfer' -and
        $null -ne $i5WaitOwner -and [string]$i5WaitOwner.kind -eq 'ship_wait_owner'
    second_leg_pair_present =
        $null -ne $omenDriveTwo -and [string]$omenDriveTwo.kind -eq 'ship_drive' -and
        $null -ne $i5ObserveTwo -and [string]$i5ObserveTwo.kind -eq 'ship_observe'
    second_leg_observer_covers_driver =
        $null -ne $omenDriveTwo -and $null -ne $i5ObserveTwo -and
        [double]$i5ObserveTwo.duration_seconds -ge [double]$omenDriveTwo.duration_seconds
    second_release_gate_precedes_hold =
        $observeTwoIndex -ge 0 -and
        $releaseTwoIndex -eq ($observeTwoIndex + 1) -and
        $holdTwoIndex -eq ($releaseTwoIndex + 1) -and
        $null -ne $i5ReleaseTwo -and
        [string]$i5ReleaseTwo.kind -eq 'ship_wait_released' -and
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
    receipt_type = 'c10a_vehicle_scenario_coverage'
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
