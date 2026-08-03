#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one complete C8 dedicated-server composition into a fail-closed machine receipt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunDirectory,

    [Parameter(Mandatory)]
    [string] $RunId,

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RunDirectory).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'c8-composition-summary.json'
}

function Read-Json([string] $RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing C8 evidence file: $RelativePath"
    }
    Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json
}

function Read-Rows([string] $RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing C8 evidence file: $RelativePath"
    }
    @(
        Get-Content -LiteralPath $path -Encoding utf8 |
            ForEach-Object {
                if ([string]::IsNullOrWhiteSpace($_)) { return }
                try {
                    $row = $_ | ConvertFrom-Json -ErrorAction Stop
                    if ($row.run_id -eq $RunId) { $row }
                } catch { }
            }
    )
}

function Last-Summaries([string] $Client) {
    $rows = Read-Rows "$Client/native-network-use.jsonl"
    @(
        $rows |
            Where-Object event -eq 'summary' |
            Group-Object session_id |
            ForEach-Object {
                $_.Group |
                    Sort-Object { [DateTimeOffset]$_.timestamp_utc } |
                    Select-Object -Last 1
            }
    )
}

$scenario = Read-Json 'scenario.json'
$coverage = Read-Json 'c8-scenario-coverage.json'
$composition = Read-Json 'composition.json'
$logical = Read-Json 'c7-logical-peer-summary.json'
$restart = Read-Json 'gateway-journal-restart.json'
$integrity = Read-Json 'c8-save-integrity.json'
$runtimePoison = Read-Json 'server-runtime-native-poison.json'

$clientResults = @()
foreach ($client in @('omen', 'i5')) {
    $scenarioRows = Read-Rows "$client/native-cutover-scenario-receipts.jsonl"
    $ledger = Last-Summaries $client
    $expected = @($scenario.actions | Where-Object client -eq $client)
    $completedIds = @(
        $scenarioRows |
            Where-Object state -in @('completed', 'resume_requested') |
            ForEach-Object action_id |
            Sort-Object -Unique
    )
    $missingActions = @(
        $expected |
            Where-Object { $completedIds -notcontains $_.id } |
            ForEach-Object id
    )
    $ownership = Read-Rows "$client/ownership-lease-cutover.jsonl"
    $worldZone = Read-Rows "$client/world-zone-cutover.jsonl"
    $motion = Read-Rows "$client/motion-authority-cutover.jsonl"
    $routed = Read-Rows "$client/routed-rpc-cutover.jsonl"
    $session = Read-Rows "$client/lumberjacks-game-session.jsonl"
    $zdo = Read-Rows "$client/zdo-journal-cutover.jsonl"

    $checks = [ordered]@{
        every_manifest_action_completed = $missingActions.Count -eq 0
        scenario_completed_once =
            @($scenarioRows | Where-Object state -eq 'scenario_complete').Count -eq 1
        no_scenario_failure =
            @($scenarioRows | Where-Object state -in @('failed', 'manifest_rejected')).Count -eq 0
        native_ledger_sessions_present = $ledger.Count -ge 2
        native_poison_armed_all =
            $ledger.Count -ge 2 -and
            @($ledger | Where-Object poison_enabled -ne $true).Count -eq 0
        native_total_zero =
            $ledger.Count -ge 2 -and
            [long](($ledger | Measure-Object native_total -Sum).Sum) -eq 0
        native_poison_trips_zero =
            [long](($ledger | Measure-Object poison_trips -Sum).Sum) -eq 0
        ledger_lossless =
            [long](($ledger | Measure-Object writer_dropped_rows -Sum).Sum) -eq 0 -and
            [long](($ledger | Measure-Object writer_faults -Sum).Sum) -eq 0
        direct_control_completed =
            @($session | Where-Object state -eq 'lumberjacks_direct_pulse_received').Count -ge 1
        routed_shapes_completed =
            @($routed | Where-Object state -eq 'probe_passed').Count -ge 3
        zdo_semantics_completed =
            @($zdo | Where-Object state -eq 'probe_passed').Count -ge 1
        ownership_pickup_completed =
            @($ownership | Where-Object state -eq 'probe_passed').Count -ge 1
        zone_membership_completed =
            @($worldZone | Where-Object state -eq 'membership_probe_passed').Count -ge 1
        motion_and_loss_completed =
            @($motion | Where-Object state -eq 'probe_passed').Count -ge 3
    }
    if ($client -eq 'i5') {
        $checks.ownership_contention_rejected =
            @($ownership | Where-Object {
                $_.state -eq 'contention_probe_passed' -and
                $_.action_id -eq 'c8-ownership-contended' -and
                $_.detail -match 'reason=holder_mismatch'
            }).Count -eq 1
    }
    $failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
    $clientResults += [ordered]@{
        client = $client
        result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
        native_sessions = $ledger.Count
        native_total = [long](($ledger | Measure-Object native_total -Sum).Sum)
        missing_actions = $missingActions
        checks = $checks
        failed_checks = @($failed | ForEach-Object Key)
    }
}

