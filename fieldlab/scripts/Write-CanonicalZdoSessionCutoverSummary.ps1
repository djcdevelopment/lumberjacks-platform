#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one physical-client C4a run into a canonical-session ZDO cutover receipt.

.DESCRIPTION
This reducer proves that C3 ZDO semantics crossed the durable C1 game-session
boundary with logical peer identity surviving Gateway and Valheim process
turnover. It deliberately does not claim ownership-authority cutover; that is
the next C4 slice.
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

function Unique-NonEmpty([object[]] $Values) {
    @(
        $Values |
            ForEach-Object { [string]$_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
}

function Client-Summary([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $zdoRows = @(
        Read-JsonLines (Join-Path $directory 'zdo-journal-cutover.jsonl')
    )
    $sessionRows = @(
        Read-JsonLines (Join-Path $directory 'lumberjacks-game-session.jsonl')
    )
    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json
    $action = if ($Client -eq 'omen') {
        'omen-zdo-journal-drive'
    } else {
        'i5-zdo-journal-observe'
    }
    $started = @($sessionRows | Where-Object { $_.state -eq 'session_started' })
    $logicalIds = @(Unique-NonEmpty @($started | ForEach-Object {
        $_.logical_peer_id
    }))
    $connectionIds = @(Unique-NonEmpty @($started | ForEach-Object {
        $_.connection_id
    }))
    $zdoLogicalIds = @(Unique-NonEmpty @($zdoRows | ForEach-Object {
        $_.logical_peer_id
    }))
    $transports = @(
        Unique-NonEmpty @($zdoRows | ForEach-Object { $_.transport })
    )
    $probePass = @($zdoRows | Where-Object {
        $_.state -eq 'probe_passed' -and $_.action_id -eq $action
    })
    $interest = @($zdoRows | Where-Object {
        $_.state -eq 'interest_registered'
    })
    $reincarnated = @($started | Where-Object {
        $_.detail -match 'reincarnated=true'
    })
    $httpFailures = @($zdoRows | Where-Object {
        $_.state -match 'http' -or
        $_.state -in @('cycle_failed', 'post_failed', 'poll_failed')
    })
    $nativeTripwire = Count-State $zdoRows 'native_rpc_zdo_data_tripwire'
    $nativeFieldUse = @($zdoRows | Where-Object {
        [long]$_.native_rpc_zdo_data -ne 0
    })
    $applyFailures = @($zdoRows | Where-Object {
        $_.state -in @('typed_apply_failed', 'canonical_delivery_rejected')
    })
    $expectedRenderer = if ($Client -eq 'omen') {
        'renderer=NVIDIA_GeForce_RTX_5070'
    } else {
        'renderer=Intel\(R\)_Iris\(R\)_Xe_Graphics'
    }

    [ordered]@{
        client = $Client
        action_id = $action
        logical_peer_ids = $logicalIds
        connection_ids = $connectionIds
        zdo_logical_peer_ids = $zdoLogicalIds
        session_started_count = $started.Count
        gateway_reincarnation_count = $reincarnated.Count
        canonical_transport_row_count = @($zdoRows).Count
        transports = $transports
        probe_pass_count = $probePass.Count
        interest_receipt_count = $interest.Count
        snapshot_applied_count = Count-State $zdoRows 'snapshot_applied_typed'
        snapshot_superseded_count = Count-State $zdoRows 'snapshot_superseded'
        delta_applied_count = Count-State $zdoRows 'delta_applied_typed'
        stale_rejected_count =
            Count-State $zdoRows 'stale_rejected_before_mutation'
        malformed_rejected_count =
            Count-State $zdoRows 'malformed_rejected_before_mutation'
        tombstone_applied_count =
            Count-State $zdoRows 'tombstone_applied_typed'
        canonical_delivery_count =
            Count-State $zdoRows 'canonical_delivery_banked'
        canonical_ack_count = Count-State $zdoRows 'canonical_ack_queued'
        native_rpc_zdo_data_count = $nativeTripwire
        typed_apply_failure_count = $applyFailures.Count
        http_failure_count = $httpFailures.Count
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
            one_stable_logical_peer = $logicalIds.Count -eq 1
            zdo_rows_match_logical_peer =
                $logicalIds.Count -eq 1 -and
                $zdoLogicalIds.Count -eq 1 -and
                $logicalIds[0] -eq $zdoLogicalIds[0]
            fresh_process_uses_new_connection =
                $started.Count -ge 2 -and $connectionIds.Count -ge 2
            gateway_restart_reincarnated_same_peer =
                $Client -ne 'omen' -or $reincarnated.Count -ge 1
            canonical_transport_only =
                @($zdoRows).Count -gt 0 -and
                $transports.Count -eq 1 -and
                $transports[0] -eq 'canonical_session'
            http_fallback_unused = $httpFailures.Count -eq 0
            probe_passed_once = $probePass.Count -eq 1
            interest_registered = $interest.Count -ge 1
            late_observer_snapshot_applied =
                $Client -ne 'i5' -or
                (Count-State $zdoRows 'snapshot_applied_typed') -ge 1
            valid_delta_applied =
                (Count-State $zdoRows 'delta_applied_typed') -ge 1
            stale_rejected_before_mutation =
                (Count-State $zdoRows 'stale_rejected_before_mutation') -ge 1
            malformed_rejected_before_mutation =
                (Count-State $zdoRows 'malformed_rejected_before_mutation') -ge 1
            tombstone_applied_typed =
                (Count-State $zdoRows 'tombstone_applied_typed') -ge 1
            delivery_acknowledged =
                (Count-State $zdoRows 'canonical_delivery_banked') -ge 1 -and
                (Count-State $zdoRows 'canonical_ack_queued') -ge 1
            selected_native_rpc_zdo_data_zero =
                $nativeTripwire -eq 0 -and $nativeFieldUse.Count -eq 0
            typed_apply_failures_zero = $applyFailures.Count -eq 0
            intended_renderer = $lifecycle.joined.detail -match $expectedRenderer
            lifecycle_completed =
                $lifecycle.result -eq 'joined_held_and_stopped'
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
$serverZdoRows = @(
    Read-JsonLines (Join-Path $absoluteRun 'server\zdo-journal-cutover.jsonl')
)
$serverSessionRows = @(
    Read-JsonLines (
        Join-Path $absoluteRun 'server\lumberjacks-game-session.jsonl')
)
$serverStarted = @($serverSessionRows | Where-Object {
    $_.state -eq 'session_started'
})
$serverLogicalIds = @(Unique-NonEmpty @($serverStarted | ForEach-Object {
    $_.logical_peer_id
}))
$serverConnectionIds = @(Unique-NonEmpty @($serverStarted | ForEach-Object {
    $_.connection_id
}))
$serverZdoLogicalIds = @(Unique-NonEmpty @($serverZdoRows | ForEach-Object {
    $_.logical_peer_id
}))
$serverTransports = @(Unique-NonEmpty @($serverZdoRows | ForEach-Object {
    $_.transport
}))
$serverReincarnated = @($serverStarted | Where-Object {
    $_.detail -match 'reincarnated=true'
})
$driveCreated = @($serverZdoRows | Where-Object {
    $_.state -eq 'drive_created'
})
$driveReleased = @($serverZdoRows | Where-Object {
    $_.state -eq 'drive_faults_and_valid_queued'
})
$driveComplete = @($serverZdoRows | Where-Object {
    $_.state -eq 'drive_complete'
})
$mutations = @($serverZdoRows | Where-Object {
    $_.state -eq 'mutation_posted'
})
$acceptedMutations = @($serverZdoRows | Where-Object {
    $_.state -eq 'canonical_mutation_accepted'
})
$interestStatus = @($serverZdoRows | Where-Object {
    $_.state -eq 'canonical_interest_status'
})
$serverFailures = @($serverZdoRows | Where-Object {
    $_.state -match 'failed' -or
    $_.state -in @('capture_failed', 'drive_request_rejected')
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
$canonicalArm = @($journalRuntime.receipts | Where-Object {
    $_.setting -eq 'zdoJournalCanonicalSessionEnabled' -and
    $_.effective_value -eq 'true'
})
$canonicalDisarm = @($journalRuntime.receipts | Where-Object {
    $_.setting -eq 'zdoJournalCanonicalSessionEnabled' -and
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
    $health =
        Invoke-WebRequest -UseBasicParsing -Uri $GatewayHealthUrl -TimeoutSec 5
    $gatewayHealth.reachable = $true
    $gatewayHealth.status_code = [int]$health.StatusCode
} catch {
    $gatewayHealth.error = $_.Exception.GetType().Name
}

$clientChecks = @($clients | ForEach-Object {
    $values = $_['checks']
    $values.Keys | ForEach-Object { [bool]$values[$_] }
})
$clientLogicalIds = @(Unique-NonEmpty @($clients | ForEach-Object {
    $_['logical_peer_ids']
}))
$hashes = @(Unique-NonEmpty @($clients | ForEach-Object {
    $_['plugin_sha256']
}))
$versions = @(Unique-NonEmpty @($clients | ForEach-Object {
    $_['plugin_version']
}))
$serverVersions = @(Unique-NonEmpty @(
    $journalRuntime.receipts | ForEach-Object {
    $_.build_version
}))
$checks = [ordered]@{
    both_clients_passed =
        $clientChecks.Count -gt 0 -and
        @($clientChecks | Where-Object { -not $_ }).Count -eq 0
    client_logical_peers_are_distinct = $clientLogicalIds.Count -eq 2
    server_one_stable_logical_peer = $serverLogicalIds.Count -eq 1
    server_zdo_rows_match_logical_peer =
        $serverLogicalIds.Count -eq 1 -and
        $serverZdoLogicalIds.Count -eq 1 -and
        $serverLogicalIds[0] -eq $serverZdoLogicalIds[0]
    server_gateway_restart_new_connection_same_peer =
        $serverStarted.Count -ge 2 -and
        $serverConnectionIds.Count -ge 2 -and
        $serverReincarnated.Count -ge 1
    server_canonical_transport_only =
        $serverZdoRows.Count -gt 0 -and
        $serverTransports.Count -eq 1 -and
        $serverTransports[0] -eq 'canonical_session'
    server_drive_created_once = $driveCreated.Count -eq 1
    server_released_after_two_interests =
        $driveReleased.Count -eq 1 -and
        $driveReleased[0].detail -match 'interested_recipients=2'
    server_drive_completed_once = $driveComplete.Count -eq 1
    selected_create_sync_list_candidates_zero =
        $driveComplete.Count -eq 1 -and
        [long]$driveComplete[0].native_create_sync_candidates -eq 0 -and
        $driveComplete[0].detail -match 'native_candidates=0'
    every_mutation_accepted_on_canonical_session =
        $mutations.Count -eq 6 -and
        $acceptedMutations.Count -eq $mutations.Count
    server_observed_two_interests =
        @($interestStatus | Where-Object {
            $_.detail -match 'interested_recipients=2'
        }).Count -ge 1
    server_failures_zero = $serverFailures.Count -eq 0
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
    canonical_gate_armed_and_disarmed =
        $canonicalArm.Count -eq 1 -and $canonicalDisarm.Count -eq 1 -and
        [string]::IsNullOrWhiteSpace($journalRuntime.disarm_error)
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
    server_and_clients_loaded_same_version =
        $versions.Count -eq 1 -and
        $serverVersions.Count -eq 1 -and
        $versions[0] -eq $serverVersions[0]
    gateway_healthy_after_run =
        $gatewayHealth.reachable -and $gatewayHealth.status_code -eq 200
}
$passed =
    @($checks.Keys | Where-Object { -not [bool]$checks[$_] }).Count -eq 0
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'canonical_zdo_session_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($passed) { 'passed' } else { 'failed' }
    boundary = [ordered]@{
        proved =
            'C3 ZDO mutation, interest, delivery, and ACK use the durable C1 session with stable logical peers across transport turnover.'
        not_yet_proved =
            'Gameplay ownership leases and authoritative pickup/action results remain C4 work.'
    }
    clients = $clients
    server = [ordered]@{
        logical_peer_ids = $serverLogicalIds
        connection_ids = $serverConnectionIds
        zdo_logical_peer_ids = $serverZdoLogicalIds
        session_started_count = $serverStarted.Count
        gateway_reincarnation_count = $serverReincarnated.Count
        transports = $serverTransports
        drive_created_count = $driveCreated.Count
        drive_release_count = $driveReleased.Count
        drive_complete_count = $driveComplete.Count
        mutation_posted_count = $mutations.Count
        mutation_accepted_count = $acceptedMutations.Count
        interest_status_count = $interestStatus.Count
        failure_count = $serverFailures.Count
    }
    gateway = [ordered]@{
        restart = $restart
        final = $final
        health = $gatewayHealth
    }
    artifact = [ordered]@{
        plugin_sha256 = if ($hashes.Count -eq 1) { $hashes[0] } else { $hashes }
        plugin_version =
            if ($versions.Count -eq 1) { $versions[0] } else { $versions }
        server_version =
            if ($serverVersions.Count -eq 1) {
                $serverVersions[0]
            } else {
                $serverVersions
            }
    }
    checks = $checks
}
$output = Join-Path $absoluteRun 'c4a-machine-summary.json'
[IO.File]::WriteAllText(
    $output,
    ($summary | ConvertTo-Json -Depth 16) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 16
if (-not $passed) { exit 1 }
