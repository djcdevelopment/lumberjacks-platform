#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one physical-client C2b run into a routed-RPC cutover gate receipt.
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

function Count-Rows(
    [object[]] $Rows,
    [string] $State,
    [string] $Action = '',
    [string] $Method = '') {
    return @($Rows | Where-Object {
        $_.state -eq $State -and
        ([string]::IsNullOrEmpty($Action) -or $_.action_id -eq $Action) -and
        ([string]::IsNullOrEmpty($Method) -or $_.method -eq $Method)
    }).Count
}

function Client-Summary([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $rows = @(Read-JsonLines (Join-Path $directory 'routed-rpc-cutover.jsonl'))
    $actions = [ordered]@{
        request = "$Client-routed-request"
        broadcast = "$Client-routed-broadcast"
        target = "$Client-routed-target"
        withhold = "$Client-routed-withhold"
    }
    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json
    $requestPass = Count-Rows $rows 'probe_passed' $actions.request
    $broadcastPass = Count-Rows $rows 'probe_passed' $actions.broadcast
    $targetPass = Count-Rows $rows 'probe_passed' $actions.target
    $withholdPass = Count-Rows $rows 'routed_expected_stale' $actions.withhold
    $nativeReceived = Count-Rows $rows 'native_route_received'
    $dispatchFailed = Count-Rows $rows 'lumberjacks_dispatch_failed'
    $duplicates = Count-Rows $rows 'lumberjacks_route_duplicate'
    $attempts = Count-Rows $rows 'native_route_attempted'
    $suppressed = Count-Rows $rows 'native_route_suppressed'

    [ordered]@{
        client = $Client
        actions = $actions
        request_pass_count = $requestPass
        broadcast_pass_count = $broadcastPass
        target_zdo_pass_count = $targetPass
        withhold_stale_count = $withholdPass
        native_receive_count = $nativeReceived
        dispatch_failure_count = $dispatchFailed
        duplicate_delivery_count = $duplicates
        selected_native_attempt_count = $attempts
        selected_native_suppressed_count = $suppressed
        plugin_sha256 = $lifecycle.plugin_sha256
        lifecycle_result = $lifecycle.result
        scenario_terminal = $lifecycle.scenario_terminal.state
        checks = [ordered]@{
            client_to_server_and_response_passed = $requestPass -eq 1
            server_broadcast_passed = $broadcastPass -eq 1
            target_zdo_interaction_passed = $targetPass -eq 1
            withheld_response_became_stale = $withholdPass -eq 1
            no_native_fallback = $nativeReceived -eq 0
            no_dispatch_failure = $dispatchFailed -eq 0
            no_duplicate_delivery = $duplicates -eq 0
            selected_native_attempts_suppressed =
                $attempts -gt 0 -and $attempts -eq $suppressed
            lifecycle_completed = $lifecycle.result -eq 'joined_held_and_stopped'
            scenario_completed = $lifecycle.scenario_terminal.state -eq 'scenario_complete'
        }
    }
}

$clients = @(
    Client-Summary 'omen'
    Client-Summary 'i5'
)
$serverRows = @(
    Read-JsonLines (Join-Path $absoluteRun 'server\routed-rpc-cutover.jsonl')
)
$resetMethod = 'RPC_ResetCloth'
$serverNativeReceived = Count-Rows $serverRows 'native_route_received'
$serverFailures = Count-Rows $serverRows 'lumberjacks_dispatch_failed'
$serverDuplicates = Count-Rows $serverRows 'lumberjacks_route_duplicate'
$serverResetDispatch = @($serverRows | Where-Object {
    $_.state -eq 'lumberjacks_handler_dispatched' -and
    $_.method -eq $resetMethod -and
    $_.action_id -in @('omen-routed-target', 'i5-routed-target')
})
$serverRequestDispatch = @($serverRows | Where-Object {
    $_.state -eq 'lumberjacks_handler_dispatched' -and
    $_.method -eq 'ComfyNetworkSense_CutoverRoutedRequest'
})
$serverBroadcastRequestDispatch = @($serverRows | Where-Object {
    $_.state -eq 'lumberjacks_handler_dispatched' -and
    $_.method -eq 'ComfyNetworkSense_CutoverRoutedBroadcastRequest'
})
$serverAttempts = Count-Rows $serverRows 'native_route_attempted'
$serverSuppressed = Count-Rows $serverRows 'native_route_suppressed'

$runtime =
    Get-Content -LiteralPath (
        Join-Path $absoluteRun 'server-runtime-routed-rpc.json') -Raw |
    ConvertFrom-Json
$arm = @($runtime.receipts | Where-Object {
    $_.setting -eq 'routedRpcCutoverEnabled' -and $_.effective_value -eq 'true'
})
$disarm = @($runtime.receipts | Where-Object {
    $_.setting -eq 'routedRpcCutoverEnabled' -and $_.effective_value -eq 'false'
})
$gatewaySet = @($runtime.receipts | Where-Object {
    $_.setting -eq 'lumberjacksGatewayUrl' -and
    $_.request_id -like '*-routed-gateway'
})
$gatewayRestore = @($runtime.receipts | Where-Object {
    $_.setting -eq 'lumberjacksGatewayUrl' -and
    $_.request_id -like '*-routed-restore' -and
    $_.effective_value -eq $gatewaySet[0].old_value
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
$checks = [ordered]@{
    both_clients_passed =
        $clientChecks.Count -gt 0 -and
        @($clientChecks | Where-Object { -not $_ }).Count -eq 0
    server_received_all_four_requests = $serverRequestDispatch.Count -eq 4
    server_received_both_broadcast_requests = $serverBroadcastRequestDispatch.Count -eq 2
    real_target_zdo_dispatched_once_per_client =
        $serverResetDispatch.Count -eq 2 -and
        @($serverResetDispatch.action_id | Sort-Object -Unique).Count -eq 2
    server_selected_native_attempts_suppressed =
        $serverAttempts -gt 0 -and $serverAttempts -eq $serverSuppressed
    server_received_no_native_copy = $serverNativeReceived -eq 0
    server_had_no_dispatch_failure = $serverFailures -eq 0
    server_had_no_duplicate_delivery = $serverDuplicates -eq 0
    runtime_gate_armed_once = $arm.Count -eq 1
    runtime_gate_disarmed_once =
        $disarm.Count -eq 1 -and [string]::IsNullOrWhiteSpace($runtime.disarm_error)
    server_gateway_restored =
        $gatewaySet.Count -eq 1 -and $gatewayRestore.Count -eq 1
    client_artifact_hashes_match = $hashes.Count -eq 1
    gateway_healthy_after_run =
        $gatewayHealth.reachable -and $gatewayHealth.status_code -eq 200
}
$passed =
    @($checks.Keys | Where-Object { -not [bool]$checks[$_] }).Count -eq 0
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'lumberjacks_routed_rpc_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($passed) { 'passed' } else { 'failed' }
    clients = $clients
    server = [ordered]@{
        request_dispatch_count = $serverRequestDispatch.Count
        broadcast_request_dispatch_count = $serverBroadcastRequestDispatch.Count
        reset_cloth_dispatch_count = $serverResetDispatch.Count
        selected_native_attempt_count = $serverAttempts
        selected_native_suppressed_count = $serverSuppressed
        native_receive_count = $serverNativeReceived
        dispatch_failure_count = $serverFailures
        duplicate_delivery_count = $serverDuplicates
    }
    gateway_health = $gatewayHealth
    checks = $checks
}
$output = Join-Path $absoluteRun 'c2b-machine-summary.json'
[IO.File]::WriteAllText(
    $output,
    ($summary | ConvertTo-Json -Depth 14) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 14
if (-not $passed) { exit 1 }
