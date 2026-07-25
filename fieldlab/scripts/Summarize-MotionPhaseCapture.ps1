<#
.SYNOPSIS
Summarize cumulative Lumberjacks motion phase counters from a Companion capture.

.DESCRIPTION
Reads Companion transport-truth samples.jsonl, calculates restart-aware deltas for
the cumulative motion counters, and derives bounded phase ratios and means. Maximum
fields remain process-lifetime maxima; the report says whether each maximum changed
during the capture instead of presenting it as capture-local.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SamplesPath,

    [string] $OutputPath
)

$SamplesPath = (Resolve-Path -LiteralPath $SamplesPath -ErrorAction Stop).Path
$counterNames = @(
    'motion_received_udp',
    'motion_received_websocket',
    'motion_applied',
    'motion_stale_fallbacks',
    'motion_unknown_zdos',
    'motion_zdo_lookup_attempts',
    'motion_receive_interval_count',
    'motion_receive_interval_total_us',
    'motion_drain_batches',
    'motion_drained_samples',
    'motion_coalesced_in_drain',
    'motion_stale_sequence_drops',
    'motion_late_update_calls',
    'motion_fresh_remote_visits',
    'motion_stale_remote_visits',
    'motion_fresh_visit_age_samples',
    'motion_fresh_visit_age_total_ms',
    'motion_bind_measurement_count',
    'motion_bind_total_us',
    'motion_render_measurement_count',
    'motion_render_total_us',
    'motion_target_error_samples',
    'motion_target_error_total_mm',
    'motion_correction_guard_rejections',
    'motion_interframe_displacement_checks',
    'motion_interframe_displacement_over_50mm',
    'motion_interframe_displacement_total_mm'
)
$maximumNames = @(
    'motion_receive_interval_max_us',
    'motion_drain_batch_max',
    'motion_bind_max_us',
    'motion_render_max_us',
    'motion_target_error_max_mm',
    'motion_fresh_visit_age_max_ms',
    'motion_interframe_displacement_max_mm'
)

function Read-Int64Property([object] $Value, [string] $Name) {
    if ($null -eq $Value) { return $null }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $null }
    try { return [long] $property.Value } catch { return $null }
}

