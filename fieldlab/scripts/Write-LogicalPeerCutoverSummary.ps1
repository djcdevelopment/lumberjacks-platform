#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one C7 Steam-free two-client run into an exact logical-peer cutover receipt.

.DESCRIPTION
This reducer refuses a merely healthy-looking scene. Both physical clients must have entered and
resumed through the authenticated logical-peer adapter without a native connect target or any
client native-network funnel use. The dedicated server must have constructed both logical client
peers and applied their typed CharacterID controls without accepting native peer/handshake/world
traffic for the run.
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
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    return @($rows)
}

function Get-LedgerAggregate([object[]] $Rows, [string] $Actor) {
    $summaries = @(
        $Rows |
        Where-Object event -eq 'summary' |
        Group-Object session_id |
        ForEach-Object {
            $_.Group |
            Sort-Object { [DateTimeOffset]::Parse([string]$_.timestamp_utc) } |
            Select-Object -Last 1
        }
    )
    if ($summaries.Count -eq 0) {
        throw "No native-network summary exists for $Actor and run $RunId."
    }
    $funnels = [ordered]@{}
    foreach ($summary in $summaries) {
        foreach ($property in $summary.PSObject.Properties) {
            if (-not $property.Name.StartsWith(
                    'funnel_', [StringComparison]::Ordinal)) {
                continue
            }
            $name = $property.Name.Substring(7)
            if (-not $funnels.Contains($name)) { $funnels[$name] = 0L }
            $funnels[$name] = [long]$funnels[$name] + [long]$property.Value
        }
    }
    return [ordered]@{
        session_count = $summaries.Count
        poison_enabled_all =
            @($summaries | Where-Object { $_.poison_enabled -ne $true }).Count -eq 0
        native_total = [long](($summaries |
            Measure-Object -Property native_total -Sum).Sum)
        poison_trips = [long](($summaries |
            Measure-Object -Property poison_trips -Sum).Sum)
        writer_dropped_rows = [long](($summaries |
            Measure-Object -Property writer_dropped_rows -Sum).Sum)
        writer_faults = [long](($summaries |
            Measure-Object -Property writer_faults -Sum).Sum)
        native_use_rows = @($Rows | Where-Object event -eq 'native_use').Count
        funnels = $funnels
    }
}

function Count-State([object[]] $Rows, [string] $State) {
    return @($Rows | Where-Object { $_.state -eq $State }).Count
}

function Detail-Value([string] $Detail, [string] $Name) {
    $match = [regex]::Match(
        [string]$Detail,
        '(?:^|\s)' + [regex]::Escape($Name) + '=(?<value>[^\s]+)')
    if ($match.Success) { return $match.Groups['value'].Value }
    return ''
}

function Get-Client([string] $Client) {
    $directory = Join-Path $absoluteRun $Client
    $logical = @(Read-JsonLines (Join-Path $directory 'logical-peer-cutover.jsonl'))
    $autotest = @(Read-JsonLines (Join-Path $directory 'native-autotest-receipts.jsonl'))
    $native = @(Read-JsonLines (Join-Path $directory 'native-network-use.jsonl'))
    $ledger = Get-LedgerAggregate $native $Client
    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json
    $constructed = @($logical | Where-Object {
        $_.state -eq 'logical_peer_constructed' -and
        $_.detail -match '(?:^|\s)role=server(?:\s|$)' -and
        $_.detail -match '(?:^|\s)native_socket=false(?:\s|$)'
    })
    $failures = @($logical | Where-Object {
        $_.state -in @(
            'cold_join_failed',
            'logical_peer_frame_rejected',
            'logical_control_rejected')
    })

    return [ordered]@{
        client = $Client
        lifecycle_result = [string]$lifecycle.result
        steam_free_requested = [bool]$lifecycle.steam_free_cold_join_requested
        resume_count = [int]$lifecycle.resume_count
        scenario_terminal = [string]$lifecycle.scenario_terminal.state
        lifecycle_error = [string]$lifecycle.error
        native_total = [long]$ledger.native_total
        native_ledger_sessions = [int]$ledger.session_count
        native_poison_armed = [bool]$ledger.poison_enabled_all
        native_use_rows = [int]$ledger.native_use_rows
        poison_trips = [long]$ledger.poison_trips
        writer_dropped_rows = [long]$ledger.writer_dropped_rows
        writer_faults = [long]$ledger.writer_faults
        steam_free_scene_requested =
            Count-State $autotest 'steam_free_scene_requested'
        joined_receipts = Count-State $autotest 'joined'
        native_client_connect_suppressed =
            Count-State $logical 'native_client_connect_suppressed'
        logical_server_announced =
            Count-State $logical 'logical_server_announced'
        logical_peer_constructed = $constructed.Count
        logical_peer_ready =
            Count-State $logical 'client_logical_peer_ready'
        character_id_queued = Count-State $logical 'character_id_queued'
        logical_failure_count = $failures.Count
        logical_failure_states = @($failures | ForEach-Object { $_.state })
        checks = [ordered]@{
            lifecycle_complete =
                $lifecycle.result -eq 'joined_held_and_stopped' -and
                [bool]$lifecycle.steam_free_cold_join_requested -and
                [int]$lifecycle.resume_count -eq 1 -and
                $lifecycle.scenario_terminal.state -eq 'scenario_complete' -and
                [string]::IsNullOrWhiteSpace([string]$lifecycle.error)
            scene_requested_without_native_target =
                (Count-State $autotest 'steam_free_scene_requested') -ge 1
            joined_twice =
                (Count-State $autotest 'joined') -ge 2
            client_connect_suppressed =
                (Count-State $logical 'native_client_connect_suppressed') -ge 2
            logical_server_constructed =
                $constructed.Count -ge 2 -and
                (Count-State $logical 'client_logical_peer_ready') -ge 2
            typed_character_id_queued =
                (Count-State $logical 'character_id_queued') -ge 2
            no_logical_terminal_failure = $failures.Count -eq 0
            native_network_zero =
                [long]$ledger.native_total -eq 0 -and
                [long]$ledger.native_use_rows -eq 0 -and
                [long]$ledger.poison_trips -eq 0
            native_poison_armed = [bool]$ledger.poison_enabled_all
            ledger_lossless =
                [long]$ledger.writer_dropped_rows -eq 0 -and
                [long]$ledger.writer_faults -eq 0
        }
    }
}

