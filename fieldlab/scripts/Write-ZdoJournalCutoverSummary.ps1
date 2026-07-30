#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one physical-client C3 run into a durable ZDO-journal cutover gate receipt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunDirectory,

    [Parameter(Mandatory)]
    [string] $RunId,

    [string] $GatewayHealthUrl = 'http://127.0.0.1:4000/health'
)

$ErrorActionPreference = 'Stop'
$absoluteRun = (Resolve-Path -LiteralPath $RunDirectory -ErrorAction Stop).Path

function Read-JsonLines([string] $Path) {
    $rows = @()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    return @($rows)
}

function Read-JsonFile([string] $Name) {
    Get-Content -LiteralPath (Join-Path $absoluteRun $Name) -Raw |
        ConvertFrom-Json
}

function Count-State([object[]] $Rows, [string] $State) {
    @($Rows | Where-Object { $_.state -eq $State }).Count
}

function Client-Summary([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $rows = @(
        Read-JsonLines (Join-Path $directory 'zdo-journal-cutover.jsonl')
    )
    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json
    $action = if ($Client -eq 'omen') {
        'omen-zdo-journal-drive'
    } else {
        'i5-zdo-journal-observe'
    }
    $expectedRecipient = "$RunId.$Client"
    $probePass = @($rows | Where-Object {
        $_.state -eq 'probe_passed' -and $_.action_id -eq $action
    })
    $interest = @($rows | Where-Object {
        $_.state -eq 'interest_registered' -and
        $_.detail -match [regex]::Escape(
            '"recipient_id":"' + $expectedRecipient + '"')
    })
    $snapshot = Count-State $rows 'snapshot_applied_typed'
    $delta = Count-State $rows 'delta_applied_typed'
    $stale = Count-State $rows 'stale_rejected_before_mutation'
    $malformed = Count-State $rows 'malformed_rejected_before_mutation'
    $tombstone = Count-State $rows 'tombstone_applied_typed'
    $nativeTripwire = Count-State $rows 'native_rpc_zdo_data_tripwire'
    $applyFailures = Count-State $rows 'typed_apply_failed'
    $legacyScope = @($rows | Where-Object {
        $_.state -eq 'interest_registered' -and
        $_.detail -match '"recipient_id":"legacy"'
    }).Count

    [ordered]@{
        client = $Client
        action_id = $action
        expected_recipient_id = $expectedRecipient
        probe_pass_count = $probePass.Count
        interest_receipt_count = $interest.Count
        snapshot_applied_count = $snapshot
        delta_applied_count = $delta
        stale_rejected_count = $stale
        malformed_rejected_count = $malformed
        tombstone_applied_count = $tombstone
        native_rpc_zdo_data_count = $nativeTripwire
        typed_apply_failure_count = $applyFailures
        legacy_recipient_count = $legacyScope
        plugin_sha256 = $lifecycle.plugin_sha256
        plugin_version =
            if ($lifecycle.deployment.mod.version) {
                $lifecycle.deployment.mod.version
            } else {
                $lifecycle.preflight.plugin_version
            }
        lifecycle_result = $lifecycle.result
        resume_count = $lifecycle.resume_count
        scenario_terminal = $lifecycle.scenario_terminal.state
        joined_detail = $lifecycle.joined.detail
        checks = [ordered]@{
            probe_passed_once = $probePass.Count -eq 1
            recipient_isolated = $interest.Count -gt 0 -and $legacyScope -eq 0
            late_observer_snapshot_applied =
                $Client -ne 'i5' -or $snapshot -ge 1
            valid_delta_applied = $delta -ge 1
            stale_rejected_before_mutation = $stale -ge 1
            malformed_rejected_before_mutation = $malformed -ge 1
            tombstone_applied_typed = $tombstone -ge 1
            selected_native_rpc_zdo_data_zero = $nativeTripwire -eq 0
            typed_apply_failures_zero = $applyFailures -eq 0
            lifecycle_completed = $lifecycle.result -eq 'joined_held_and_stopped'
            fresh_process_resume_completed = [int]$lifecycle.resume_count -eq 1
            scenario_completed =
                $lifecycle.scenario_terminal.state -eq 'scenario_complete'
        }
    }
}

$clients = @(
    Client-Summary 'omen'
    Client-Summary 'i5'
)
$serverRows = @(
    Read-JsonLines (Join-Path $absoluteRun 'server\zdo-journal-cutover.jsonl')
)
$routedRows = @(
    Read-JsonLines (Join-Path $absoluteRun 'server\routed-rpc-cutover.jsonl')
)
$driveCreated = @($serverRows | Where-Object { $_.state -eq 'drive_created' })
$driveReleased = @($serverRows | Where-Object {
    $_.state -eq 'drive_faults_and_valid_queued'
})
$driveComplete = @($serverRows | Where-Object { $_.state -eq 'drive_complete' })
$mutations = @($serverRows | Where-Object { $_.state -eq 'mutation_posted' })
$deliveryOnly = @($mutations | Where-Object {
    $_.detail -match 'delivery_only=True'
})
$tombstones = @($mutations | Where-Object {
    $_.detail -match 'tombstone=True'
})
$serverFailures = @($serverRows | Where-Object {
    $_.state -in @('capture_failed', 'drive_request_rejected')
})
$requestDispatch = @($routedRows | Where-Object {
    $_.state -eq 'lumberjacks_handler_dispatched' -and
    $_.method -eq 'ComfyNetworkSense_CutoverZdoJournalRequest'
})
$routedFailures = @($routedRows | Where-Object {
    $_.state -eq 'lumberjacks_dispatch_failed' -and
    $_.method -eq 'ComfyNetworkSense_CutoverZdoJournalRequest'
})

$restart = Read-JsonFile 'gateway-journal-restart.json'
$final = Read-JsonFile 'gateway-journal-final.json'
$journalRuntime = Read-JsonFile 'server-runtime-zdo-journal.json'
$routedRuntime = Read-JsonFile 'server-runtime-routed-rpc.json'
$composition = Read-JsonFile 'composition.json'

$journalArm = @($journalRuntime.receipts | Where-Object {
    $_.setting -eq 'zdoJournalCutoverEnabled' -and
    $_.effective_value -eq 'true'
})
$journalDisarm = @($journalRuntime.receipts | Where-Object {
    $_.setting -eq 'zdoJournalCutoverEnabled' -and
    $_.effective_value -eq 'false'
})
$routedArm = @($routedRuntime.receipts | Where-Object {
    $_.setting -eq 'routedRpcCutoverEnabled' -and
    $_.effective_value -eq 'true'
})
$routedDisarm = @($routedRuntime.receipts | Where-Object {
    $_.setting -eq 'routedRpcCutoverEnabled' -and
    $_.effective_value -eq 'false'
})
$gatewaySet = @($routedRuntime.receipts | Where-Object {
    $_.setting -eq 'lumberjacksGatewayUrl' -and
    $_.request_id -like '*-routed-gateway'
})
$gatewayRestore = @($routedRuntime.receipts | Where-Object {
    $_.setting -eq 'lumberjacksGatewayUrl' -and
    $_.request_id -like '*-routed-restore'
})

$gatewayHealth = [ordered]@{ reachable = $false; status_code = $null }
try {
    $health = Invoke-WebRequest -UseBasicParsing -Uri $GatewayHealthUrl -TimeoutSec 5
    $gatewayHealth.reachable = $true
    $gatewayHealth.status_code = [int]$health.StatusCode
} catch {
    $gatewayHealth.error = $_.Exception.GetType().Name
}

$clientChecks = @($clients | ForEach-Object {
    $values = $_['checks']
    $values.Keys | ForEach-Object { [bool]$values[$_] }
})
$hashes = @(
    $clients |
        ForEach-Object { [string]$_['plugin_sha256'] } |
        Sort-Object -Unique
)
$versions = @(
    $clients |
        ForEach-Object { [string]$_['plugin_version'] } |
        Sort-Object -Unique
)
$checks = [ordered]@{
    both_clients_passed =
        $clientChecks.Count -gt 0 -and
        @($clientChecks | Where-Object { -not $_ }).Count -eq 0
    server_drive_created_once = $driveCreated.Count -eq 1
    server_released_after_two_interests =
        $driveReleased.Count -eq 1 -and
        $driveReleased[0].detail -match 'interested_recipients=2'
    server_drive_completed_once = $driveComplete.Count -eq 1
    selected_create_sync_list_candidates_zero =
        $driveComplete.Count -eq 1 -and
        [long]$driveComplete[0].native_create_sync_candidates -eq 0 -and
        $driveComplete[0].detail -match 'native_candidates=0'
    stale_and_malformed_crossed_delivery_boundary =
        $deliveryOnly.Count -eq 2
    durable_tombstone_posted = $tombstones.Count -eq 1
    server_capture_and_request_failures_zero = $serverFailures.Count -eq 0
    routed_request_dispatched_once = $requestDispatch.Count -eq 1
    routed_request_dispatch_failures_zero = $routedFailures.Count -eq 0
    gateway_had_durable_object_before_restart =
        [long]$restart.before.durable_objects -ge 1
    gateway_replayed_durable_object_after_restart =
        [bool]$restart.durable_replay_verified -and
        [long]$restart.after.durable_objects -ge 1
    two_distinct_final_interests =
        [int]$final.run_status.interested_recipients -eq 2
    final_delivery_queue_acknowledged =
        [long]$final.run_status.pending -eq 0
    final_tombstone_remained_durable =
        [long]$final.run_status.durable_objects -eq 1
    gateway_persistence_healthy =
        [bool]$final.global_status.persistence_enabled -and
        [bool]$final.global_status.persistence_healthy
    dev_journal_state_deleted =
        [bool]$final.reset.ok -and [int]$final.reset.objects_removed -eq 1
    journal_gate_armed_and_disarmed =
        $journalArm.Count -eq 1 -and $journalDisarm.Count -eq 1 -and
        [string]::IsNullOrWhiteSpace($journalRuntime.disarm_error)
    routed_gate_armed_and_disarmed =
        $routedArm.Count -eq 1 -and $routedDisarm.Count -eq 1 -and
        [string]::IsNullOrWhiteSpace($routedRuntime.disarm_error)
    server_gateway_restored =
        $gatewaySet.Count -eq 1 -and $gatewayRestore.Count -eq 1 -and
        $gatewayRestore[0].effective_value -eq $gatewaySet[0].old_value
    composition_completed = $composition.result -eq 'completed'
    client_artifact_hashes_match = $hashes.Count -eq 1
    client_plugin_versions_match = $versions.Count -eq 1
    gateway_healthy_after_run =
        $gatewayHealth.reachable -and $gatewayHealth.status_code -eq 200
}
$passed =
    @($checks.Keys | Where-Object { -not [bool]$checks[$_] }).Count -eq 0
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'lumberjacks_zdo_journal_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($passed) { 'passed' } else { 'failed' }
    clients = $clients
    server = [ordered]@{
        drive_created_count = $driveCreated.Count
        drive_release_count = $driveReleased.Count
        drive_complete_count = $driveComplete.Count
        mutation_posted_count = $mutations.Count
        delivery_only_count = $deliveryOnly.Count
        tombstone_count = $tombstones.Count
        capture_or_request_failure_count = $serverFailures.Count
        selected_create_sync_list_candidates =
            if ($driveComplete.Count -eq 1) {
                [long]$driveComplete[0].native_create_sync_candidates
            } else {
                $null
            }
        routed_request_dispatch_count = $requestDispatch.Count
        routed_request_failure_count = $routedFailures.Count
    }
    gateway = [ordered]@{
        restart = $restart
        final = $final
        health = $gatewayHealth
    }
    artifact = [ordered]@{
        plugin_sha256 = if ($hashes.Count -eq 1) { $hashes[0] } else { $hashes }
        plugin_version = if ($versions.Count -eq 1) { $versions[0] } else { $versions }
    }
    checks = $checks
}
$output = Join-Path $absoluteRun 'c3-machine-summary.json'
[IO.File]::WriteAllText(
    $output,
    ($summary | ConvertTo-Json -Depth 16) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 16
if (-not $passed) { exit 1 }
