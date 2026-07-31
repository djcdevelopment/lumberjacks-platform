#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one physical-client C4 run into an ownership-lease cutover receipt.

.DESCRIPTION
This reducer verifies the real AM4 boundary: both GPU clients exercise lease
reclaim, wrong-epoch and expiry rejection, poison Valheim's selected native
ownership/inventory paths, receive one authoritative item, reconnect, and stop.
It also verifies the dedicated server created and destroyed both real ZDOs and
received the Gateway result receipts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunDirectory,

    [Parameter(Mandatory)]
    [string] $RunId
)

$ErrorActionPreference = 'Stop'
$absoluteRun = (Resolve-Path -LiteralPath $RunDirectory -ErrorAction Stop).Path

function Read-JsonLines([string] $Path) {
    $rows = @()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    foreach ($line in [IO.File]::ReadLines($Path)) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    return @($rows)
}

function Read-JsonFile([string] $Name) {
    [IO.File]::ReadAllText((Join-Path $absoluteRun $Name)) | ConvertFrom-Json
}

function Count-State(
    [object[]] $Rows,
    [string] $State,
    [string] $Action = '') {
    @($Rows | Where-Object {
        $_.state -eq $State -and
        ([string]::IsNullOrEmpty($Action) -or $_.action_id -eq $Action)
    }).Count
}

function Count-Detail(
    [object[]] $Rows,
    [string] $State,
    [string] $Action,
    [string] $Pattern) {
    @($Rows | Where-Object {
        $_.state -eq $State -and
        ([string]::IsNullOrEmpty($Action) -or $_.action_id -eq $Action) -and
        [string]$_.detail -match $Pattern
    }).Count
}

function Detail-Integer([string] $Detail, [string] $Name) {
    $match = [regex]::Match(
        [string]$Detail,
        '(?:^|\s)' + [regex]::Escape($Name) + '=(?<value>-?\d+)(?:\s|$)')
    if (-not $match.Success) { return $null }
    return [long]$match.Groups['value'].Value
}

