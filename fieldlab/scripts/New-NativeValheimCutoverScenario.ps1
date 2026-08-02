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

    [ValidateSet('baseline', 'c1', 'c2a', 'c2b', 'c3', 'c4a', 'c4', 'c5', 'c6', 'c7', 'c7-cold', 'c8', 'c10a-stamina', 'full')]
    [string] $Profile = 'baseline',

    [string] $OmenOwnershipTargetTag = '',

    [string] $I5OwnershipTargetTag = '',

    # C9 needs a long enough continuous motion window to judge rendered quality;
    # C8's accepted 6s/4m pair is too short to see anything in a clip. These
    # bounds intentionally match NativeCutoverScenarioController so this
    # generator cannot emit a manifest that the deployed mod must reject.
    [ValidateRange(4, 20)]
    [int] $MotionDurationSeconds = 6,

    [ValidateRange(2, 24)]
    [int] $MotionDistanceMeters = 4,

    [ValidateRange(2, 24)]
    [int] $MotionGapDistanceMeters = 6
)

$ErrorActionPreference = 'Stop'

# C8's accepted motion cells ran 6s against a 16s deadline; keep that 10s of
# headroom as the window grows so a longer capture cannot deadline on duration
# alone.
$motionDeadline = $MotionDurationSeconds + 10

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
    [string] $TargetTag = '',
    [double] $WorldX = 0,
    [double] $WorldY = 0,
    [double] $WorldZ = 0) {
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
        world_x = $WorldX
        world_y = $WorldY
        world_z = $WorldZ
    }
}

$actions = @(
    New-Action 'omen-settle' 'omen' 'wait' 10 2
    New-Action 'i5-settle' 'i5' 'wait' 10 2
)

if ($Profile -eq 'c8') {
    # Character profiles persist their last position. A prior portal proof can leave
    # either client inside a portal trigger, so establish a safe movement origin
    # before any motion choreography.
    $actions += @(
        New-Action 'omen-c8-safe-origin' 'omen' 'teleport_to' 15 0 0 0 '' '' 2211 80 -69
        New-Action 'i5-c8-safe-origin' 'i5' 'teleport_to' 15 0 0 0 '' '' 2211 80 -69
    )
}

if ($Profile -eq 'c10a-stamina') {
    $actions += @(
        New-Action 'omen-c10a-stamina-safe-origin' 'omen' 'teleport_to' 15 0 0 0 '' '' 2211 80 -69
        New-Action 'i5-c10a-stamina-safe-origin' 'i5' 'teleport_to' 15 0 0 0 '' '' 2211 80 -69
    )
}

$actions += @(
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
    # Character profiles persist their last position. The retained-state Lab bridge can
    # leave OMEN and i5 thousands of metres apart, outside the Gateway's interest edge;
    # without an edge the motion rendezvous has no remote descriptor to bootstrap from.
    # Use the same bounded, fail-closed safe origin as C8 so C6 tests the motion lane
    # rather than whichever world position a prior session happened to leave behind.
    $actions += @(
        # The retained world settles this location at roughly y=33.  Using a
        # high vertical target (y=80) leaves one client reporting a remote
        # descriptor nearly 50m above the other, which the authority guard
        # must reject as an implausible correction.  Keep both clients on the
        # known ground plane so C6 measures the motion lane itself.
        New-Action 'omen-c6-safe-origin' 'omen' 'teleport_to' 15 0 0 0 '' '' 2211 33 -69
        New-Action 'i5-c6-safe-origin' 'i5' 'teleport_to' 15 0 0 0 '' '' 2211 33 -69
        New-Action 'omen-c6-rendezvous-settle' 'omen' 'wait' 12 5
        New-Action 'i5-c6-rendezvous' 'i5' 'motion_rendezvous' 15
        New-Action 'i5-c6-rendezvous-settle' 'i5' 'wait' 10 3
        New-Action 'omen-c6-drive-one' 'omen' 'motion_drive' $motionDeadline $MotionDurationSeconds $MotionDistanceMeters 0 east 'c6-omen-to-i5'
        New-Action 'i5-c6-observe-one' 'i5' 'motion_observe' $motionDeadline $MotionDurationSeconds $MotionDistanceMeters 0 east 'c6-omen-to-i5'
        New-Action 'omen-c6-pair-one-settle' 'omen' 'wait' 10 4
        New-Action 'i5-c6-pair-one-settle' 'i5' 'wait' 10 4
        New-Action 'omen-c6-observe-two' 'omen' 'motion_observe' $motionDeadline $MotionDurationSeconds $MotionDistanceMeters 0 north 'c6-i5-to-omen'
        New-Action 'i5-c6-drive-two' 'i5' 'motion_drive' $motionDeadline $MotionDurationSeconds $MotionDistanceMeters 0 north 'c6-i5-to-omen'
        New-Action 'omen-c6-pair-two-settle' 'omen' 'wait' 10 4
        New-Action 'i5-c6-pair-two-settle' 'i5' 'wait' 10 4
        # Each client advances its own manifest independently. In the clean
        # watchdog run the OMEN sender reached its gap action 2.74s before the
        # i5 observer, so i5 applied the correlated resync before its probe
        # captured baseline counters. Reuse C8's proven observer-alignment
        # margin and guard the ordering with Test-C6ScenarioCoverage.ps1.
        New-Action 'omen-c6-gap-observer-align' 'omen' 'wait' 10 4
        New-Action 'omen-c6-drive-gap' 'omen' 'motion_drive_gap' $motionDeadline $MotionDurationSeconds $MotionGapDistanceMeters 0 west 'c6-gap-omen-to-i5'
        New-Action 'i5-c6-observe-gap' 'i5' 'motion_observe_gap' $motionDeadline $MotionDurationSeconds $MotionGapDistanceMeters 0 west 'c6-gap-omen-to-i5'
    )
}