function Read-Property([object] $Value, [string] $Name) {
    if ($null -eq $Value) { return $null }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Read-BoolProperty([object] $Value, [string] $Name) {
    $raw = Read-Property $Value $Name
    if ($null -eq $raw) { return $null }
    try { return [Convert]::ToBoolean($raw) } catch { return $null }
}

function Divide-OrNull([long] $Numerator, [long] $Denominator, [int] $Digits = 3) {
    if ($Denominator -le 0) { return $null }
    return [Math]::Round($Numerator / [double] $Denominator, $Digits)
}

$first = @{}
$last = @{}
$previous = @{}
$deltas = @{}
$resets = @{}
$maxFirst = @{}
$maxLast = @{}
$validSamples = 0
$malformedRows = 0
$firstTimestamp = $null
$lastTimestamp = $null
$lastPhaseEnabled = $null
$phaseContractSamples = 0
$firstRemoteEntities = $null
$lastRemoteEntities = $null
$roleSamples = 0
$roleObserved = [ordered]@{}
$hasFirstRole = $false
$firstApplyEnabled = $null
$lastApplyEnabled = $null
$currentRoleSet = $false
$currentRole = $null
$roleTransitions = 0
$lastRoleSegmentSamples = 0
$lastRoleSegmentFirstTimestamp = $null
$lastRoleSegmentLastTimestamp = $null
$roleTailPrevious = @{}
$roleTailDeltas = @{}
$roleTailResets = @{}

foreach ($line in Get-Content -LiteralPath $SamplesPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try {
        $row = $line | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $malformedRows++
        continue
    }
    $motion = Read-Property $row 'local_motion'
    if ($null -eq $motion) { continue }

    $validSamples++
    $timestamp = Read-Property $row 'timestamp_utc'
    if ($null -eq $firstTimestamp) { $firstTimestamp = $timestamp }
    $lastTimestamp = $timestamp
    $lastPhaseEnabled = Read-Property $motion 'motion_phase_measurements_enabled'
    if ($null -ne $lastPhaseEnabled) { $phaseContractSamples++ }
    $remoteEntities = Read-Int64Property $motion 'motion_remote_entities'
    if ($null -eq $firstRemoteEntities) { $firstRemoteEntities = $remoteEntities }
    $lastRemoteEntities = $remoteEntities

    $applyEnabled = Read-BoolProperty $motion 'motion_apply_enabled'
    if ($null -eq $applyEnabled) {
        $applyEnabled = Read-BoolProperty $motion 'apply_enabled'
    }
    if ($null -ne $applyEnabled) {
        $roleSamples++
        $roleKey = $applyEnabled.ToString().ToLowerInvariant()
        $roleObserved[$roleKey] = $applyEnabled
        if (-not $hasFirstRole) {
            $hasFirstRole = $true
            $firstApplyEnabled = $applyEnabled
        }
        $lastApplyEnabled = $applyEnabled

        if (-not $currentRoleSet -or $currentRole -ne $applyEnabled) {
            if ($currentRoleSet) { $roleTransitions++ }
            $currentRoleSet = $true
            $currentRole = $applyEnabled
            $lastRoleSegmentSamples = 0
            $lastRoleSegmentFirstTimestamp = $timestamp
            $roleTailPrevious = @{}
            $roleTailDeltas = @{}
            $roleTailResets = @{}
        }
        $lastRoleSegmentSamples++
        $lastRoleSegmentLastTimestamp = $timestamp

        foreach ($name in $counterNames) {
            $current = Read-Int64Property $motion $name
            if ($null -eq $current) { continue }
            if (-not $roleTailPrevious.ContainsKey($name)) {
                $roleTailPrevious[$name] = $current
                $roleTailDeltas[$name] = [long] 0
                $roleTailResets[$name] = 0
                continue
            }

            $increment = $current - [long] $roleTailPrevious[$name]
            if ($increment -lt 0) {
                $roleTailResets[$name] = [int] $roleTailResets[$name] + 1
                $increment = $current
            }
            $roleTailDeltas[$name] = [long] $roleTailDeltas[$name] + $increment
            $roleTailPrevious[$name] = $current
        }
    }

    foreach ($name in $counterNames) {
        $current = Read-Int64Property $motion $name
        if ($null -eq $current) { continue }
        if (-not $first.ContainsKey($name)) {
            $first[$name] = $current
            $previous[$name] = $current
            $deltas[$name] = [long] 0
            $resets[$name] = 0
            continue
        }

        $increment = $current - [long] $previous[$name]
        if ($increment -lt 0) {
            $resets[$name] = [int] $resets[$name] + 1
            $increment = $current
        }
        $deltas[$name] = [long] $deltas[$name] + $increment
        $previous[$name] = $current
        $last[$name] = $current
    }

    foreach ($name in $maximumNames) {
        $current = Read-Int64Property $motion $name
        if ($null -eq $current) { continue }
        if (-not $maxFirst.ContainsKey($name)) { $maxFirst[$name] = $current }
        $maxLast[$name] = $current
    }
}

if ($validSamples -lt 2) {
    throw "At least two samples with local_motion are required; found $validSamples in $SamplesPath"
}
if ($phaseContractSamples -lt 2) {
    throw "Motion phase fields are missing; install the CRE-E06 mod build and capture again"
}

$deltaOutput = [ordered] @{}
$resetOutput = [ordered] @{}
foreach ($name in $counterNames) {
    if ($deltas.ContainsKey($name)) { $deltaOutput[$name] = [long] $deltas[$name] }
    if ($resets.ContainsKey($name) -and [int] $resets[$name] -gt 0) {
        $resetOutput[$name] = [int] $resets[$name]
    }
}

$maximumOutput = [ordered] @{}
foreach ($name in $maximumNames) {
    if (-not $maxLast.ContainsKey($name)) { continue }
    $beginning = if ($maxFirst.ContainsKey($name)) { [long] $maxFirst[$name] } else { $null }
    $ending = [long] $maxLast[$name]
    $maximumOutput[$name] = [ordered] @{
        beginning_lifetime_max = $beginning
        ending_lifetime_max = $ending
        changed_during_capture = $null -ne $beginning -and $ending -gt $beginning
    }
}

$roleTailDeltaOutput = [ordered] @{}
$roleTailResetOutput = [ordered] @{}
foreach ($name in $counterNames) {
    if ($roleTailDeltas.ContainsKey($name)) {
        $roleTailDeltaOutput[$name] = [long] $roleTailDeltas[$name]
    }
    if ($roleTailResets.ContainsKey($name) -and [int] $roleTailResets[$name] -gt 0) {
        $roleTailResetOutput[$name] = [int] $roleTailResets[$name]
    }
}

function Delta([string] $Name) {
    if ($deltas.ContainsKey($Name)) { return [long] $deltas[$Name] }
    return [long] 0
}

$received = (Delta 'motion_received_udp') + (Delta 'motion_received_websocket')
$derived = [ordered] @{
    received_samples = $received
    mean_receive_interval_us = Divide-OrNull (Delta 'motion_receive_interval_total_us') (Delta 'motion_receive_interval_count')
    mean_drained_per_batch = Divide-OrNull (Delta 'motion_drained_samples') (Delta 'motion_drain_batches')
    applies_per_received_sample = Divide-OrNull (Delta 'motion_applied') $received
    applies_per_fresh_remote_visit = Divide-OrNull (Delta 'motion_applied') (Delta 'motion_fresh_remote_visits')
    mean_bind_us = Divide-OrNull (Delta 'motion_bind_total_us') (Delta 'motion_bind_measurement_count')
    mean_late_update_us = Divide-OrNull (Delta 'motion_render_total_us') (Delta 'motion_render_measurement_count')
    mean_fresh_visit_age_ms = Divide-OrNull (Delta 'motion_fresh_visit_age_total_ms') (Delta 'motion_fresh_visit_age_samples')
    mean_target_error_mm = Divide-OrNull (Delta 'motion_target_error_total_mm') (Delta 'motion_target_error_samples')
    mean_interframe_displacement_mm = Divide-OrNull (Delta 'motion_interframe_displacement_total_mm') (Delta 'motion_interframe_displacement_checks')
    interframe_over_50mm_ratio = Divide-OrNull (Delta 'motion_interframe_displacement_over_50mm') (Delta 'motion_interframe_displacement_checks') 6
}

$result = [ordered] @{
    schema_version = 2
    event_type = 'motion_phase.capture_summary'
    samples_path = $SamplesPath
    first_timestamp_utc = $firstTimestamp
    last_timestamp_utc = $lastTimestamp
    valid_local_motion_samples = $validSamples
    phase_contract_samples = $phaseContractSamples
    malformed_rows = $malformedRows
    phase_measurements_enabled_at_end = $lastPhaseEnabled
    remote_entities = [ordered] @{
        beginning = $firstRemoteEntities
        ending = $lastRemoteEntities
    }
    apply_role = [ordered] @{
        samples = $roleSamples
        observed_values = @($roleObserved.Values)
        first = $firstApplyEnabled
        last = $lastApplyEnabled
        transitions = $roleTransitions
        last_segment_samples = $lastRoleSegmentSamples
        last_segment_first_timestamp_utc = $lastRoleSegmentFirstTimestamp
        last_segment_last_timestamp_utc = $lastRoleSegmentLastTimestamp
        last_segment_ready = ($currentRoleSet -and $lastRoleSegmentSamples -ge 2)
        last_segment_counter_resets = $roleTailResetOutput
        last_segment_deltas = $roleTailDeltaOutput
    }
    counter_resets = $resetOutput
    deltas = $deltaOutput
    derived = $derived
    lifetime_maxima = $maximumOutput
    interpretation_limits = @(
        'Lifetime maxima are not capture-local unless changed_during_capture is true.',
        'APPLY/OBSERVE attribution uses only the final contiguous role segment so setup-time role changes do not contaminate the control.',
        'Interframe displacement can reveal another transform writer or physics step, but cannot identify native Valheim as the cause.',
        'Receive intervals are monotonic arrival spacing, not one-way network latency.'
    )
}

$json = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $json -Encoding utf8
}
$json