function Client-Summary([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $action = "$Client-ownership-lease-pickup"
    $rows = @(Read-JsonLines (
        Join-Path $directory 'ownership-lease-cutover.jsonl'))
    $scenario = @(Read-JsonLines (
        Join-Path $directory 'native-cutover-scenario-receipts.jsonl'))
    $lifecycle =
        [IO.File]::ReadAllText((Join-Path $directory 'lifecycle.json')) |
        ConvertFrom-Json
    $playerLog = [IO.File]::ReadAllText((Join-Path $directory 'player.log'))
    $inventory = @($rows | Where-Object {
        $_.state -eq 'authoritative_inventory_applied' -and
        $_.action_id -eq $action
    })
    $before = if ($inventory.Count -eq 1) {
        Detail-Integer ([string]$inventory[0].detail) 'inventory_units_before'
    } else { $null }
    $after = if ($inventory.Count -eq 1) {
        Detail-Integer ([string]$inventory[0].detail) 'inventory_units_after'
    } else { $null }
    $requestOwnMax = @(
        $rows | ForEach-Object { [long]$_.native_request_own_suppressed } |
            Measure-Object -Maximum
    )[0].Maximum
    $pickupMax = @(
        $rows | ForEach-Object {
            [long]$_.native_inventory_pickup_suppressed
        } | Measure-Object -Maximum
    )[0].Maximum
    $expectedRenderer = if ($Client -eq 'omen') {
        'renderer=NVIDIA_GeForce_RTX_5070'
    } else {
        'renderer=Intel\(R\)_Iris\(R\)_Xe_Graphics'
    }

    [ordered]@{
        client = $Client
        action_id = $action
        plugin_sha256 = $lifecycle.plugin_sha256
        lease_grant_count = Count-State $rows 'lease_granted' $action
        reclaimed_rejection_count =
            Count-Detail $rows 'action_rejected' $action 'reason=lease_reclaimed'
        wrong_epoch_rejection_count =
            Count-Detail $rows 'action_rejected' $action 'reason=epoch_mismatch'
        expired_rejection_count =
            Count-Detail $rows 'action_rejected' $action 'reason=lease_expired'
        native_request_own_suppressed = [long]$requestOwnMax
        native_inventory_pickup_suppressed = [long]$pickupMax
        authoritative_inventory_apply_count = $inventory.Count
        inventory_units_before = $before
        inventory_units_after = $after
        completion_ack_count =
            Count-Detail $rows 'canonical_frame_acked' $action `
                'type=valheim_ownership_action_completed'
        probe_pass_count = Count-State $rows 'probe_passed' $action
        run_context_reset_count = Count-State $rows 'run_context_reset'
        scenario_action_complete_count =
            Count-State $scenario 'completed' $action
        lifecycle_result = $lifecycle.result
        resume_count = [int]$lifecycle.resume_count
        scenario_terminal = $lifecycle.scenario_terminal.state
        renderer_marker_present = [bool]($playerLog -match $expectedRenderer)
        checks = [ordered]@{
            three_lease_epochs_received =
                (Count-State $rows 'lease_granted' $action) -eq 3
            disconnected_lease_reclaimed =
                (Count-Detail $rows 'action_rejected' $action `
                    'reason=lease_reclaimed') -eq 1
            wrong_epoch_rejected =
                (Count-Detail $rows 'action_rejected' $action `
                    'reason=epoch_mismatch') -eq 1
            expired_lease_rejected =
                (Count-Detail $rows 'action_rejected' $action `
                    'reason=lease_expired') -eq 1
            native_request_own_poisoned = [long]$requestOwnMax -gt 0
            native_inventory_pickup_poisoned = [long]$pickupMax -gt 0
            authoritative_inventory_applied_once = $inventory.Count -eq 1
            inventory_incremented_exactly_once =
                $null -ne $before -and $null -ne $after -and
                [long]$after -eq [long]$before + 1
            completion_reliably_acked =
                (Count-Detail $rows 'canonical_frame_acked' $action `
                    'type=valheim_ownership_action_completed') -eq 1
            probe_passed = (Count-State $rows 'probe_passed' $action) -eq 1
            no_probe_failure = (Count-State $rows 'probe_failed' $action) -eq 0
            run_context_initialized =
                (Count-State $rows 'run_context_reset') -ge 1
            scenario_action_completed =
                (Count-State $scenario 'completed' $action) -eq 1
            fresh_process_resume_completed = [int]$lifecycle.resume_count -eq 1
            lifecycle_completed =
                $lifecycle.result -eq 'joined_held_and_stopped'
            scenario_completed =
                $lifecycle.scenario_terminal.state -eq 'scenario_complete'
            intended_gpu_renderer_retained =
                [bool]($playerLog -match $expectedRenderer)
        }
    }
}

function Server-Summary() {
    $rows = @(Read-JsonLines (
        Join-Path $absoluteRun 'server\ownership-lease-cutover.jsonl'))
    $actions = @(
        'omen-ownership-lease-pickup',
        'i5-ownership-lease-pickup'
    )
    $perAction = @()
    foreach ($action in $actions) {
        $perAction += [ordered]@{
            action_id = $action
            target_created_count = Count-State $rows 'target_created' $action
            lease_issue_count = Count-State $rows 'lease_issue_sent' $action
            lease_receipt_count = Count-State $rows 'lease_receipt' $action
            action_authorized_count =
                Count-Detail $rows 'action_authorized' $action 'epoch=3'
            target_destroyed_count =
                Count-Detail $rows 'authoritative_target_destroyed' $action `
                    'server_owner_restored=true native_destroy_rpc=false'
            result_sent_count = Count-State $rows 'action_result_sent' $action
            result_receipt_count =
                Count-Detail $rows 'result_receipt' $action `
                    'result=completed epoch=3'
        }
    }
    $selectionMax = @(
        $rows | ForEach-Object { [long]$_.native_selection_suppressed } |
            Measure-Object -Maximum
    )[0].Maximum
    $releaseMax = @(
        $rows | ForEach-Object { [long]$_.native_release_suppressed } |
            Measure-Object -Maximum
    )[0].Maximum
    $destroyMax = @(
        $rows | ForEach-Object { [long]$_.native_destroy_suppressed } |
            Measure-Object -Maximum
    )[0].Maximum
    $reset = @($rows | Where-Object { $_.state -eq 'run_context_reset' })
    $allPerAction = @($perAction | Where-Object {
        $_.target_created_count -eq 1 -and
        $_.lease_issue_count -eq 3 -and
        $_.lease_receipt_count -eq 3 -and
        $_.action_authorized_count -eq 1 -and
        $_.target_destroyed_count -eq 1 -and
        $_.result_sent_count -eq 1 -and
        $_.result_receipt_count -eq 1
    }).Count -eq 2

    [ordered]@{
        actions = $perAction
        run_context_reset_count = $reset.Count
        stale_targets_destroyed_on_reset =
            if ($reset.Count -gt 0) {
                Detail-Integer ([string]$reset[-1].detail) `
                    'stale_targets_destroyed'
            } else { $null }
        native_create_sync_list_suppressed = [long]$selectionMax
        native_release_suppressed = [long]$releaseMax
        native_destroy_suppressed = [long]$destroyMax
        checks = [ordered]@{
            both_real_targets_completed = $allPerAction
            run_state_isolated = $reset.Count -ge 1
            native_selection_poisoned = [long]$selectionMax -gt 0
            native_release_poisoned = [long]$releaseMax -gt 0
            native_destroy_poisoned = [long]$destroyMax -gt 0
            no_native_destroy_rpc =
                (Count-State $rows 'authoritative_target_destroyed') -eq 2 -and
                (Count-Detail $rows 'authoritative_target_destroyed' '' `
                    'native_destroy_rpc=false') -eq 2
            no_server_frame_failure =
                @($rows | Where-Object {
                    $_.state -in @(
                        'canonical_frame_invalid',
                        'native_invalid_filter_failed')
                }).Count -eq 0
        }
    }
}

function Runtime-Summary() {
    $runtime = Read-JsonFile 'server-runtime-ownership-lease.json'
    $arm = @($runtime.receipts | Where-Object {
        $_.request_id -eq "$RunId-ownership-arm"
    })
    $disarm = @($runtime.receipts | Where-Object {
        $_.request_id -eq "$RunId-ownership-disarm"
    })
    [ordered]@{
        arm_count = $arm.Count
        disarm_count = $disarm.Count
        disarm_error = $runtime.disarm_error
        checks = [ordered]@{
            server_gate_armed = $arm.Count -eq 1 -and
                $arm[0].effective_value -eq 'true'
            server_gate_disarmed = $disarm.Count -eq 1 -and
                $disarm[0].effective_value -eq 'false'
            no_disarm_error = $null -eq $runtime.disarm_error
        }
    }
}

$composition = Read-JsonFile 'composition.json'
$gateway = Read-JsonFile 'gateway-journal-final.json'
$clients = @(
    Client-Summary 'omen'
    Client-Summary 'i5'
)
$server = Server-Summary
$runtime = Runtime-Summary

$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'ownership_lease_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    composition = [ordered]@{
        result = $composition.result
        client_count = @($composition.clients).Count
        checks = [ordered]@{
            composition_completed = $composition.result -eq 'completed'
            both_clients_completed = @($composition.clients | Where-Object {
                $_.result -eq 'joined_held_and_stopped' -and
                $_.resume_count -eq 1 -and
                $_.scenario_terminal.state -eq 'scenario_complete'
            }).Count -eq 2
        }
    }
    clients = $clients
    server = $server
    runtime = $runtime
    journal_cleanup = [ordered]@{
        pending_before_cleanup = [long]$gateway.run_status.pending
        reset_ok = [bool]$gateway.reset.ok
        objects_removed = [long]$gateway.reset.objects_removed
        checks = [ordered]@{
            no_pending_delivery = [long]$gateway.run_status.pending -eq 0
            disposable_journal_deleted =
                [bool]$gateway.reset.ok -and
                [long]$gateway.reset.objects_removed -gt 0
        }
    }
}

$checkSets = @(
    [ordered]@{ prefix = 'composition'; values = $summary.composition.checks }
    [ordered]@{ prefix = 'client.omen'; values = $clients[0].checks }
    [ordered]@{ prefix = 'client.i5'; values = $clients[1].checks }
    [ordered]@{ prefix = 'server'; values = $server.checks }
    [ordered]@{ prefix = 'runtime'; values = $runtime.checks }
    [ordered]@{ prefix = 'journal_cleanup'; values = $summary.journal_cleanup.checks }
)
$failed = @()
foreach ($set in $checkSets) {
    foreach ($check in $set.values.GetEnumerator()) {
        if (-not [bool]$check.Value) {
            $failed += "$($set.prefix).$($check.Key)"
        }
    }
}
$summary['result'] = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
$summary['failed_checks'] = $failed
$output = Join-Path $absoluteRun 'ownership-lease-cutover-summary.json'
[IO.File]::WriteAllText(
    $output,
    ($summary | ConvertTo-Json -Depth 16) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$summary | ConvertTo-Json -Depth 16
if ($failed.Count -ne 0) {
    throw "Ownership-lease cutover summary failed: $($failed -join ', ')"
}
