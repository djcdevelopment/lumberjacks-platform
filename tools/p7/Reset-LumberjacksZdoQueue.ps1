#Requires -Version 5.1
<#
.SYNOPSIS
Archive the Lumberjacks ZDO queue stores (redirect WAL + zone-bank journal) so the next
armed window starts from a clean bank.

.DESCRIPTION
The 2026-08-05 outage postmortem (SESSION-RETRO-2026-08-05, lesson L-2026-08-05-6) showed
two failure modes this script exists to prevent:

  1. Wiping the CONTAINER path instead of the HOST path silently no-ops (the container
     was down; the bind mount is the truth). This script only ever touches host paths
     (P7) or the named volume (local), never a container filesystem path.
  2. Deleting instead of archiving destroys forensic evidence. This script always
     renames to `<name>.bak-<utc-stamp>` beside the original; cleanup of old archives
     is a deliberate, separate act.

It refuses to run while the gateway container is up: an online gateway holds the WAL
open and re-persists in-memory state over the reset.

Targets:
  -Target p7     (default) the GCP VM over ssh alias `comfy-p7`; host dir
                 /mnt/comfy-p7/lumberjacks/zdo-queue/{redirect.wal,journal.jsonl}.
  -Target local  the OMEN rehearsal gateway (compose project `lumberjacks-local`);
                 the journal lives in the `gatewaydata` named volume (the local lane
                 does not persist the redirect WAL). Used by the Phase-2 AM4 rehearsal
                 so the P7 window runs an already-rehearsed script.

Actions:
  -Action reset   archive the store files (gateway must be stopped).
  -Action verify  after the operator restarts the gateway, assert the telemetry
                  surface reports an empty queue (wal_bytes 0 and pending 0).

.EXAMPLE
tools\p7\Reset-LumberjacksZdoQueue.ps1                       # P7 reset (VM up, gateway stopped)
tools\p7\Reset-LumberjacksZdoQueue.ps1 -Action verify        # after systemd start
tools\p7\Reset-LumberjacksZdoQueue.ps1 -Target local         # rehearse on OMEN
#>
[CmdletBinding()]
param(
    [ValidateSet('reset', 'verify')]
    [string] $Action = 'reset',

    [ValidateSet('p7', 'local')]
    [string] $Target = 'p7',

    [string] $SshTarget = 'comfy-p7',

    [string] $GatewayContainer = '',

    [string] $VerifyUrl = ''
)

$ErrorActionPreference = 'Stop'
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')

if ([string]::IsNullOrWhiteSpace($GatewayContainer)) {
    $GatewayContainer = if ($Target -eq 'p7') {
        'comfy-lumberjacks-p7-gateway-1'
    } else {
        'lumberjacks-local-gateway-1'
    }
}
if ([string]::IsNullOrWhiteSpace($VerifyUrl)) {
    $VerifyUrl = if ($Target -eq 'p7') {
        'https://comfy-p7.duckdns.org/api/v0/telemetry/cutover'
    } else {
        'http://127.0.0.1:4000/api/v0/telemetry/cutover'
    }
}

function Invoke-RemoteShell([string] $Command) {
    $output = @(& ssh -o BatchMode=yes -o ConnectTimeout=10 $SshTarget $Command)
    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed (exit $LASTEXITCODE): $Command"
    }
    return $output
}

function Assert-GatewayStopped() {
    if ($Target -eq 'p7') {
        $state = Invoke-RemoteShell(
            "sudo docker inspect '$GatewayContainer' --format '{{.State.Status}}' 2>/dev/null || echo absent")
    } else {
        $state = @(& docker inspect $GatewayContainer --format '{{.State.Status}}' 2>$null)
        if ($LASTEXITCODE -ne 0 -or -not $state) { $state = @('absent') }
    }
    $status = ($state -join '').Trim()
    if ($status -eq 'running') {
        throw "Gateway container '$GatewayContainer' is running; stop it first (an online gateway re-persists over the reset)."
    }
    return $status
}

function Invoke-Reset() {
    $gatewayState = Assert-GatewayStopped
    $archived = @()
    if ($Target -eq 'p7') {
        $dir = '/mnt/comfy-p7/lumberjacks/zdo-queue'
        # Host path only — L-2026-08-05-6: the container path no-ops when the stack is down.
        foreach ($name in @('redirect.wal', 'journal.jsonl')) {
            $listing = Invoke-RemoteShell(
                "if sudo test -f '$dir/$name'; then sudo mv '$dir/$name' '$dir/$name.bak-$stamp' && sudo stat -c '%n|%s' '$dir/$name.bak-$stamp'; else echo '$dir/${name}|absent'; fi")
            $archived += ($listing -join '')
        }
    } else {
        # The journal sits in the compose named volume; archive via a throwaway
        # container so no path outside the volume is ever touched.
        $volume = 'lumberjacks-local_gatewaydata'
        $inner = "if [ -f /data/valheim-zdo-journal.jsonl ]; then mv /data/valheim-zdo-journal.jsonl /data/valheim-zdo-journal.jsonl.bak-$stamp && stat -c '%n|%s' /data/valheim-zdo-journal.jsonl.bak-$stamp; else echo '/data/valheim-zdo-journal.jsonl|absent'; fi"
        $listing = @(& docker run --rm -v "${volume}:/data" alpine sh -c $inner)
        if ($LASTEXITCODE -ne 0) { throw "Volume archive failed for $volume." }
        $archived += ($listing -join '')
    }
    [ordered]@{
        schema_version = 1
        receipt_type = 'lumberjacks_zdo_queue_reset'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        target = $Target
        gateway_container = $GatewayContainer
        gateway_state = $gatewayState
        archived = $archived
        stamp = $stamp
    }
}

function Invoke-Verify() {
    $snapshot = Invoke-RestMethod -Method Get -Uri $VerifyUrl -TimeoutSec 10
    $window = $snapshot.authoritative_window
    $walBytes = [long]($window.wal_bytes)
    $pending = [long]($window.pending)
    $clean = ($walBytes -eq 0) -and ($pending -eq 0)
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'lumberjacks_zdo_queue_verify'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        target = $Target
        verify_url = $VerifyUrl
        wal_bytes = $walBytes
        pending = $pending
        result = if ($clean) { 'clean' } else { 'dirty' }
    }
    if (-not $clean) {
        $receipt | ConvertTo-Json -Depth 4 | Write-Host
        throw "Queue is not clean after reset: wal_bytes=$walBytes pending=$pending"
    }
    $receipt
}

$result = if ($Action -eq 'reset') { Invoke-Reset } else { Invoke-Verify }
$result | ConvertTo-Json -Depth 4
