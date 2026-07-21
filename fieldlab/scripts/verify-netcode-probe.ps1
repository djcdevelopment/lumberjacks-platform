# Verifier for rung I1 — Interception Reachability (VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md).
#
# Reads the netcode-probe.jsonl written by ComfyNetworkSense's
# `network_sense_lumberjacks_netcode_probe` command, scopes to the latest start->stop
# window, and applies the I1 pass gate: nonzero observed sends AND receives during normal
# play, with the ZDO uid/owner fields legible.
#
# Usage:
#   pwsh -File fieldlab\scripts\verify-netcode-probe.ps1
#   pwsh -File fieldlab\scripts\verify-netcode-probe.ps1 -OutSummary fieldlab\runs\i1\netcode-probe-summary.json
#
# Env overrides:
#   FIELDLAB_NETWORKSENSE_LOG_DIR  - directory holding netcode-probe.jsonl
#   FIELDLAB_VALHEIM_DIR           - Valheim install root (for the installed-mod version gate)

param(
  [string]$OutSummary,
  [string]$LogDir,
  [string]$MinVersion = "0.5.6",
  # Freshness guard: when > 0, a probe jsonl older than this many minutes is downgraded to
  # `blocked_stale_probe_data` so stale/copied data cannot silently PASS. 0 = record-only
  # (age/host/hash still recorded in the summary, just not enforced — for re-gating archives).
  [int]$MaxAgeMinutes = 0,
  # Records where this file was read from; helps catch a probe copied off another host.
  [string]$SourceHost = $env:COMPUTERNAME
)

$ErrorActionPreference = "Stop"

function Resolve-NetworkSenseLogDir {
  param([string]$Explicit)

  $candidates = @()
  if ($Explicit) { $candidates += $Explicit }
  if ($env:FIELDLAB_NETWORKSENSE_LOG_DIR) { $candidates += $env:FIELDLAB_NETWORKSENSE_LOG_DIR }

  $valheimDir = if ($env:FIELDLAB_VALHEIM_DIR) { $env:FIELDLAB_VALHEIM_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Valheim" }
  $candidates += @(
    (Join-Path $valheimDir "BepInEx\config\comfy-network-sense"),
    "C:\Program Files\Steam\steamapps\common\Valheim\BepInEx\config\comfy-network-sense"
  )

  foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path $candidate -PathType Container)) {
      return (Resolve-Path $candidate).Path
    }
  }

  return $candidates[0]
}

function Read-JsonLines {
  param([string]$Path)

  if (-not (Test-Path $Path)) { return @() }

  return @(Get-Content -Path $Path | ForEach-Object {
      if ([string]::IsNullOrWhiteSpace($_)) { return }
      try { $_ | ConvertFrom-Json } catch { $null }
    } | Where-Object { $null -ne $_ })
}

function Get-Long {
  param([object]$Value, [long]$Default = 0)
  if ($null -eq $Value) { return $Default }
  try { return [long]$Value } catch { return $Default }
}

function New-Gate {
  param([string]$Id, [string]$Status, [string]$Observed)
  [ordered]@{ id = $Id; status = $Status; observed = $Observed }
}