function Get-Server {
    $directory = Join-Path $absoluteRun 'server'
    $logical = @(Read-JsonLines (Join-Path $directory 'logical-peer-cutover.jsonl'))
    $native = @(Read-JsonLines (Join-Path $directory 'native-network-use.jsonl'))
    $ledger = Get-LedgerAggregate $native 'server'
    $constructed = @($logical | Where-Object {
        $_.state -eq 'logical_peer_constructed' -and
        $_.detail -match '(?:^|\s)role=client(?:\s|$)' -and
        $_.detail -match '(?:^|\s)native_socket=false(?:\s|$)'
    })
    $logicalIds = @(
        $constructed |
        ForEach-Object { Detail-Value $_.detail 'logical_peer_id' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    )
    $blockedFunnelNames = @(
        'native_peer_connection',
        'server_handshake',
        'client_handshake',
        'peer_info_send',
        'peer_info_receive',
        'zdo_data_receive',
        'routed_rpc_receive'
    )
    $selectedNative = [ordered]@{}
    foreach ($name in $blockedFunnelNames) {
        $selectedNative[$name] = if ($ledger.funnels.Contains($name)) {
            [long]$ledger.funnels[$name]
        } else { 0L }
    }
    $failures = @($logical | Where-Object {
        $_.state -in @(
            'logical_peer_frame_rejected',
            'logical_control_rejected')
    })

    return [ordered]@{
        actor = 'server'
        logical_client_ids = $logicalIds
        logical_client_constructed = $constructed.Count
        character_id_applied = Count-State $logical 'character_id_applied'
        selected_native_funnels = $selectedNative
        native_total_including_idle_host_poll = [long]$ledger.native_total
        writer_dropped_rows = [long]$ledger.writer_dropped_rows
        writer_faults = [long]$ledger.writer_faults
        logical_failure_count = $failures.Count
        checks = [ordered]@{
            two_distinct_logical_clients = $logicalIds.Count -eq 2
            client_peers_reconstructed_after_resume = $constructed.Count -ge 4
            typed_character_ids_applied =
                (Count-State $logical 'character_id_applied') -ge 4
            selected_native_ingress_zero =
                @($selectedNative.Values | Where-Object { $_ -ne 0 }).Count -eq 0
            no_logical_terminal_failure = $failures.Count -eq 0
            ledger_lossless =
                [long]$ledger.writer_dropped_rows -eq 0 -and
                [long]$ledger.writer_faults -eq 0
        }
    }
}

$composition =
    Get-Content -LiteralPath (Join-Path $absoluteRun 'composition.json') -Raw |
    ConvertFrom-Json
$clients = @(
    Get-Client 'omen'
    Get-Client 'i5'
)
$server = Get-Server
$checks = [ordered]@{
    composition_completed_steam_free =
        $composition.result -eq 'completed' -and
        [bool]$composition.steam_free_cold_join
    both_clients_passed =
        @($clients | ForEach-Object {
            @($_.checks.Values | Where-Object { -not $_ }).Count
        } | Where-Object { $_ -ne 0 }).Count -eq 0
    server_passed =
        @($server.checks.Values | Where-Object { -not $_ }).Count -eq 0
}
$failed = @($checks.Values | Where-Object { -not $_ })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'logical_peer_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    composition = $composition
    clients = $clients
    server = $server
    checks = $checks
}
$outputPath = Join-Path $absoluteRun 'c7-logical-peer-summary.json'
[IO.File]::WriteAllText(
    $outputPath,
    ($receipt | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))
$receipt | ConvertTo-Json -Depth 20
if ($failed.Count -gt 0) { exit 1 }
