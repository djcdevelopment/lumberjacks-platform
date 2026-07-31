#Requires -Version 5.1
<#
.SYNOPSIS
Reduce C7's invalid-enrollment, unavailable-Gateway, wrong-release, and
wrong-descriptor physical-client cells into one fail-closed receipt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseRunDirectory,

    [Parameter(Mandatory)]
    [string] $BaseRunId,

    [Parameter(Mandatory)]
    [string] $EvidenceRoot
)

$ErrorActionPreference = 'Stop'
$base = [IO.Path]::GetFullPath($BaseRunDirectory)
$root = [IO.Path]::GetFullPath($EvidenceRoot)

function Read-Rows([string] $Path, [string] $RunId) {
    $rows = @()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    foreach ($line in Get-Content -LiteralPath $Path) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    return @($rows)
}

function Get-Cell(
    [string] $Name,
    [string] $ExpectedResult,
    [string] $ExpectedFailureMode,
    [string] $ExpectedDescriptorReason) {
    $runId = "$BaseRunId-$Name"
    $directory = Join-Path (Join-Path $root $runId) 'omen'
    $lifecycle =
        Get-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Raw |
        ConvertFrom-Json
    $autotest =
        @(Read-Rows (Join-Path $directory 'native-autotest-receipts.jsonl') $runId)
    $session =
        @(Read-Rows (Join-Path $directory 'lumberjacks-game-session.jsonl') $runId)
    $world =
        @(Read-Rows (Join-Path $directory 'world-zone-cutover.jsonl') $runId)
    $native =
        @(Read-Rows (Join-Path $directory 'native-network-use.jsonl') $runId)
    $summaries = @($native | Where-Object event -eq 'summary')
    $nativeTotal = [long](($summaries |
        Measure-Object -Property native_total -Sum).Sum)
    $nativeUseRows = @($native | Where-Object event -eq 'native_use').Count
    $poisonArmed =
        $summaries.Count -gt 0 -and
        @($summaries | Where-Object { $_.poison_enabled -ne $true }).Count -eq 0
    $joined = @($autotest | Where-Object state -eq 'joined').Count
    $steamFree = @($autotest | Where-Object {
        $_.state -eq 'steam_free_scene_requested' -and
        $_.detail -match '(?:^|\s)native_connect=false(?:\s|$)' -and
        $_.detail -match '(?:^|\s)native_handshake=false(?:\s|$)'
    }).Count
    $connectionFailure = @($session | Where-Object {
        $_.state -eq 'connection_error' -and
        $_.detail -match (
            '(?:^|\s)fault_mode=' +
            [regex]::Escape($ExpectedFailureMode) + '(?:\s|$)')
    }).Count
    $descriptorFailure = @($world | Where-Object {
        $_.state -eq 'descriptor_rejected_before_scene' -and
        $_.detail -match (
            '(?:^|\s)reason=' +
            [regex]::Escape($ExpectedDescriptorReason) + '(?:\s|$)')
    }).Count
    $expectedFailure = if ($ExpectedFailureMode) {
        $connectionFailure -gt 0
    } else {
        $descriptorFailure -gt 0
    }
    $checks = [ordered]@{
        lifecycle_expected =
            $lifecycle.result -eq $ExpectedResult -and
            [string]::IsNullOrWhiteSpace([string]$lifecycle.error)
        steam_free_no_native_target = $steamFree -gt 0
        expected_failure_observed = $expectedFailure
        never_joined = $joined -eq 0
        native_poison_armed = $poisonArmed
        native_network_zero =
            $nativeTotal -eq 0 -and $nativeUseRows -eq 0
        ledger_lossless =
            @($summaries | Where-Object {
                [long]$_.writer_dropped_rows -ne 0 -or
                [long]$_.writer_faults -ne 0
            }).Count -eq 0
    }
    return [ordered]@{
        name = $Name
        run_id = $runId
        lifecycle_result = [string]$lifecycle.result
        native_total = $nativeTotal
        native_use_rows = $nativeUseRows
        poison_armed = $poisonArmed
        joined_receipts = $joined
        steam_free_scene_receipts = $steamFree
        connection_failure_receipts = $connectionFailure
        descriptor_failure_receipts = $descriptorFailure
        checks = $checks
        passed =
            @($checks.Values | Where-Object { -not $_ }).Count -eq 0
    }
}

$cells = @()
$cells += Get-Cell `
    'invalid-enrollment' `
    'cold_join_failed_closed_and_stopped' `
    'invalid_enrollment' `
    ''
$cells += Get-Cell `
    'gateway-unavailable' `
    'cold_join_failed_closed_and_stopped' `
    'gateway_unavailable' `
    ''
$cells += Get-Cell `
    'wrong-release' `
    'world_descriptor_rejected_before_scene_and_stopped' `
    '' `
    'descriptor_release_mismatch'
$cells += Get-Cell `
    'wrong-descriptor' `
    'world_descriptor_rejected_before_scene_and_stopped' `
    '' `
    'descriptor_protocol_mismatch'
$passed = @($cells | Where-Object { -not $_.passed }).Count -eq 0
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c7_cold_join_negative_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $BaseRunId
    result = if ($passed) { 'passed' } else { 'failed' }
    cells = $cells
    checks = [ordered]@{
        all_four_cells_passed = $passed
        all_cells_native_zero =
            @($cells | Where-Object { $_.native_total -ne 0 }).Count -eq 0
        all_cells_poison_armed =
            @($cells | Where-Object { -not $_.poison_armed }).Count -eq 0
        no_cell_joined =
            @($cells | Where-Object { $_.joined_receipts -ne 0 }).Count -eq 0
    }
}
$outputPath = Join-Path $base 'c7-cold-join-negative-summary.json'
[IO.File]::WriteAllText(
    $outputPath,
    ($receipt | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$receipt | ConvertTo-Json -Depth 20
if (-not $passed) { exit 1 }