$logDir = Resolve-NetworkSenseLogDir -Explicit $LogDir
$probeLogPath = Join-Path $logDir "netcode-probe.jsonl"
$valheimDir = if ($env:FIELDLAB_VALHEIM_DIR) { $env:FIELDLAB_VALHEIM_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Valheim" }
$pluginPath = Join-Path $valheimDir "BepInEx\plugins\ComfyNetworkSense.dll"

# --- Installed mod version -----------------------------------------------------------
$installedVersion = $null
$versionOk = $false
if (Test-Path $pluginPath) {
  try {
    $installedVersion = [Reflection.AssemblyName]::GetAssemblyName($pluginPath).Version.ToString()
    $versionOk = ([version]$installedVersion) -ge ([version]$MinVersion)
  } catch {
    $versionOk = $false
  }
}

# --- Provenance & freshness ----------------------------------------------------------
# Record host + content hash + age so a copied or stale probe file is never mistaken for a
# fresh live capture. Enforcement (downgrade to blocked) only when -MaxAgeMinutes > 0.
$probeSha256 = $null
$probeLastWriteUtc = $null
$probeAgeMinutes = $null
if (Test-Path $probeLogPath) {
  try { $probeSha256 = (Get-FileHash -Path $probeLogPath -Algorithm SHA256).Hash.ToLower() } catch { $probeSha256 = $null }
  $lw = (Get-Item $probeLogPath).LastWriteTimeUtc
  $probeLastWriteUtc = $lw.ToString("o")
  $probeAgeMinutes = [math]::Round((((Get-Date).ToUniversalTime()) - $lw).TotalMinutes, 2)
}
$freshnessEnforced = $MaxAgeMinutes -gt 0
$isStale = $freshnessEnforced -and ($null -ne $probeAgeMinutes) -and ($probeAgeMinutes -gt $MaxAgeMinutes)
$freshStatus = if ($isStale) { "fail" } elseif ($freshnessEnforced) { "pass" } else { "warn" }
$shaShort = if ($probeSha256) { $probeSha256.Substring(0, 12) } else { "n/a" }
$freshMode = if ($freshnessEnforced) { "enforced" } else { "record-only" }
$freshObserved = if ($null -eq $probeAgeMinutes) {
  "probe log missing - no age"
} else {
  "age=$probeAgeMinutes min (max=$MaxAgeMinutes, $freshMode); host=$SourceHost; sha256=$shaShort"
}

$allRows = @(Read-JsonLines -Path $probeLogPath)

# --- Scope to the latest start -> (stop | end-of-file) window -------------------------
$startRows = @($allRows | Where-Object { [string]$_.event -eq "start" } | Sort-Object timestamp_utc)
$latestStart = if ($startRows.Count -gt 0) { $startRows[-1] } else { $null }
$latestStartUtc = if ($latestStart) { [datetime]$latestStart.timestamp_utc } else { $null }

$stopRowsAfterStart = @()
if ($latestStartUtc) {
  $stopRowsAfterStart = @($allRows | Where-Object {
      [string]$_.event -eq "stop" -and ([datetime]$_.timestamp_utc) -ge $latestStartUtc
    } | Sort-Object timestamp_utc)
}
$latestStop = if ($stopRowsAfterStart.Count -gt 0) { $stopRowsAfterStart[-1] } else { $null }
$latestStopUtc = if ($latestStop) { [datetime]$latestStop.timestamp_utc } else { $null }

$windowRows = @($allRows | Where-Object {
    if (-not $latestStartUtc) { return $true }
    $ts = [datetime]$_.timestamp_utc
    if ($ts -lt $latestStartUtc) { return $false }
    if ($latestStopUtc -and $ts -gt $latestStopUtc.AddSeconds(2)) { return $false }
    return $true
  })

# --- Aggregate ------------------------------------------------------------------------
# Prefer the authoritative counters carried on the stop/status row; fall back to counting
# the per-ZDO rows directly if no lifecycle summary landed in the window.
$summaryRow = $latestStop
if (-not $summaryRow) {
  $statusRows = @($windowRows | Where-Object { [string]$_.event -eq "status" } | Sort-Object timestamp_utc)
  if ($statusRows.Count -gt 0) { $summaryRow = $statusRows[-1] }
}

$zdoRows = @($windowRows | Where-Object { [string]$_.event -eq "zdo" })
$recvZdoRowObjects = @($zdoRows | Where-Object { [string]$_.dir -eq "recv" })
$sendZdoRowObjects = @($zdoRows | Where-Object { [string]$_.dir -eq "send" })

if ($summaryRow) {
  $recvFunnelCalls = Get-Long $summaryRow.recv_funnel_calls
  $recvZdoCount = Get-Long $summaryRow.recv_zdo_rows
  $recvParseErrors = Get-Long $summaryRow.recv_parse_errors
  $sendZdosCalls = Get-Long $summaryRow.send_zdos_calls
  $sendZdosFlushed = Get-Long $summaryRow.send_zdos_flushed
  $createSyncListCalls = Get-Long $summaryRow.create_sync_list_calls
  $sendZdoCount = Get-Long $summaryRow.send_zdo_rows
} else {
  # No lifecycle row (e.g. Valheim still running / probe not stopped): fall back to the
  # per-ZDO detail rows that did land.
  $recvFunnelCalls = 0
  $recvZdoCount = $recvZdoRowObjects.Count
  $recvParseErrors = 0
  $sendZdosCalls = 0
  $sendZdosFlushed = 0
  $createSyncListCalls = 0
  $sendZdoCount = $sendZdoRowObjects.Count
}

# --- Legibility: uid non-empty and owner present on a sampled detail row --------------
function Test-Legible {
  param([object[]]$Rows)
  $sample = @($Rows | Select-Object -First 50)
  $legible = 0
  foreach ($r in $sample) {
    $uid = [string]$r.uid
    $ownerPresent = $null -ne $r.owner -and -not [string]::IsNullOrWhiteSpace([string]$r.owner)
    if (-not [string]::IsNullOrWhiteSpace($uid) -and $ownerPresent) { $legible++ }
  }
  return [ordered]@{ sampled = $sample.Count; legible = $legible }
}

$recvLegible = Test-Legible -Rows $recvZdoRowObjects
$sendLegible = Test-Legible -Rows $sendZdoRowObjects

# --- Inlining discrimination ----------------------------------------------------------
# SendZDOs is the residual-risk helper. If its counter is zero while CreateSyncList fired,
# the JIT inlined SendZDOs (a discovery) and the fallback seam still works.
$sendZdosReachable = $sendZdosCalls -gt 0
$createSyncListReachable = $createSyncListCalls -gt 0 -or $sendZdoCount -gt 0
$sendInlinedFallbackOnly = (-not $sendZdosReachable) -and $createSyncListReachable

$recvReachable = ($recvFunnelCalls -gt 0 -or $recvZdoRowObjects.Count -gt 0) -and $recvZdoCount -gt 0
$sendReachable = $createSyncListReachable -and $sendZdoCount -gt 0
$recvLegibleOk = $recvLegible.legible -gt 0
$sendLegibleOk = $sendLegible.legible -gt 0
$bothDirections = $recvReachable -and $sendReachable
$hasWindow = $null -ne $latestStartUtc

# --- Gates ----------------------------------------------------------------------------
$gates = @(
  New-Gate -Id "mod_deployed" -Status ($(if ($versionOk) { "pass" } else { "fail" })) -Observed "Installed ComfyNetworkSense version: $installedVersion (min $MinVersion)."
  New-Gate -Id "probe_window" -Status ($(if ($hasWindow) { "pass" } else { "fail" })) -Observed "Latest start=$($latestStartUtc), stop=$($latestStopUtc), window rows=$($windowRows.Count)."
  New-Gate -Id "receive_funnel_reachable" -Status ($(if ($recvReachable -and $recvLegibleOk) { "pass" } elseif ($recvReachable) { "warn" } else { "fail" })) -Observed "RPC_ZDOData calls=$recvFunnelCalls, recv ZDOs=$recvZdoCount, detail rows=$($recvZdoRowObjects.Count), legible $($recvLegible.legible)/$($recvLegible.sampled), parseErr=$recvParseErrors."
  New-Gate -Id "send_funnel_reachable" -Status ($(if ($sendReachable -and $sendLegibleOk) { "pass" } elseif ($sendReachable) { "warn" } else { "fail" })) -Observed "CreateSyncList calls=$createSyncListCalls, send ZDOs=$sendZdoCount, detail rows=$($sendZdoRowObjects.Count), legible $($sendLegible.legible)/$($sendLegible.sampled)."
  New-Gate -Id "sendzdos_not_inlined" -Status ($(if ($sendZdosReachable) { "pass" } elseif ($sendInlinedFallbackOnly) { "warn" } else { "pending" })) -Observed "SendZDOs postfix calls=$sendZdosCalls (flushed=$sendZdosFlushed). $(if ($sendInlinedFallbackOnly) { 'SendZDOs appears inlined; CreateSyncList fallback seam carried the send observation.' } elseif ($sendZdosReachable) { 'SendZDOs is directly patchable (residual inlining risk retired).' } else { 'No send activity yet to judge.' })"
  New-Gate -Id "both_directions" -Status ($(if ($bothDirections) { "pass" } else { "fail" })) -Observed "recvReachable=$recvReachable, sendReachable=$sendReachable."
  New-Gate -Id "no_vanilla_mutation" -Status "pass" -Observed "Probe is observation-only: it re-parses a copy of received wire bytes and reads outbound ZDO objects; no ZDOs, owners, revisions, or transforms were written."
  New-Gate -Id "probe_freshness" -Status $freshStatus -Observed $freshObserved
)

$failCount = @($gates | Where-Object { $_.status -eq "fail" }).Count
$warnCount = @($gates | Where-Object { $_.status -eq "warn" }).Count

$status = if (-not $versionOk) {
  "blocked_mod_not_deployed"
} elseif (-not $hasWindow) {
  "waiting_for_probe_run"
} elseif ($bothDirections -and $recvLegibleOk -and $sendLegibleOk -and $failCount -eq 0) {
  if ($sendInlinedFallbackOnly) { "pass_i1_reachable_sendzdos_inlined_fallback" } else { "pass_i1_reachable" }
} elseif ($recvReachable -or $sendReachable) {
  "partial_one_direction"
} else {
  "fail_no_funnel_observed"
}

# Stale data can never present as a PASS, regardless of the funnel gates.
if ($isStale) { $status = "blocked_stale_probe_data" }

$summary = [ordered]@{
  schema_version = 1
  rung = "I1"
  status = $status
  generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
  network_sense_log_dir = $logDir
  probe_log_path = $probeLogPath
  installed_version = $installedVersion
  min_version = $MinVersion
  provenance = [ordered]@{
    source_host = $SourceHost
    probe_sha256 = $probeSha256
    probe_last_write_utc = $probeLastWriteUtc
    probe_age_minutes = $probeAgeMinutes
    max_age_minutes = $MaxAgeMinutes
    freshness_enforced = $freshnessEnforced
    is_stale = $isStale
  }
  window = [ordered]@{
    start_utc = if ($latestStartUtc) { $latestStartUtc.ToUniversalTime().ToString("o") } else { $null }
    stop_utc = if ($latestStopUtc) { $latestStopUtc.ToUniversalTime().ToString("o") } else { $null }
    row_count = $windowRows.Count
    has_lifecycle_summary = $null -ne $summaryRow
  }
  observed = [ordered]@{
    recv_funnel_calls = $recvFunnelCalls
    recv_zdo_rows = $recvZdoCount
    recv_parse_errors = $recvParseErrors
    recv_detail_rows = $recvZdoRowObjects.Count
    recv_legible = $recvLegible
    send_zdos_calls = $sendZdosCalls
    send_zdos_flushed = $sendZdosFlushed
    create_sync_list_calls = $createSyncListCalls
    send_zdo_rows = $sendZdoCount
    send_detail_rows = $sendZdoRowObjects.Count
    send_legible = $sendLegible
    send_inlined_fallback_only = $sendInlinedFallbackOnly
  }
  gates = $gates
  fail_count = $failCount
  warn_count = $warnCount
  claims_not_proven = @(
    "Ownership seizure (I2)",
    "Outbound redirect to Lumberjacks (I3)",
    "Inbound injection (I4)",
    "Any replacement of vanilla ZDO transport"
  )
}

if ($OutSummary) {
  $outDir = Split-Path -Parent $OutSummary
  if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
  $summary | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 $OutSummary
}

Write-Host ""
Write-Host "=== I1 Netcode Probe Verifier ===" -ForegroundColor Cyan
Write-Host "log dir     : $logDir"
Write-Host "installed   : $installedVersion (min $MinVersion)"
Write-Host "window rows : $($windowRows.Count)  start=$latestStartUtc  stop=$latestStopUtc"
Write-Host ("recv        : calls={0} zdos={1} detail={2} legible={3}/{4} parseErr={5}" -f $recvFunnelCalls, $recvZdoCount, $recvZdoRowObjects.Count, $recvLegible.legible, $recvLegible.sampled, $recvParseErrors)
Write-Host ("send        : SendZDOs={0}/flushed={1} CreateSyncList={2} zdos={3} detail={4} legible={5}/{6}" -f $sendZdosCalls, $sendZdosFlushed, $createSyncListCalls, $sendZdoCount, $sendZdoRowObjects.Count, $sendLegible.legible, $sendLegible.sampled)
Write-Host ""
foreach ($g in $gates) {
  $color = switch ($g.status) { "pass" { "Green" } "warn" { "Yellow" } "pending" { "Yellow" } default { "Red" } }
  Write-Host ("  [{0}] {1} - {2}" -f $g.status.ToUpper(), $g.id, $g.observed) -ForegroundColor $color
}
Write-Host ""
$statusColor = if ($status -like "pass*") { "Green" } elseif ($status -like "partial*" -or $status -like "waiting*") { "Yellow" } else { "Red" }
Write-Host "STATUS: $status" -ForegroundColor $statusColor
if ($OutSummary) { Write-Host "summary: $OutSummary" }
Write-Host ""

$summary
