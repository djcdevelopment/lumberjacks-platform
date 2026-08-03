#Requires -Version 5.1
<#
.SYNOPSIS
Fail closed unless the C10a container manifest drives real two-client contention.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ScenarioPath,

    [Parameter(Mandatory)]
    [string] $RunId,

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$scenario = Get-Content -LiteralPath $ScenarioPath -Raw -Encoding utf8 |
    ConvertFrom-Json
$omen = @($scenario.actions | Where-Object client -eq 'omen')
$i5 = @($scenario.actions | Where-Object client -eq 'i5')

function Get-Action([object[]] $Actions, [string] $Id) {
    @($Actions | Where-Object id -eq $Id | Select-Object -First 1)[0]
}

function Get-Index([object[]] $Actions, [string] $Id) {
    for ($index = 0; $index -lt $Actions.Count; $index++) {
        if ([string]$Actions[$index].id -eq $Id) { return $index }
    }
    return -1
}

$omenTake = Get-Action $omen 'c10a-container-contended-take'
$i5Take = Get-Action $i5 'c10a-container-contended-take'
$omenHold = Get-Action $omen 'omen-c10a-container-proof-hold'
$i5Hold = Get-Action $i5 'i5-c10a-container-proof-hold'
$omenObserve = Get-Action $omen 'omen-c10a-container-reconstructed'
$i5Observe = Get-Action $i5 'i5-c10a-container-reconstructed'

$checks = [ordered]@{
    profile_is_c10a_container = [string]$scenario.profile -eq 'c10a-container'
    run_id_matches = [string]$scenario.run_id -eq $RunId
    action_ids_unique_per_client = @(
        $scenario.actions |
            Group-Object { "$($_.client):$($_.id)" } |
            Where-Object Count -gt 1).Count -eq 0
    deterministic_real_container_spawn =
        $null -ne (Get-Action $omen 'omen-c10a-container-spawn') -and
        [string](Get-Action $omen 'omen-c10a-container-spawn').kind -eq
            'container_spawn' -and
        $null -eq (Get-Action $i5 'omen-c10a-container-spawn')
    both_clients_wait_for_seeded_replica =
        [string](Get-Action $omen 'omen-c10a-container-wait').kind -eq
            'container_wait' -and
        [string](Get-Action $i5 'i5-c10a-container-wait').kind -eq
            'container_wait'
    same_transaction_id_contends_on_both_clients =
        $null -ne $omenTake -and $null -ne $i5Take -and
        [string]$omenTake.kind -eq 'container_contend_take' -and
        [string]$i5Take.kind -eq 'container_contend_take' -and
        [double]$omenTake.deadline_seconds -ge 30 -and
        [double]$i5Take.deadline_seconds -ge 30
    paired_holds_cover_contention_skew =
        $null -ne $omenHold -and $null -ne $i5Hold -and
        [string]$omenHold.kind -eq 'wait' -and
        [string]$i5Hold.kind -eq 'wait' -and
        [double]$omenHold.duration_seconds -ge 10 -and
        [double]$i5Hold.duration_seconds -ge 10 -and
        (Get-Index $omen 'omen-c10a-container-proof-hold') -gt
            (Get-Index $omen 'c10a-container-contended-take') -and
        (Get-Index $i5 'i5-c10a-container-proof-hold') -gt
            (Get-Index $i5 'c10a-container-contended-take')
    both_clients_cross_fresh_process_tail =
        (Get-Index $omen 'omen-disconnect-resume') -gt
            (Get-Index $omen 'c10a-container-contended-take') -and
        (Get-Index $i5 'i5-disconnect-resume') -gt
            (Get-Index $i5 'c10a-container-contended-take')
    both_fresh_processes_reconstruct_empty_container =
        $null -ne $omenObserve -and $null -ne $i5Observe -and
        [string]$omenObserve.kind -eq 'container_observe_empty' -and
        [string]$i5Observe.kind -eq 'container_observe_empty' -and
        (Get-Index $omen 'omen-c10a-container-reconstructed') -gt
            (Get-Index $omen 'omen-resumed') -and
        (Get-Index $i5 'i5-c10a-container-reconstructed') -gt
            (Get-Index $i5 'i5-resumed')
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_container_scenario_coverage'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    scenario_sha256 =
        (Get-FileHash -LiteralPath $ScenarioPath -Algorithm SHA256).Hash.ToLowerInvariant()
    action_count = @($scenario.actions).Count
    checks = $checks
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    failed_checks = @($failed | ForEach-Object Key)
}

$json = $receipt | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
    $directory = Split-Path -Parent $absoluteOutput
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $absoluteOutput,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}
$json
if ($failed.Count -gt 0) { exit 1 }
