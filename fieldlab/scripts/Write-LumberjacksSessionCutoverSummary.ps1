#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one physical-client C1 run into a durable Lumberjacks session gate receipt.
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
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    return @($rows)
}

function One([object[]] $Rows, [string] $State, [string] $Action) {
    $matches = @($Rows | Where-Object {
        $_.state -eq $State -and $_.action_id -eq $Action
    })
    if ($matches.Count -ne 1) {
        throw "Expected one $State row for $Action, observed $($matches.Count)."
    }
    return $matches[0]
}

function ClientSummary([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $rows = @(Read-JsonLines (Join-Path $directory 'lumberjacks-game-session.jsonl'))
    $resumeAction = "$Client-session-resume"
    $timeoutAction = "$Client-session-timeout"
    $started = One $rows 'probe_started' $resumeAction
    $drop = One $rows 'forced_socket_drop' $resumeAction
    $resumed = One $rows 'session_started' $resumeAction
    $passed = One $rows 'probe_passed' $resumeAction
    $response = One $rows 'control_response_sent' $resumeAction
    $timeout = One $rows 'expected_timeout' $timeoutAction
    $timeoutResponse = One $rows 'control_response_sent' $timeoutAction
    $requests = @($rows | Where-Object {
        $_.state -eq 'control_request' -and $_.action_id -eq $resumeAction
    })
    $requestSequences = @($requests | ForEach-Object {
        if ($_.detail -match 'sequence=(\d+)') { [long]$Matches[1] }
    })
    $resumeEpoch =
        if ($resumed.resume_epoch -ne $null) { [long]$resumed.resume_epoch } else { -1 }
    $initialEpoch =
        if ($started.resume_epoch -ne $null) { [long]$started.resume_epoch } else { -1 }
    $connectionStable =
        -not [string]::IsNullOrWhiteSpace([string]$started.connection_id) -and
        $started.connection_id -eq $drop.connection_id -and
        $started.connection_id -eq $resumed.connection_id -and
        $started.connection_id -eq $passed.connection_id
    $requestReplayed =
        $requests.Count -eq 2 -and
        $requestSequences.Count -eq 2 -and
        $requestSequences[0] -eq $requestSequences[1]
    $responseCountOne = [string]$passed.detail -match 'response_count=1'
    $timeoutBounded =
        $timeout.detail -eq 'bounded_receipt_timeout_no_native_fallback'

    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json
    [ordered]@{
        client = $Client
        connection_id = $started.connection_id
        initial_resume_epoch = $initialEpoch
        resumed_epoch = $resumeEpoch
        request_sequence = if ($requestSequences.Count) { $requestSequences[0] } else { $null }
        request_delivery_count = $requests.Count
        response_send_count = @($response).Count
        gateway_response_count = if ($responseCountOne) { 1 } else { $null }
        timeout_response_send_count = @($timeoutResponse).Count
        timeout_result = $timeout.detail
        plugin_sha256 = $lifecycle.plugin_sha256
        lifecycle_result = $lifecycle.result
        scenario_terminal = $lifecycle.scenario_terminal.state
        checks = [ordered]@{
            stable_connection_id = $connectionStable
            resume_epoch_advanced = $resumeEpoch -gt $initialEpoch
            exact_request_sequence_replayed = $requestReplayed
            exactly_one_response_sent = @($response).Count -eq 1
            gateway_accepted_exactly_one_response = $responseCountOne
            bounded_timeout_without_fallback = $timeoutBounded
            lifecycle_completed = $lifecycle.result -eq 'joined_held_and_stopped'
            scenario_completed = $lifecycle.scenario_terminal.state -eq 'scenario_complete'
        }
    }
}

$clients = @(
    ClientSummary 'omen'
    ClientSummary 'i5'
)
$gatewayHealth = [ordered]@{ reachable = $false; status_code = $null }
try {
    $health = Invoke-WebRequest -UseBasicParsing -Uri $GatewayHealthUrl -TimeoutSec 5
    $gatewayHealth.reachable = $true
    $gatewayHealth.status_code = [int]$health.StatusCode
} catch {
    $gatewayHealth.error = $_.Exception.GetType().Name
}

$allChecks = @($clients | ForEach-Object {
    $clientChecks = $_['checks']
    $clientChecks.Keys | ForEach-Object { [bool]$clientChecks[$_] }
})
$hashes = @($clients | ForEach-Object { [string]$_['plugin_sha256'] } | Sort-Object -Unique)
$checks = [ordered]@{
    both_clients_passed = $allChecks.Count -gt 0 -and @($allChecks | Where-Object { -not $_ }).Count -eq 0
    client_artifact_hashes_match = $hashes.Count -eq 1
    gateway_healthy_after_run = $gatewayHealth.reachable -and $gatewayHealth.status_code -eq 200
}
$passed = @($checks.Keys | Where-Object { -not [bool]$checks[$_] }).Count -eq 0
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'lumberjacks_durable_session_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($passed) { 'passed' } else { 'failed' }
    clients = $clients
    gateway_health = $gatewayHealth
    checks = $checks
}
$output = Join-Path $absoluteRun 'c1-machine-summary.json'
[IO.File]::WriteAllText(
    $output,
    ($summary | ConvertTo-Json -Depth 14) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 14
if (-not $passed) { exit 1 }
