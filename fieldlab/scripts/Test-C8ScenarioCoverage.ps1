#Requires -Version 5.1
<#
.SYNOPSIS
Fail closed unless a C8 manifest covers every required composition boundary.
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

function Has-Action([string] $Client, [string] $Kind) {
    @($scenario.actions | Where-Object {
        $_.client -eq $Client -and $_.kind -eq $Kind
    }).Count -gt 0
}

function Has-Value([object[]] $Values, [string] $Expected) {
    @($Values | Where-Object { [string]$_ -eq $Expected }).Count -gt 0
}

$checks = [ordered]@{
    profile_is_c8 = [string]$scenario.profile -eq 'c8'
    run_id_matches = [string]$scenario.run_id -eq $RunId
    steam_free_cold_join_declared =
        Has-Value @($scenario.c8_contract.semantics) 'steam_free_cold_join'
    co_presence_declared =
        Has-Value @($scenario.c8_contract.semantics) 'co_presence'
    direct_control_both =
        (Has-Action omen direct_control_pulse) -and
        (Has-Action i5 direct_control_pulse)
    routed_request_both =
        (Has-Action omen routed_request) -and
        (Has-Action i5 routed_request)
    routed_broadcast_both =
        (Has-Action omen routed_broadcast) -and
        (Has-Action i5 routed_broadcast)
    target_zdo_both =
        (Has-Action omen routed_target_zdo) -and
        (Has-Action i5 routed_target_zdo)
    zdo_journal_both =
        (Has-Action omen zdo_journal_drive) -and
        (Has-Action i5 zdo_journal_observe)
    motion_both_directions =
        (Has-Action omen motion_drive) -and
        (Has-Action omen motion_observe) -and
        (Has-Action i5 motion_drive) -and
        (Has-Action i5 motion_observe)
    udp_drop_range =
        (Has-Action omen motion_drive_gap) -and
        (Has-Action i5 motion_observe_gap) -and
        (Has-Value @($scenario.c8_contract.faults) 'udp_drop_range')
    pickup_both_clients =
        (Has-Action omen ownership_lease_pickup) -and
        (Has-Action i5 ownership_lease_pickup)
    two_peer_ownership_contention = @(
        $scenario.actions | Where-Object {
            $_.id -eq 'c8-ownership-contended' -and
            (($_.client -eq 'omen' -and
              $_.kind -eq 'ownership_lease_pickup') -or
             ($_.client -eq 'i5' -and
              $_.kind -eq 'ownership_contention'))
        }).Count -eq 2
    zone_exit_enter =
        (Has-Action omen zone_cross) -and
        (Has-Action i5 zone_cross) -and
        (Has-Action omen zone_membership_resume) -and
        (Has-Action i5 zone_membership_resume)
    gateway_restart_declared =
        Has-Value @($scenario.c8_contract.faults) 'gateway_restart'
    websocket_resume_declared =
        Has-Value @($scenario.c8_contract.faults) 'websocket_resume'
    clean_disconnect_rejoin =
        (Has-Action omen disconnect_resume) -and
        (Has-Action i5 disconnect_resume)
    client_server_poison_declared =
        Has-Value @($scenario.c8_contract.integrity) `
            'client_and_server_native_poison'
    save_integrity_declared =
        Has-Value @($scenario.c8_contract.integrity) `
            'save_fingerprint_before_after'
    action_ids_unique_per_client = @(
        $scenario.actions |
            Group-Object { "$($_.client):$($_.id)" } |
            Where-Object Count -gt 1).Count -eq 0
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c8_scenario_coverage'
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
if ($failed.Count -gt 0) {
    exit 1
}
