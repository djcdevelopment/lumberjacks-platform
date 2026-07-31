#Requires -Version 5.1
<#
.SYNOPSIS
Create one bounded manifest for the physical-client native cutover driver.

.DESCRIPTION
The manifest is data, not an input macro. ComfyNetworkSense accepts only the fixed action kinds
declared here, validates every bound again in-process, and correlates the file to an expiring
native-autotest request.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunId,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [ValidateSet('baseline', 'c1', 'c2a', 'c2b', 'c3', 'c4a', 'c4', 'c5', 'c6', 'c7', 'full')]
    [string] $Profile = 'baseline',

    [string] $OmenOwnershipTargetTag = '',

    [string] $I5OwnershipTargetTag = ''
)

$ErrorActionPreference = 'Stop'

if ($RunId.Length -gt 80 -or $RunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw "RunId must be an 80-character-or-shorter safe token: $RunId"
}
if ($Profile -eq 'full') {
    $expectedPrefix = "cutover-$RunId"
    foreach ($tag in @($OmenOwnershipTargetTag, $I5OwnershipTargetTag)) {
        if ([string]::IsNullOrWhiteSpace($tag) -or
            -not $tag.StartsWith($expectedPrefix, [StringComparison]::Ordinal) -or
            $tag.Length -gt 96 -or $tag -notmatch '^[A-Za-z0-9._-]+$') {
            throw "Full profile ownership tags must begin with '$expectedPrefix' and be safe tokens."
        }
    }
}

function New-Action(
    [string] $Id,
    [string] $Client,
    [string] $Kind,
    [double] $DeadlineSeconds,
    [double] $DurationSeconds = 0,
    [double] $DistanceMeters = 0,
    [double] $RadiusMeters = 0,
    [string] $Direction = '',
    [string] $TargetTag = '') {
    [ordered]@{
        id = $Id
        client = $Client
        kind = $Kind
        deadline_seconds = $DeadlineSeconds
        duration_seconds = $DurationSeconds
        distance_meters = $DistanceMeters
        radius_meters = $RadiusMeters
        direction = $Direction
        target_tag = $TargetTag
    }
}

$actions = @(
    New-Action 'omen-settle' 'omen' 'wait' 10 2
    New-Action 'i5-settle' 'i5' 'wait' 10 2
    New-Action 'omen-move' 'omen' 'move' 15 3 6 0 east
    New-Action 'i5-move' 'i5' 'move' 15 3 6 0 north
)

if ($Profile -eq 'full') {
    $actions += @(
        New-Action 'omen-pickup' 'omen' 'pickup_nearest' 25 0 0 8
        New-Action 'i5-pickup' 'i5' 'pickup_nearest' 25 0 0 8
        New-Action 'omen-ownership' 'omen' 'ownership_target' 25 0 0 16 '' $OmenOwnershipTargetTag
        New-Action 'i5-ownership' 'i5' 'ownership_target' 25 0 0 16 '' $I5OwnershipTargetTag
        New-Action 'omen-zone' 'omen' 'zone_cross' 20 0 72 0 east
        New-Action 'i5-zone' 'i5' 'zone_cross' 20 0 72 0 west
    )
}

if ($Profile -eq 'c1') {
    $actions += @(
        New-Action 'omen-session-resume' 'omen' 'session_resume_probe' 30
        New-Action 'i5-session-resume' 'i5' 'session_resume_probe' 30
        New-Action 'omen-session-timeout' 'omen' 'session_timeout_probe' 8
        New-Action 'i5-session-timeout' 'i5' 'session_timeout_probe' 8
    )
}

if ($Profile -eq 'c2a') {
    $actions += @(
        New-Action 'omen-direct-pulse' 'omen' 'direct_control_pulse' 15
        New-Action 'i5-direct-pulse' 'i5' 'direct_control_pulse' 15
        New-Action 'omen-direct-withhold' 'omen' 'direct_control_withhold' 8
        New-Action 'i5-direct-withhold' 'i5' 'direct_control_withhold' 8
    )
}

if ($Profile -eq 'c2b') {
    $actions += @(
        New-Action 'omen-routed-request' 'omen' 'routed_request' 20
        New-Action 'i5-routed-request' 'i5' 'routed_request' 20
        New-Action 'omen-routed-broadcast' 'omen' 'routed_broadcast' 20
        New-Action 'i5-routed-broadcast' 'i5' 'routed_broadcast' 20
        New-Action 'omen-routed-target' 'omen' 'routed_target_zdo' 20
        New-Action 'i5-routed-target' 'i5' 'routed_target_zdo' 20
        New-Action 'omen-routed-withhold' 'omen' 'routed_withhold' 8
        New-Action 'i5-routed-withhold' 'i5' 'routed_withhold' 8
    )
}