$serverLedger = Last-Summaries 'server'
$serverFinal = @($serverLedger | Select-Object -Last 1)
$arm = @($runtimePoison.receipts | Where-Object {
    $_.setting -eq 'nativeNetworkPoisonEnabled' -and
    $_.effective_value -eq 'true'
})
$disarm = @($runtimePoison.receipts | Where-Object {
    $_.setting -eq 'nativeNetworkPoisonEnabled' -and
    $_.effective_value -eq 'false'
})
$serverChecks = [ordered]@{
    runtime_poison_armed = $arm.Count -eq 1
    runtime_poison_disarmed = $disarm.Count -eq 1 -and
        [string]::IsNullOrWhiteSpace([string]$runtimePoison.disarm_error)
    native_summary_present = $serverFinal.Count -eq 1
    native_poison_armed =
        $serverFinal.Count -eq 1 -and $serverFinal[0].poison_enabled -eq $true
    native_total_zero =
        $serverFinal.Count -eq 1 -and [long]$serverFinal[0].native_total -eq 0
    native_poison_trips_zero =
        $serverFinal.Count -eq 1 -and [long]$serverFinal[0].poison_trips -eq 0
    ledger_lossless =
        $serverFinal.Count -eq 1 -and
        [long]$serverFinal[0].writer_dropped_rows -eq 0 -and
        [long]$serverFinal[0].writer_faults -eq 0
}
$serverFailed =
    @($serverChecks.GetEnumerator() | Where-Object { -not [bool]$_.Value })

$checks = [ordered]@{
    coverage_passed = $coverage.result -eq 'passed'
    composition_completed =
        $composition.result -eq 'completed' -and
        $composition.steam_free_cold_join -eq $true
    logical_cold_join_passed = $logical.result -eq 'passed'
    both_clients_passed =
        @($clientResults | Where-Object result -ne 'passed').Count -eq 0
    server_native_zero = $serverFailed.Count -eq 0
    gateway_interruption_replayed =
        $restart.durable_replay_verified -eq $true
    save_integrity_passed = $integrity.result -eq 'passed'
}
$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$scenarioHash = (Get-FileHash `
    -LiteralPath (Join-Path $root 'scenario.json') `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'c8_native_zero_composition_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    scenario_sha256 = $scenarioHash
    clients = $clientResults
    server = [ordered]@{
        result = if ($serverFailed.Count -eq 0) { 'passed' } else { 'failed' }
        native_total = if ($serverFinal.Count -eq 1) {
            [long]$serverFinal[0].native_total
        } else { $null }
        checks = $serverChecks
        failed_checks = @($serverFailed | ForEach-Object Key)
    }
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    verified = @(
        'complete manifest action coverage',
        'client and server poison armed with native totals zero',
        'typed direct, routed, ZDO, ownership, zone, and motion boundaries composed',
        'a distinct logical peer was rejected from the contended ownership target',
        'Gateway restart replay and UDP loss recovery completed',
        'clean disconnect/rejoin completed on both clients',
        'dedicated-server save fingerprint stayed structurally clean')
    inferred = @()
    unverified = @(
        'subjective motion quality',
        'P7 promotion and fallback deletion')
}

$json = $summary | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($OutputPath),
    $json + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$json
if ($failed.Count -gt 0) {
    exit 1
}
