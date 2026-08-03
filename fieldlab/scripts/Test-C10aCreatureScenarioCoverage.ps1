#Requires -Version 5.1
<#
.SYNOPSIS
Fail closed unless the C10a creature manifest proves autonomous AI across transfer and reclaim.

.DESCRIPTION
The selected canary is one real, tamed, unridden Lox. This verifier requires
paired owner/observer AI legs at the spawn owner, transferred owner, and
disconnect-reclaimed owner, with the accepted saddle lane used only to move
authority between those autonomous proof windows.
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

$initialDrive = Get-Action $omen 'omen-c10a-creature-ai-initial'
$initialObserve = Get-Action $i5 'i5-c10a-creature-ai-initial-observe'
$transferDrive = Get-Action $i5 'i5-c10a-creature-transfer'
$transferObserve = Get-Action $omen 'omen-c10a-creature-observe-transfer'
$transferRelease = Get-Action $omen 'omen-c10a-creature-transfer-released'
$i5Drive = Get-Action $i5 'i5-c10a-creature-ai-i5'
$i5Observe = Get-Action $omen 'omen-c10a-creature-ai-i5-observe'
$reclaim = Get-Action $omen 'omen-c10a-creature-disconnect-reclaim'
$reclaimObserve = Get-Action $i5 'i5-c10a-creature-observe-reclaim'
$reclaimDrive = Get-Action $i5 'i5-c10a-creature-ai-reclaim'
$reclaimAiObserve = Get-Action $omen 'omen-c10a-creature-ai-reclaim-observe'
$omenHold = Get-Action $omen 'omen-c10a-creature-proof-hold'
$i5Hold = Get-Action $i5 'i5-c10a-creature-proof-hold'

$checks = [ordered]@{
    profile_is_c10a_creature = [string]$scenario.profile -eq 'c10a-creature'
    run_id_matches = [string]$scenario.run_id -eq $RunId
    action_ids_unique_per_client = @(
        $scenario.actions |
            Group-Object { "$($_.client):$($_.id)" } |
            Where-Object Count -gt 1).Count -eq 0
    deterministic_real_lox_spawn_and_rendezvous =
        $null -ne (Get-Action $omen 'omen-c10a-creature-spawn') -and
        $null -ne (Get-Action $omen 'omen-c10a-creature-wait') -and
        $null -ne (Get-Action $i5 'i5-c10a-creature-wait') -and
        $null -ne (Get-Action $omen 'omen-c10a-creature-rendezvous') -and
        $null -ne (Get-Action $i5 'i5-c10a-creature-rendezvous')
    initial_owner_and_gate_are_paired =
        $null -ne $initialDrive -and
        [string]$initialDrive.kind -eq 'creature_ai_drive' -and
        $null -ne $initialObserve -and
        [string]$initialObserve.kind -eq 'creature_ai_observe' -and
        [double]$initialObserve.duration_seconds -ge
            [double]$initialDrive.duration_seconds
    accepted_saddle_transfer_is_only_between_ai_windows =
        $null -ne $transferDrive -and
        [string]$transferDrive.kind -eq 'saddle_drive' -and
        $null -ne $transferObserve -and
        [string]$transferObserve.kind -eq 'saddle_observe' -and
        $null -ne $transferRelease -and
        [string]$transferRelease.kind -eq 'saddle_wait_released' -and
        (Get-Index $omen 'omen-c10a-creature-observe-transfer') -gt
            (Get-Index $omen 'omen-c10a-creature-ai-initial') -and
        (Get-Index $i5 'i5-c10a-creature-transfer') -gt
            (Get-Index $i5 'i5-c10a-creature-ai-initial-observe')
    transferred_i5_ai_and_omen_gate_are_paired =
        $null -ne $i5Drive -and
        [string]$i5Drive.kind -eq 'creature_ai_drive' -and
        $null -ne $i5Observe -and
        [string]$i5Observe.kind -eq 'creature_ai_observe' -and
        [double]$i5Observe.duration_seconds -ge [double]$i5Drive.duration_seconds -and
        (Get-Index $omen 'omen-c10a-creature-ai-i5-observe') -gt
            (Get-Index $omen 'omen-c10a-creature-transfer-released') -and
        (Get-Index $i5 'i5-c10a-creature-ai-i5') -gt
            (Get-Index $i5 'i5-c10a-creature-transfer')
    forced_disconnect_reclaim_is_paired =
        $null -ne $reclaim -and
        [string]$reclaim.kind -eq 'saddle_disconnect_reclaim' -and
        [double]$reclaim.duration_seconds -ge 5 -and
        [double]$reclaim.deadline_seconds -ge 45 -and
        $null -ne $reclaimObserve -and
        [string]$reclaimObserve.kind -eq 'saddle_observe_reclaim' -and
        [double]$reclaimObserve.deadline_seconds -ge 45
    reclaimed_i5_ai_and_omen_gate_are_paired =
        $null -ne $reclaimDrive -and
        [string]$reclaimDrive.kind -eq 'creature_ai_drive' -and
        $null -ne $reclaimAiObserve -and
        [string]$reclaimAiObserve.kind -eq 'creature_ai_observe' -and
        [double]$reclaimAiObserve.duration_seconds -ge
            [double]$reclaimDrive.duration_seconds -and
        (Get-Index $omen 'omen-c10a-creature-ai-reclaim-observe') -eq
            ((Get-Index $omen 'omen-c10a-creature-reclaim-settle') + 1) -and
        (Get-Index $i5 'i5-c10a-creature-ai-reclaim') -eq
            ((Get-Index $i5 'i5-c10a-creature-reclaim-settle') + 1)
    exactly_three_autonomous_owner_observer_pairs =
        @($scenario.actions | Where-Object kind -eq 'creature_ai_drive').Count -eq 3 -and
        @($scenario.actions | Where-Object kind -eq 'creature_ai_observe').Count -eq 3
    proof_holds_cover_client_skew =
        $null -ne $omenHold -and $null -ne $i5Hold -and
        [string]$omenHold.kind -eq 'wait' -and [string]$i5Hold.kind -eq 'wait' -and
        [double]$omenHold.duration_seconds -ge 10 -and
        [double]$i5Hold.duration_seconds -ge 10
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_creature_scenario_coverage'
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
