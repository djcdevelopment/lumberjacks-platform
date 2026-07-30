#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one physical-client C2a run into a typed direct-control cutover gate receipt.
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
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    return @($rows)
}

function Count-State([object[]] $Rows, [string] $State, [string] $Action = '') {
    return @($Rows | Where-Object {
        $_.state -eq $State -and
        ([string]::IsNullOrEmpty($Action) -or $_.action_id -eq $Action)
    }).Count
}

function ClientSummary([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $sessionRows =
        @(Read-JsonLines (Join-Path $directory 'lumberjacks-game-session.jsonl'))
    $nativeRows =
        @(Read-JsonLines (Join-Path $directory 'direct-control-cutover.jsonl'))
    $deliverAction = "$Client-direct-pulse"
    $withholdAction = "$Client-direct-withhold"
    $deliverCount =
        Count-State $sessionRows 'lumberjacks_direct_pulse_received' $deliverAction
    $staleCount =
        Count-State $sessionRows 'direct_pulse_expected_stale' $withholdAction
    $nativeSessionCount =
        Count-State $sessionRows 'native_direct_pulse_received'
    $nativeHandlerRegistrationCount =
        Count-State $nativeRows 'native_handler_registered'
    $nativeRunnerCount =
        Count-State $nativeRows 'native_received'
    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json

    [ordered]@{
        client = $Client
        deliver_action = $deliverAction
        withhold_action = $withholdAction
        lumberjacks_delivery_count = $deliverCount
        expected_stale_count = $staleCount
        native_handler_registration_count = $nativeHandlerRegistrationCount
        native_session_delivery_count = $nativeSessionCount
        native_handler_delivery_count = $nativeRunnerCount
        plugin_sha256 = $lifecycle.plugin_sha256
        lifecycle_result = $lifecycle.result
        scenario_terminal = $lifecycle.scenario_terminal.state
        checks = [ordered]@{
            exactly_one_typed_delivery = $deliverCount -eq 1
            withheld_copy_became_stale = $staleCount -eq 1
            native_tripwire_registered = $nativeHandlerRegistrationCount -gt 0
            no_native_session_fallback = $nativeSessionCount -eq 0
            no_native_handler_fallback = $nativeRunnerCount -eq 0
            lifecycle_completed = $lifecycle.result -eq 'joined_held_and_stopped'
            scenario_completed = $lifecycle.scenario_terminal.state -eq 'scenario_complete'
        }
    }
}

$clients = @(
    ClientSummary 'omen'
    ClientSummary 'i5'
)
$serverRows = @(
    Read-JsonLines (
        Join-Path $absoluteRun 'server\direct-control-cutover.jsonl')
)
$attempted = Count-State $serverRows 'native_attempted'
$suppressed = Count-State $serverRows 'native_suppressed'
$nativeReceived = Count-State $serverRows 'native_received'
$runtime =
    Get-Content -LiteralPath (
        Join-Path $absoluteRun 'server-runtime-direct-control.json') -Raw |
    ConvertFrom-Json
$arm = @($runtime.receipts | Where-Object {
    $_.setting -eq 'directControlCutoverEnabled' -and $_.effective_value -eq 'true'
})
$disarm = @($runtime.receipts | Where-Object {
    $_.setting -eq 'directControlCutoverEnabled' -and $_.effective_value -eq 'false'
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
    selected_native_attempted = $attempted -gt 0
    every_selected_native_attempt_suppressed =
        $attempted -gt 0 -and $attempted -eq $suppressed
    server_received_no_native_copy = $nativeReceived -eq 0
    runtime_gate_armed_once = $arm.Count -eq 1
    runtime_gate_disarmed_once =
        $disarm.Count -eq 1 -and [string]::IsNullOrWhiteSpace($runtime.disarm_error)
    client_artifact_hashes_match = $hashes.Count -eq 1
    gateway_healthy_after_run =
        $gatewayHealth.reachable -and $gatewayHealth.status_code -eq 200
}
$passed =
    @($checks.Keys | Where-Object { -not [bool]$checks[$_] }).Count -eq 0
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'lumberjacks_direct_control_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($passed) { 'passed' } else { 'failed' }
    clients = $clients
    server = [ordered]@{
        selected_native_attempt_count = $attempted
        selected_native_suppressed_count = $suppressed
        native_receive_count = $nativeReceived
    }
    gateway_health = $gatewayHealth
    checks = $checks
}
$output = Join-Path $absoluteRun 'c2a-machine-summary.json'
[IO.File]::WriteAllText(
    $output,
    ($summary | ConvertTo-Json -Depth 14) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 14
if (-not $passed) { exit 1 }
