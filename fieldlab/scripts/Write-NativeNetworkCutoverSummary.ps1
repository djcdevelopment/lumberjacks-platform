#Requires -Version 5.1
<#
.SYNOPSIS
Reduce retained native-cutover JSONL evidence into one exact machine-readable gate receipt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunDirectory,

    [Parameter(Mandatory)]
    [string] $RunId,

    [string] $PoisonRunDirectory = '',

    [string] $PoisonRunId = ''
)

$ErrorActionPreference = 'Stop'
$absoluteRun = (Resolve-Path -LiteralPath $RunDirectory -ErrorAction Stop).Path

function Read-JsonLines([string] $Path, [string] $RequestedRunId) {
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RequestedRunId) { $rows += $row }
        } catch { }
    }
    return $rows
}

function Get-FunnelCounts([object] $Summary) {
    $counts = [ordered]@{}
    foreach ($property in $Summary.PSObject.Properties) {
        if ($property.Name.StartsWith('funnel_', [StringComparison]::Ordinal)) {
            $counts[$property.Name.Substring(7)] = [long]$property.Value
        }
    }
    return $counts
}

function Get-Actor([string] $Actor) {
    $ledgerPath = Join-Path $absoluteRun "$Actor\native-network-use.jsonl"
    $rows = @(Read-JsonLines $ledgerPath $RunId)
    $summary = $rows |
        Where-Object event -eq 'summary' |
        Sort-Object { [DateTimeOffset]::Parse([string]$_.timestamp_utc) } |
        Select-Object -Last 1
    if (-not $summary) { throw "No ledger summary for $Actor and run $RunId." }

    $stages = @($rows |
        Where-Object event -eq 'connection_stage' |
        ForEach-Object {
            [ordered]@{
                timestamp_utc = $_.timestamp_utc
                stage = $_.stage
                prior_stage = $_.prior_stage
                elapsed_since_prior_stage_ms = [double]$_.elapsed_since_prior_stage_ms
            }
        })
    $lifecyclePath = Join-Path $absoluteRun "$Actor\lifecycle.json"
    $lifecycle =
        if (Test-Path -LiteralPath $lifecyclePath -PathType Leaf) {
            Get-Content -LiteralPath $lifecyclePath -Raw | ConvertFrom-Json
        } else { $null }

    [ordered]@{
        actor = $Actor
        role = $summary.role
        session_id = $summary.session_id
        native_total = [long]$summary.native_total
        poison_trips = [long]$summary.poison_trips
        writer_queue_depth = [long]$summary.writer_queue_depth
        writer_dropped_rows = [long]$summary.writer_dropped_rows
        writer_faults = [long]$summary.writer_faults
        funnels = Get-FunnelCounts $summary
        connection_stages = $stages
        lifecycle_result = if ($lifecycle) { $lifecycle.result } else { $null }
        resume_count = if ($lifecycle) { [int]$lifecycle.resume_count } else { $null }
        scenario_terminal =
            if ($lifecycle -and $lifecycle.scenario_terminal) {
                $lifecycle.scenario_terminal.state
            } else { $null }
        plugin_sha256 = if ($lifecycle) { $lifecycle.plugin_sha256 } else { $null }
    }
}

$actors = @(
    Get-Actor 'omen'
    Get-Actor 'i5'
    Get-Actor 'server'
)
$composition =
    Get-Content -LiteralPath (Join-Path $absoluteRun 'composition.json') -Raw |
    ConvertFrom-Json

$checks = @(
    [ordered]@{
        name = 'composition_completed'
        passed = $composition.result -eq 'completed'
        detail = [string]$composition.result
    },
    [ordered]@{
        name = 'clients_joined_moved_disconnected_resumed'
        passed = @($actors | Where-Object {
            $_.actor -in @('omen', 'i5') -and
            $_.lifecycle_result -eq 'joined_held_and_stopped' -and
            $_.resume_count -eq 1 -and
            $_.scenario_terminal -eq 'scenario_complete'
        }).Count -eq 2
        detail = 'both client scenario terminals and one resume each'
    },
    [ordered]@{
        name = 'expected_native_nonzero'
        passed = @($actors | Where-Object { $_.native_total -gt 0 }).Count -eq 3
        detail = (($actors | ForEach-Object { "$($_.actor)=$($_.native_total)" }) -join ',')
    },
    [ordered]@{
        name = 'ledger_lossless'
        passed = @($actors | Where-Object {
            $_.writer_dropped_rows -ne 0 -or $_.writer_faults -ne 0
        }).Count -eq 0
        detail = (($actors | ForEach-Object {
            "$($_.actor):drop=$($_.writer_dropped_rows):fault=$($_.writer_faults)"
        }) -join ',')
    },
    [ordered]@{
        name = 'poison_off_for_baseline'
        passed = @($actors | Where-Object { $_.poison_trips -ne 0 }).Count -eq 0
        detail = (($actors | ForEach-Object { "$($_.actor)=$($_.poison_trips)" }) -join ',')
    }
)

$poison = $null
if ($PoisonRunDirectory -and $PoisonRunId) {
    $poisonLedger = Join-Path (
        (Resolve-Path -LiteralPath $PoisonRunDirectory -ErrorAction Stop).Path) `
        'omen\native-network-use.jsonl'
    $poisonRows = @(Read-JsonLines $poisonLedger $PoisonRunId)
    $poisonSummary = $poisonRows |
        Where-Object event -eq 'summary' |
        Sort-Object { [DateTimeOffset]::Parse([string]$_.timestamp_utc) } |
        Select-Object -Last 1
    $firstBlocked = $poisonRows |
        Where-Object { $_.event -eq 'native_use' -and $_.blocked -eq $true } |
        Sort-Object { [DateTimeOffset]::Parse([string]$_.timestamp_utc) } |
        Select-Object -First 1
    if (-not $poisonSummary -or -not $firstBlocked) {
        throw "Poison proof is incomplete for run $PoisonRunId."
    }
    $poison = [ordered]@{
        run_id = $PoisonRunId
        native_total = [long]$poisonSummary.native_total
        poison_trips = [long]$poisonSummary.poison_trips
        first_blocked_funnel = $firstBlocked.funnel
        first_blocked_message_class = $firstBlocked.message_class
        writer_dropped_rows = [long]$poisonSummary.writer_dropped_rows
        writer_faults = [long]$poisonSummary.writer_faults
    }
    $checks += [ordered]@{
        name = 'poison_enforced'
        passed = $poison.poison_trips -gt 0 -and
            $poison.poison_trips -eq $poison.native_total -and
            $poison.writer_dropped_rows -eq 0 -and $poison.writer_faults -eq 0
        detail = "first=$($poison.first_blocked_funnel) trips=$($poison.poison_trips)"
    }
}

$failed = @($checks | Where-Object { -not $_.passed })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'native_network_cutover_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    composition = $composition
    actors = $actors
    poison_proof = $poison
    checks = $checks
}
$outputPath = Join-Path $absoluteRun 'machine-summary.json'
[IO.File]::WriteAllText(
    $outputPath,
    ($receipt | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))
$receipt | ConvertTo-Json -Depth 20
if ($failed.Count -gt 0) { exit 1 }
