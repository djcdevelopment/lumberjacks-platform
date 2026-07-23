<#
.SYNOPSIS
Capture a short transport-truth window from the local Companion/Gateway APIs.

.DESCRIPTION
Polls the same live surfaces used by the Companion "Moving parts" panel and writes an
append-only JSONL sample stream plus a compact summary JSON. This is intentionally
operator-lightweight: start it before a two-client movement test, move in-game, then keep the
generated run directory as evidence.

The default URL targets the local Companion at http://127.0.0.1:8080 so the same script works
against OMEN or i5 without exposing private Gateway endpoints.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:8080',

    [ValidateRange(1, 3600)]
    [int]$DurationSeconds = 60,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 5,

    [string]$Label = 'transport-truth',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\runs\transport-truth')
)

$ErrorActionPreference = 'Stop'

function New-SafeName([string]$Value) {
    $safe = $Value -replace '[^A-Za-z0-9._-]', '-'
    $safe.Trim('-')
}

function Invoke-JsonEndpoint([string]$Path) {
    $uri = $BaseUrl.TrimEnd('/') + $Path
    try {
        $body = Invoke-RestMethod -Uri $uri -TimeoutSec 10
        [pscustomobject]@{
            ok = $true
            path = $Path
            status = 200
            body = $body
            error = $null
        }
    }
    catch {
        [pscustomobject]@{
            ok = $false
            path = $Path
            status = $null
            body = $null
            error = $_.Exception.Message
        }
    }
}

function Get-Value($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    if ($null -eq $property.Value) { return $Default }
    $property.Value
}

function Get-CurrentRead($Valheim, $Motion, $Deployment) {
    if (-not (Get-Value $Deployment 'ok' $false)) {
        return [pscustomobject]@{
            level = 'bad'
            text = 'Gateway telemetry unavailable; live network evidence is not trustworthy.'
        }
    }

    if (-not (Get-Value $Motion 'ok' $false)) {
        return [pscustomobject]@{
            level = 'bad'
            text = 'Motion telemetry unavailable; use the in-game strip and trace before interpreting movement.'
        }
    }

    $motionBody = Get-Value $Motion 'body'
    $received = [int](Get-Value $motionBody 'received' 0)
    if ($received -gt 0) {
        return [pscustomobject]@{
            level = 'ok'
            text = 'Lumberjacks motion frames are arriving.'
        }
    }

    $valheimBody = Get-Value $Valheim 'body'
    $peers = [int](Get-Value $valheimBody 'peers' 0)
    if ($peers -gt 0) {
        return [pscustomobject]@{
            level = 'wait'
            text = "Valheim has $peers peer(s), but Lumberjacks motion counters are zero. Visible player movement is native Valheim for this run."
        }
    }

    return [pscustomobject]@{
        level = 'wait'
        text = 'P7 is up with no active peers. Join two clients, then watch Valheim peers and Motion counters change together.'
    }
}

function Get-Verdict([int]$BadSamples, [int]$MaxPeers, $FirstMotionReceived, $LastMotionReceived) {
    if ($BadSamples -gt 0) { return 'incomplete_telemetry' }
    if ($null -ne $FirstMotionReceived -and $null -ne $LastMotionReceived -and $LastMotionReceived -gt $FirstMotionReceived) {
        return 'lumberjacks_motion_observed'
    }
    if ($MaxPeers -gt 0) { return 'native_motion_only' }
    'no_peer_window'
}

$startedUtc = [DateTimeOffset]::UtcNow
$runId = ('{0}-{1}' -f $startedUtc.ToString('yyyyMMdd-HHmmss'), (New-SafeName $Label))
$runDirectory = Join-Path $OutputDirectory $runId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$samplesPath = Join-Path $runDirectory 'samples.jsonl'
$summaryPath = Join-Path $runDirectory 'summary.json'
$endAt = $startedUtc.AddSeconds($DurationSeconds)
$sampleIndex = 0
$firstMotionReceived = $null
$lastMotionReceived = $null
$maxPeers = 0
$badSamples = 0
$finalCurrentRead = $null

Write-Host "capture -> $samplesPath"
Write-Host "base_url=$BaseUrl duration=${DurationSeconds}s interval=${IntervalSeconds}s"

while ($true) {
    $timestamp = [DateTimeOffset]::UtcNow
    $deployment = Invoke-JsonEndpoint '/api/v0/telemetry/deployment'
    $valheim = Invoke-JsonEndpoint '/api/v0/telemetry/valheim'
    $cutover = Invoke-JsonEndpoint '/api/v0/telemetry/cutover'
    $motion = Invoke-JsonEndpoint '/live/valheim-motion'
    $currentRead = Get-CurrentRead $valheim $motion $deployment
    $finalCurrentRead = $currentRead

    if (-not $deployment.ok -or -not $valheim.ok -or -not $cutover.ok -or -not $motion.ok) {
        $badSamples++
    }

    if ($motion.ok) {
        $received = [int](Get-Value $motion.body 'received' 0)
        if ($null -eq $firstMotionReceived) { $firstMotionReceived = $received }
        $lastMotionReceived = $received
    }

    if ($valheim.ok) {
        $peers = [int](Get-Value $valheim.body 'peers' 0)
        if ($peers -gt $maxPeers) { $maxPeers = $peers }
    }

    $row = [pscustomobject]@{
        schema_version = 1
        event_type = 'transport_truth.sample'
        timestamp_utc = $timestamp.ToString('o')
        run_id = $runId
        sample_index = $sampleIndex
        base_url = $BaseUrl
        current_read = $currentRead
        endpoints = [pscustomobject]@{
            deployment = $deployment
            valheim = $valheim
            cutover = $cutover
            motion = $motion
        }
    }

    $row | ConvertTo-Json -Depth 20 -Compress | Add-Content -LiteralPath $samplesPath -Encoding utf8
    Write-Host ("[{0}] {1}" -f $sampleIndex, $currentRead.text)
    $sampleIndex++

    $remainingSeconds = ($endAt - [DateTimeOffset]::UtcNow).TotalSeconds
    if ($remainingSeconds -le 0) { break }
    Start-Sleep -Seconds ([int][math]::Min($IntervalSeconds, [math]::Ceiling($remainingSeconds)))
}

$finishedUtc = [DateTimeOffset]::UtcNow
$summary = [pscustomobject]@{
    schema_version = 1
    run_id = $runId
    label = $Label
    base_url = $BaseUrl
    started_utc = $startedUtc.ToString('o')
    finished_utc = $finishedUtc.ToString('o')
    duration_seconds = [math]::Round(($finishedUtc - $startedUtc).TotalSeconds, 3)
    interval_seconds = $IntervalSeconds
    sample_count = $sampleIndex
    bad_sample_count = $badSamples
    max_peers = $maxPeers
    first_motion_received = $firstMotionReceived
    last_motion_received = $lastMotionReceived
    motion_received_delta = if ($null -ne $firstMotionReceived -and $null -ne $lastMotionReceived) { $lastMotionReceived - $firstMotionReceived } else { $null }
    verdict = Get-Verdict -BadSamples $badSamples -MaxPeers $maxPeers -FirstMotionReceived $firstMotionReceived -LastMotionReceived $lastMotionReceived
    final_current_read = $finalCurrentRead
    samples_path = (Resolve-Path -LiteralPath $samplesPath).Path
}

$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | ConvertTo-Json -Depth 10