if ($Profile -in @('c3', 'c4a', 'c4')) {
    $actions += @(
        New-Action 'omen-zdo-journal-drive' 'omen' 'zdo_journal_drive' 300
        New-Action 'i5-zdo-journal-observe' 'i5' 'zdo_journal_observe' 300
        New-Action 'omen-zdo-journal-ack-settle' 'omen' 'wait' 10 2
        New-Action 'i5-zdo-journal-ack-settle' 'i5' 'wait' 10 2
    )
}

if ($Profile -eq 'c4') {
    $actions += @(
        New-Action 'omen-ownership-lease-pickup' 'omen' 'ownership_lease_pickup' 180
        New-Action 'i5-ownership-lease-pickup' 'i5' 'ownership_lease_pickup' 180
    )
}

if ($Profile -eq 'c5') {
    $actions += @(
        New-Action 'omen-c5-zone-cross' 'omen' 'zone_cross' 25 0 72 0 east
        New-Action 'i5-c5-zone-cross' 'i5' 'zone_cross' 25 0 72 0 west
        New-Action 'omen-c5-membership-resume' 'omen' 'zone_membership_resume' 90
        New-Action 'i5-c5-membership-resume' 'i5' 'zone_membership_resume' 90
        New-Action 'omen-c5-membership-withhold' 'omen' 'zone_membership_withhold' 30
        New-Action 'i5-c5-membership-withhold' 'i5' 'zone_membership_withhold' 30
    )
}

if ($Profile -eq 'c6') {
    $actions += @(
        New-Action 'omen-c6-rendezvous-settle' 'omen' 'wait' 12 5
        New-Action 'i5-c6-rendezvous' 'i5' 'motion_rendezvous' 15
        New-Action 'i5-c6-rendezvous-settle' 'i5' 'wait' 10 3
        New-Action 'omen-c6-drive-one' 'omen' 'motion_drive' 16 6 4 0 east 'c6-omen-to-i5'
        New-Action 'i5-c6-observe-one' 'i5' 'motion_observe' 16 6 0 0 east 'c6-omen-to-i5'
        New-Action 'omen-c6-pair-one-settle' 'omen' 'wait' 10 4
        New-Action 'i5-c6-pair-one-settle' 'i5' 'wait' 10 4
        New-Action 'omen-c6-observe-two' 'omen' 'motion_observe' 16 6 0 0 north 'c6-i5-to-omen'
        New-Action 'i5-c6-drive-two' 'i5' 'motion_drive' 16 6 4 0 north 'c6-i5-to-omen'
        New-Action 'omen-c6-pair-two-settle' 'omen' 'wait' 10 4
        New-Action 'i5-c6-pair-two-settle' 'i5' 'wait' 10 4
        New-Action 'omen-c6-drive-gap' 'omen' 'motion_drive_gap' 16 6 6 0 west 'c6-gap-omen-to-i5'
        New-Action 'i5-c6-observe-gap' 'i5' 'motion_observe_gap' 16 6 0 0 west 'c6-gap-omen-to-i5'
    )
}

if ($Profile -eq 'c7') {
    $actions += @(
        New-Action 'omen-c7-socket-quarantine' 'omen' 'socket_quarantine' 90 60
        New-Action 'i5-c7-socket-quarantine' 'i5' 'socket_quarantine' 90 60
    )
}

$actions += @(
    New-Action 'omen-disconnect-resume' 'omen' 'disconnect_resume' 15
    New-Action 'i5-disconnect-resume' 'i5' 'disconnect_resume' 15
    New-Action 'omen-resumed' 'omen' 'wait' 10 2
    New-Action 'i5-resumed' 'i5' 'wait' 10 2
)

$manifest = [ordered]@{
    schema_version = 1
    run_id = $RunId
    created_utc = [DateTimeOffset]::UtcNow.ToString('o')
    expires_utc = [DateTimeOffset]::UtcNow.AddHours(1).ToString('o')
    profile = $Profile
    actions = $actions
}

$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $absoluteOutput
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText(
    $absoluteOutput,
    $json + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))

[ordered]@{
    schema_version = 1
    receipt_type = 'native_cutover_scenario_manifest'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    profile = $Profile
    action_count = $actions.Count
    path = $absoluteOutput
    sha256 = (Get-FileHash -LiteralPath $absoluteOutput -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 6
