<#
.SYNOPSIS
Wait for two real P7 peers, then run the Wave 0 live gate.

.DESCRIPTION
This is the low-touch operator wrapper for the remaining live proof. Start it
before or while the two clients are joining. It polls the P7 Valheim telemetry
until the heartbeat is fresh/ready and peer_count is at least -MinPeers, then
delegates to Start-Wave0LiveGate.ps1 with the requested apply/observe role.

If the peer window never appears, the script writes a WAIT receipt and exits 0.
That makes it safe to run before Derek returns without turning "not joined yet"
into a false failure.
#>
[CmdletBinding()]
param(
    [ValidateSet('omen','i5','preserve')]
    [string]$DesiredApplyClient = 'omen',

    [ValidateSet('straight_north','straight_east','stutter_north','circle')]
    [string]$Pattern = 'straight_north',

    [ValidateRange(1,60)]
    [int]$MotionDurationSeconds = 10,

    [ValidateRange(10,300)]
    [int]$CaptureDurationSeconds = 30,

    [ValidateRange(0,30)]
    [int]$WarmupSeconds = 5,

    [ValidateRange(1,60)]
    [int]$IntervalSeconds = 1,

    [ValidateRange(2,10)]
    [int]$MinPeers = 2,

    [ValidateRange(0,3600)]
    [int]$WaitSeconds = 600,

    [ValidateRange(1,30)]
    [int]$PollSeconds = 5,

    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',

    [string]$Label = 'wave0-live-gate',

    [string]$OutputJson,

    [string]$ObservationMarkdown,

    [string]$BundleDirectory,

    [string]$MockValheimTelemetryJson,

    [string]$MockRolePreflightJson,

    [switch]$StopAfterRolePreflight,

    [switch]$SkipSynthetic,

    [switch]$SkipReadiness,

    [switch]$SkipRolePreflight
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$httpTimeoutSeconds = 15

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'wave0-live-gate' }
$runId = "$stamp-$safeLabel-autowait"

if (-not $OutputJson) {
    $OutputJson = "captures/$runId/result.json"
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputJson)) {
    [IO.Path]::GetFullPath($OutputJson)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputJson))
}
$outputDirectory = Split-Path -Parent $outputPath
if ($outputDirectory) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Get-ValheimTelemetry {
    if ($MockValheimTelemetryJson) {
        $mockPath = Resolve-UnderRepo $MockValheimTelemetryJson
        if (-not (Test-Path -LiteralPath $mockPath)) { throw "mock Valheim telemetry not found: $mockPath" }
        return Get-Content -LiteralPath $mockPath -Raw | ConvertFrom-Json
    }
    return Invoke-RestMethod -Uri (($GatewayUrl.TrimEnd('/')) + '/api/v0/telemetry/valheim') -Method Get -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $httpTimeoutSeconds
}

function Get-PeerSnapshot {
    param($Telemetry)

    $heartbeat = $Telemetry.heartbeat
    [ordered]@{
        sampled_utc = [DateTimeOffset]::UtcNow.ToString('o')
        stale = [bool]$Telemetry.stale
        server_state = [string]$heartbeat.server_state
        peer_count = [int]$heartbeat.peer_count
        players = @($heartbeat.players)
    }
}

function Write-WaitReceipt {
    param(
        [string]$Verdict,
        [string]$NextAction,
        [object[]]$Samples
    )

    $receipt = [ordered]@{
        schema_version = 1
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        run_id = $runId
        verdict = $Verdict
        next_action = $NextAction
        desired_apply_client = $DesiredApplyClient
        min_peers = $MinPeers
        wait_seconds = $WaitSeconds
        poll_seconds = $PollSeconds
        http_timeout_seconds = $httpTimeoutSeconds
        gateway_url = $GatewayUrl
        samples = $Samples
        delegated_live_gate = $null
    }
    [IO.File]::WriteAllText($outputPath, (($receipt | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Host ("Wave 0 auto-wait live gate: {0}" -f $Verdict)
    Write-Host ("Receipt JSON: {0}" -f $outputPath)
    if ($NextAction) { Write-Host ("Next: {0}" -f $NextAction) }
}

$samples = @()
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($WaitSeconds)
do {
    try {
        $sample = Get-PeerSnapshot (Get-ValheimTelemetry)
        $samples += [pscustomobject]$sample
        Write-Host ("P7 peers={0} state={1} stale={2}" -f $sample.peer_count, $sample.server_state, $sample.stale)
        if (-not $sample.stale -and $sample.server_state -eq 'ready' -and $sample.peer_count -ge $MinPeers) {
            $gateArgs = @(
                '-DesiredApplyClient', $DesiredApplyClient,
                '-Pattern', $Pattern,
                '-MotionDurationSeconds', [string]$MotionDurationSeconds,
                '-CaptureDurationSeconds', [string]$CaptureDurationSeconds,
                '-WarmupSeconds', [string]$WarmupSeconds,
                '-IntervalSeconds', [string]$IntervalSeconds,
                '-GatewayUrl', $GatewayUrl,
                '-Label', $Label,
                '-OutputJson', $outputPath
            )
            if ($ObservationMarkdown) { $gateArgs += @('-ObservationMarkdown', $ObservationMarkdown) }
            if ($BundleDirectory) { $gateArgs += @('-BundleDirectory', $BundleDirectory) }
            if ($MockValheimTelemetryJson) { $gateArgs += @('-MockValheimTelemetryJson', $MockValheimTelemetryJson) }
            if ($MockRolePreflightJson) { $gateArgs += @('-MockRolePreflightJson', $MockRolePreflightJson) }
            if ($StopAfterRolePreflight) { $gateArgs += '-StopAfterRolePreflight' }
            if ($SkipSynthetic) { $gateArgs += '-SkipSynthetic' }
            if ($SkipReadiness) { $gateArgs += '-SkipReadiness' }
            if ($SkipRolePreflight) { $gateArgs += '-SkipRolePreflight' }

            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Start-Wave0LiveGate.ps1') @gateArgs
            exit $LASTEXITCODE
        }
    } catch {
        $samples += [pscustomobject]@{
            sampled_utc = [DateTimeOffset]::UtcNow.ToString('o')
            error = $_.Exception.Message
        }
        Write-Host ("P7 peer poll failed: {0}" -f $_.Exception.Message)
    }

    if ($WaitSeconds -eq 0) { break }
    if ([DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Seconds $PollSeconds }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

Write-WaitReceipt `
    -Verdict 'wait_for_two_real_clients_timeout' `
    -NextAction 'Peer window did not reach the required count before timeout. Leave both clients joined, then rerun this command.' `
    -Samples $samples
exit 0