if ($Profile -eq 'c10a-stamina') {
    # Both physical clients first converge on the same safe world slice. The
    # fixed remote-stamina action then invokes the exact vanilla instance RPC
    # against the other real player's non-owned ZNetView and waits for a
    # receiver-side before/requested/after semantic receipt.
    $actions += @(
        New-Action 'omen-c10a-stamina-rendezvous-settle' 'omen' 'wait' 12 5
        New-Action 'i5-c10a-stamina-rendezvous' 'i5' 'motion_rendezvous' 20
        New-Action 'omen-c10a-stamina-ready' 'omen' 'wait' 10 3
        New-Action 'i5-c10a-stamina-settle' 'i5' 'wait' 10 3
        New-Action 'omen-c10a-remote-stamina' 'omen' 'routed_remote_stamina' 20
        New-Action 'i5-c10a-remote-stamina' 'i5' 'routed_remote_stamina' 20
    )
}

if ($Profile -eq 'c7') {
    $actions += @(
        New-Action 'omen-c7-socket-quarantine' 'omen' 'socket_quarantine' 90 60
        New-Action 'i5-c7-socket-quarantine' 'i5' 'socket_quarantine' 90 60
    )
}

if ($Profile -eq 'c7-cold') {
    $actions += @(
        New-Action 'omen-c7-cold-join-settle' 'omen' 'wait' 20 10
        New-Action 'i5-c7-cold-join-settle' 'i5' 'wait' 20 10
    )
}

