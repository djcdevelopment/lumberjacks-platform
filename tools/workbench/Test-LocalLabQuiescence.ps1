#Requires -Version 5.1
<#
.SYNOPSIS
Prove the local Valheim Lab is ready and has no active players.

.DESCRIPTION
Uses the same Gateway telemetry contract as the Wave 0 tools. The gate requires
a fresh dedicated-server heartbeat, ready state, zero peers, no named players,
and no local rendered Valheim process. It emits one JSON receipt and never
starts, stops, or changes a client or server.
#>
[CmdletBinding()]
param(
    [string] $GatewayUrl = 'http://127.0.0.1:4000',

    [ValidateRange(5, 300)]
    [int] $MaxSampleAgeSeconds = 30,

    [ValidateRange(1, 60)]
    [int] $HttpTimeoutSeconds = 15,

    [string] $ServerConfigRoot = '',

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$checkedUtc = [DateTimeOffset]::UtcNow
$endpoint = ($GatewayUrl.TrimEnd('/')) + '/api/v0/telemetry/valheim'
$checks = @()
$telemetry = $null
$errorDetail = ''

function New-Check([string] $Name, [bool] $Passed, [string] $Detail) {
    [pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail }
}

try {
    $telemetry = Invoke-RestMethod -Uri $endpoint -Method Get -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $HttpTimeoutSeconds
} catch {
    $errorDetail = $_.Exception.Message
}

if ($null -eq $telemetry) {
    $checks += New-Check 'gateway_telemetry' $false $errorDetail
    $heartbeat = $null
    $sampleAgeSeconds = $null
} else {
    $checks += New-Check 'gateway_telemetry' $true $endpoint
    $heartbeat = $telemetry.heartbeat
    $sampleAgeSeconds = $null
    try {
        $sample = [DateTimeOffset]::Parse([string]$heartbeat.sample_timestamp_utc)
        $sampleAgeSeconds = [Math]::Max(0, ($checkedUtc - $sample).TotalSeconds)
    } catch {
        $sampleAgeSeconds = [double]::PositiveInfinity
    }
    $fresh = (-not [bool]$telemetry.stale) -and $sampleAgeSeconds -le $MaxSampleAgeSeconds
    $checks += New-Check 'heartbeat_fresh' $fresh ("stale=$([bool]$telemetry.stale); age_seconds=$sampleAgeSeconds; max=$MaxSampleAgeSeconds")
    $checks += New-Check 'dedicated_server_role' ([string]$heartbeat.server_role -eq 'dedicated') ("role=$([string]$heartbeat.server_role)")
    $checks += New-Check 'server_ready' ([string]$heartbeat.server_state -eq 'ready') ("state=$([string]$heartbeat.server_state)")
    $checks += New-Check 'peer_count_zero' ([int]$heartbeat.peer_count -eq 0) ("peer_count=$([int]$heartbeat.peer_count)")
    $players = @($heartbeat.players)
    $checks += New-Check 'player_list_empty' ($players.Count -eq 0) ("players=$($players -join ',')")
}

$localValheim = @(Get-Process valheim -ErrorAction SilentlyContinue)
$checks += New-Check 'local_valheim_stopped' ($localValheim.Count -eq 0) ("process_ids=$(@($localValheim.Id) -join ',')")
if (-not [string]::IsNullOrWhiteSpace($ServerConfigRoot)) {
    $configRoot = [IO.Path]::GetFullPath($ServerConfigRoot)
    $backupRoot = Join-Path $configRoot 'backups'
    $temporaryBackups = @()
    if (Test-Path -LiteralPath $backupRoot -PathType Container) {
        $temporaryBackups = @(Get-ChildItem -LiteralPath $backupRoot -File -Force |
            Where-Object { $_.Extension -ne '.zip' })
    }
    $checks += New-Check 'world_backup_idle' ($temporaryBackups.Count -eq 0) ("temporary_files=$(@($temporaryBackups.Name) -join ',')")
}
$passed = @($checks | Where-Object { -not $_.passed }).Count -eq 0
$receipt = [pscustomobject]@{
    schema_version = 1
    receipt_type = 'local_lab_quiescence'
    checked_utc = $checkedUtc.ToString('o')
    verdict = $(if ($passed) { 'passed' } else { 'failed' })
    gateway_endpoint = $endpoint
    server_config_root = $ServerConfigRoot
    heartbeat = $(if ($null -eq $heartbeat) { $null } else {
        [pscustomobject]@{
            instance_id = [string]$heartbeat.instance_id
            server_role = [string]$heartbeat.server_role
            server_state = [string]$heartbeat.server_state
            peer_count = [int]$heartbeat.peer_count
            players = @($heartbeat.players)
            sample_timestamp_utc = [string]$heartbeat.sample_timestamp_utc
            sample_age_seconds = $sampleAgeSeconds
        }
    })
    checks = @($checks)
}

$json = $receipt | ConvertTo-Json -Depth 10
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $temporary = "$OutputPath.tmp"
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $OutputPath -Force
}
Write-Output $json
if (-not $passed) { exit 1 }