if ($Profile -eq 'c8') {
    $actions += @(
        New-Action 'omen-c8-rendezvous-settle' 'omen' 'wait' 12 5
        New-Action 'i5-c8-rendezvous' 'i5' 'motion_rendezvous' 20
        New-Action 'omen-c8-rendezvous-ready' 'omen' 'wait' 10 3
        New-Action 'i5-c8-rendezvous-settle' 'i5' 'wait' 10 3
        New-Action 'omen-c8-motion-drive-one' 'omen' 'motion_drive' $motionDeadline $MotionDurationSeconds $MotionDistanceMeters 0 east 'c8-omen-to-i5'
        New-Action 'i5-c8-motion-observe-one' 'i5' 'motion_observe' $motionDeadline $MotionDurationSeconds 0 0 east 'c8-omen-to-i5'
        New-Action 'omen-c8-motion-pair-one-settle' 'omen' 'wait' 10 4
        New-Action 'i5-c8-motion-pair-one-settle' 'i5' 'wait' 10 4
        New-Action 'omen-c8-motion-observe-two' 'omen' 'motion_observe' $motionDeadline $MotionDurationSeconds 0 0 north 'c8-i5-to-omen'
        New-Action 'i5-c8-motion-drive-two' 'i5' 'motion_drive' $motionDeadline $MotionDurationSeconds $MotionDistanceMeters 0 north 'c8-i5-to-omen'
        New-Action 'omen-c8-motion-pair-two-settle' 'omen' 'wait' 10 4
        New-Action 'i5-c8-motion-pair-two-settle' 'i5' 'wait' 10 4
        New-Action 'omen-c8-gap-observer-align' 'omen' 'wait' 10 4
        New-Action 'omen-c8-motion-drive-gap' 'omen' 'motion_drive_gap' $motionDeadline $MotionDurationSeconds $MotionGapDistanceMeters 0 west 'c8-gap-omen-to-i5'
        New-Action 'i5-c8-motion-observe-gap' 'i5' 'motion_observe_gap' $motionDeadline $MotionDurationSeconds 0 0 west 'c8-gap-omen-to-i5'
        New-Action 'omen-c8-direct-pulse' 'omen' 'direct_control_pulse' 20
        New-Action 'i5-c8-direct-pulse' 'i5' 'direct_control_pulse' 20
        New-Action 'omen-c8-routed-request' 'omen' 'routed_request' 20
        New-Action 'i5-c8-routed-request' 'i5' 'routed_request' 20
        New-Action 'omen-c8-routed-broadcast' 'omen' 'routed_broadcast' 20
        New-Action 'i5-c8-routed-broadcast' 'i5' 'routed_broadcast' 20
        New-Action 'omen-c8-routed-target' 'omen' 'routed_target_zdo' 20
        New-Action 'i5-c8-routed-target' 'i5' 'routed_target_zdo' 20
        New-Action 'omen-c8-zdo-journal-drive' 'omen' 'zdo_journal_drive' 90
        New-Action 'i5-c8-zdo-journal-observe' 'i5' 'zdo_journal_observe' 90
        New-Action 'omen-c8-gateway-restart-resume' 'omen' 'gateway_restart_resume' 20
        New-Action 'i5-c8-gateway-restart-resume' 'i5' 'gateway_restart_resume' 20
        New-Action 'c8-ownership-contended' 'omen' 'ownership_lease_pickup' 90
        New-Action 'c8-ownership-contended' 'i5' 'ownership_contention' 60
        New-Action 'omen-c8-post-contention-settle' 'omen' 'wait' 10 3
        New-Action 'i5-c8-ownership-pickup' 'i5' 'ownership_lease_pickup' 90
        New-Action 'omen-c8-zone-cross' 'omen' 'zone_cross' 25 0 72 0 east
        New-Action 'i5-c8-zone-cross' 'i5' 'zone_cross' 25 0 72 0 west
        New-Action 'omen-c8-zone-resume' 'omen' 'zone_membership_resume' 90
        New-Action 'i5-c8-zone-resume' 'i5' 'zone_membership_resume' 90
        New-Action 'omen-c8-portal-slice' 'omen' 'teleport_to' 15 0 0 0 '' '' 3250 80 2250
        New-Action 'i5-c8-portal-slice' 'i5' 'teleport_to' 15 0 0 0 '' '' 3250 80 2250
        # 40s covers BOTH legs: vanilla's distant-teleport completion can take up to
        # 2s minimum + 15s FindFloor fallback PER LEG once the area is ready, and the
        # journal path converges readiness in ~4-5s per side. full43's return leg
        # inherited only ~5s of the old 22s budget and deadlined 0.2s after the area
        # went ready - a calibration miss, not a boundary defect.
        New-Action 'omen-c8-portal-roundtrip' 'omen' 'portal_roundtrip' 40 0 0 256
        New-Action 'i5-c8-portal-roundtrip' 'i5' 'portal_roundtrip' 40 0 0 256
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
if ($Profile -eq 'c8') {
    $manifest['c8_contract'] = [ordered]@{
        semantics = @(
            'steam_free_cold_join',
            'co_presence',
            'motion_both_directions',
            'pickup_both_clients',
            'target_zdo_interaction',
            'two_logical_peer_ownership_contention',
            'zone_exit_enter',
            'clean_disconnect_rejoin')
        faults = @(
            'gateway_restart',
            'udp_drop_range',
            'websocket_resume')
        integrity = @(
            'client_and_server_native_poison',
            'save_fingerprint_before_after')
    }
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
