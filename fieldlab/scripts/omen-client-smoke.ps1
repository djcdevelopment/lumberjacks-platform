# OMEN client authenticity-smoke launch handoff. PowerShell 5.1, ASCII only.
#
# Closes the ONE step run-loopback-window.ps1 leaves manual: the fresh-launch + join of the real
# OMEN Windows Valheim client (client of record - RTX 5070, real GPU). run-loopback-window.ps1
# -Stage arm restarts the am4/P7 server and then STOPS at ">>> FRESH-LAUNCH Valheim ... and join".
# This script IS that fresh launch, scripted around the single unavoidable manual touch: the operator
# typing the Valheim JOIN password into the in-game dialog. Everything else is automated.
#
# It does NOT drive character-select (COMFY_AUTOJOIN in the mod does that) and it does NOT send the
# server address to the join dialog (the +connect launch arg does that). The one thing a human still
# does is type the Valheim join password - by design (Constraint: the manual join stays manual).
#
# Usage (run ON OMEN, in the same PS session that ran set-gcp-p7-target.ps1 so the env is inherited):
#   .\omen-client-smoke.ps1                          # endpoint from $env:FIELDLAB_VALHEIM_ENDPOINT
#   .\omen-client-smoke.ps1 -Endpoint 1.2.3.4:2456   # explicit endpoint override
#   .\omen-client-smoke.ps1 -SkipLaunch              # preflight + checks only, no valheim.exe launch
#
# Two DISTINCT secrets are in play (never conflated):
#   1. Valheim JOIN password  - typed by the operator into the in-game dialog. Manual. Unchanged.
#   2. Lumberjacks NETWORK password - what the MOD would present on its gateway connection. Supplied
#      here as an OPTIONAL env passthrough ($env:COMFY_LUMBERJACKS_NETWORK_PASSWORD), sourced from env
#      or a gitignored local file - NEVER hard-coded. See the NOTE in Resolve-LumberjacksPassword:
#      the open GCP P7 "SUT" the smoke targets today runs WITHOUT this password, and the current mod +
#      gateway build does not yet consume it. The passthrough is wired for when the auth layer lands.

param(
  [string]$Endpoint = $env:FIELDLAB_VALHEIM_ENDPOINT,
  [string]$WindowId = "i7-live-w1",
  [string]$ExpectedAccount = "floooooobcakes",
  [string]$ExpectedSteamId = "76561198088711642",
  [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
  [string]$SteamConfig = "C:\Program Files (x86)\Steam\config\loginusers.vdf",
  [string]$LumberjacksNetworkPassword = "",
  [string]$SecretsFile = (Join-Path $PSScriptRoot "omen-client-smoke.secrets.ps1"),
  [int]$ConnectTimeoutSeconds = 180,
  [switch]$SkipLaunch,
  [switch]$StrictConnect
)

$ErrorActionPreference = "Stop"

$valheimExe = Join-Path $ValheimDir "valheim.exe"
$localLow   = Join-Path $env:USERPROFILE "AppData\LocalLow\IronGate\Valheim"

# Connect success + failure line signatures observed in Player.log (see fieldlab notes 2026-07-11).
$connectOkPattern   = 'k_ESteamNetworkingConnectionState_Connected|Got connection SteamID|Load world|Received ZDO'
$connectBadPattern  = 'INVALID_MESSAGE|socket error|Got connection error|desync|ErrorDisconnected|Lost connection to server|Failed to connect|ErrorPassword|Wrong password|k_ESteamNetworkingConnectionState_ClosedByPeer'

# --- helpers --------------------------------------------------------------------------------------

function Assert-InteractiveDesktop {
  # The client of record needs a real GPU desktop. A session-0 / service / non-interactive context
  # cannot render Valheim (headless is a DEAD PATH - see MULTIPLAYER-NETWORK-SETUP.md failure table).
  $sessionId = (Get-Process -Id $PID).SessionId
  if ($sessionId -eq 0) {
    throw "Refusing to run in session 0 (service/headless). The OMEN client needs a real GPU desktop session."
  }
  if (-not [Environment]::UserInteractive) {
    throw "Refusing to run in a non-interactive context. The OMEN client needs an interactive desktop session."
  }
  Write-Host "Desktop session OK (session $sessionId, interactive)." -ForegroundColor Green
}

function Get-MostRecentSteamAccount {
  # Parse loginusers.vdf and return the account block flagged MostRecent "1" (the one Steam will use).
  if (-not (Test-Path -LiteralPath $SteamConfig)) { return $null }
  $lines = Get-Content -LiteralPath $SteamConfig
  $blocks = New-Object System.Collections.Generic.List[object]
  $current = $null
  foreach ($line in $lines) {
    $t = $line.Trim()
    if ($t -match '^"(\d{17})"$') {
      $current = [ordered]@{ steamid = $Matches[1]; account = ""; persona = ""; mostRecent = "0" }
      $blocks.Add($current); continue
    }
    if ($null -eq $current) { continue }
    if ($t -match '^"AccountName"\s+"(.*)"$') { $current.account = $Matches[1] }
    elseif ($t -match '^"PersonaName"\s+"(.*)"$') { $current.persona = $Matches[1] }
    elseif ($t -match '^"MostRecent"\s+"(.*)"$') { $current.mostRecent = $Matches[1] }
  }
  return @($blocks | Where-Object { $_.mostRecent -eq "1" }) | Select-Object -First 1
}

function Test-SteamAccount {
  # NON-FATAL sanity check: warn loudly if the signed-in account is not the client-of-record identity.
  $acct = Get-MostRecentSteamAccount
  if ($null -eq $acct) {
    Write-Host "WARN: could not read Steam loginusers.vdf ($SteamConfig) - skipping account check." -ForegroundColor Yellow
    return
  }
  if ($acct.account -eq $ExpectedAccount -and $acct.steamid -eq $ExpectedSteamId) {
    Write-Host "Steam account OK: $($acct.account) / $($acct.persona) ($($acct.steamid))." -ForegroundColor Green
    return
  }
  Write-Host "======================================================================" -ForegroundColor Yellow
  Write-Host "WARN: signed-in Steam account is NOT the client of record." -ForegroundColor Yellow
  Write-Host "      expected: $ExpectedAccount / wary.fool ($ExpectedSteamId)" -ForegroundColor Yellow
  Write-Host "      found:    $($acct.account) / $($acct.persona) ($($acct.steamid))" -ForegroundColor Yellow
  if ($acct.account -eq "waryfool" -or $acct.persona -eq "Zephar410") {
    Write-Host "      This is the SERVER-SIDE identity (Zephar410/waryfool) baked into the pushed" -ForegroundColor Red
    Write-Host "      VM image. Do NOT cross it with the client. Log OMEN's Steam into $ExpectedAccount." -ForegroundColor Red
  }
  Write-Host "======================================================================" -ForegroundColor Yellow
}

function Test-SteamRunning {
  if (-not (Get-Process -Name "steam" -ErrorAction SilentlyContinue)) {
    Write-Host "WARN: steam.exe is not running. Valheim needs Steam running + logged in to launch/join." -ForegroundColor Yellow
    return $false
  }
  Write-Host "Steam is running." -ForegroundColor Green
  return $true
}

function Resolve-LumberjacksPassword {
  # Resolve the Lumberjacks NETWORK password from (precedence): -LumberjacksNetworkPassword param,
  # then $env:COMFY_LUMBERJACKS_NETWORK_PASSWORD, then a gitignored local secrets file that sets
  # $LumberjacksNetworkPassword. Export it to the child process env so the mod inherits it.
  #
  # NOTE (ground truth, verified 2026-07-11): this is a SEPARATE secret from the Valheim join
  # password. It is NOT typed into any Valheim dialog. As of the current build the mod
  # (ComfyNetworkSense PluginConfig, [Lumberjacks] section) exposes lumberjacksGatewayUrl /
  # COMFY_LUMBERJACKS_GATEWAY_URL but NO password key, and the Lumberjacks gateway has no auth on any
  # path (WS join + HTTP endpoints are open). The open GCP P7 "SUT" the smoke targets runs WITHOUT
  # this password. So today this export is a documented NO-OP forward-passthrough: when the gateway
  # auth layer + a mod [Lumberjacks] password key land, this same env carries the secret with zero
  # script change. It is read from env / a gitignored file and never hard-coded (Constraint).
  $pw = $LumberjacksNetworkPassword
  if ([string]::IsNullOrEmpty($pw)) { $pw = $env:COMFY_LUMBERJACKS_NETWORK_PASSWORD }
  if ([string]::IsNullOrEmpty($pw) -and (Test-Path -LiteralPath $SecretsFile)) {
    Write-Host "Sourcing local secrets file: $SecretsFile" -ForegroundColor Gray
    . $SecretsFile
    if (-not [string]::IsNullOrEmpty($LumberjacksNetworkPassword)) { $pw = $LumberjacksNetworkPassword }
  }
  if ([string]::IsNullOrEmpty($pw)) {
    Write-Host "Lumberjacks network password: not set (open SUT mode - the P7 target requires none)." -ForegroundColor Gray
    return
  }
  $env:COMFY_LUMBERJACKS_NETWORK_PASSWORD = $pw
  Write-Host "Lumberjacks network password: set from secret and exported to the client env" -ForegroundColor Green
  Write-Host "  (passthrough only - the current mod/gateway build does not yet consume it)." -ForegroundColor Gray
}

function Get-CurrentPlayerLog {
  # Pick the freshest Unity player log under LocalLow (Player.log is current; Player-prev.log is prior).
  if (-not (Test-Path -LiteralPath $localLow)) { return $null }
  $candidates = @(
    (Join-Path $localLow "Player.log"),
    (Join-Path $localLow "output_log.txt")
  ) | Where-Object { Test-Path -LiteralPath $_ }
  if ($candidates.Count -eq 0) { return $null }
  return ($candidates | Get-Item | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

function Read-LogSafely {
  # Read a log file Valheim holds open for writing. A plain Get-Content hits a sharing violation;
  # open with FileShare ReadWrite|Delete and read from a byte offset so we only see NEW lines.
  param([string]$Path, [long]$FromOffset)
  $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
  try {
    if ($FromOffset -gt $fs.Length) { $FromOffset = 0 }   # log rotated/truncated - re-read from start
    [void]$fs.Seek($FromOffset, [System.IO.SeekOrigin]::Begin)
    $sr = New-Object System.IO.StreamReader($fs)
    $text = $sr.ReadToEnd()
    $newOffset = $fs.Length
    return [pscustomobject]@{ Text = $text; Offset = $newOffset }
  } finally { $fs.Dispose() }
}

# --- main -----------------------------------------------------------------------------------------

Write-Host "=== OMEN client authenticity-smoke (launch handoff) ===" -ForegroundColor Cyan

Assert-InteractiveDesktop

if ([string]::IsNullOrEmpty($Endpoint)) {
  throw "No endpoint. Pass -Endpoint host:port or run set-gcp-p7-target.ps1 first (sets FIELDLAB_VALHEIM_ENDPOINT)."
}
Write-Host "Target Valheim endpoint: $Endpoint" -ForegroundColor Green

if (-not (Test-Path -LiteralPath $valheimExe)) { throw "valheim.exe not found at $valheimExe" }

Test-SteamAccount
[void](Test-SteamRunning)
Resolve-LumberjacksPassword

# Force-quit any running client FIRST: the mod's auto-rehearsal + netcode probe only re-arm on a full
# process start, not a menu rejoin. A stale client would silently skip the whole armed window.
$running = @(Get-Process -Name "valheim" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
  Write-Host "Force-quitting $($running.Count) running valheim.exe (auto-rehearsal re-arms only on a fresh start) ..." -ForegroundColor Gray
  $running | Stop-Process -Force
  Start-Sleep 3
}

# Mark the current end of the player log so connect-confirmation only reads lines produced by THIS launch.
$logPath = Get-CurrentPlayerLog
$startOffset = 0
if ($logPath) {
  $startOffset = (Get-Item -LiteralPath $logPath).Length
  Write-Host "Watching client log: $logPath (from offset $startOffset)." -ForegroundColor Gray
} else {
  Write-Host "WARN: no Player.log yet under $localLow - it will be created on launch; connect-confirm may be limited." -ForegroundColor Yellow
}

if ($SkipLaunch) {
  Write-Host "-SkipLaunch set: preflight complete, not launching valheim.exe." -ForegroundColor Yellow
  Write-Host "Would launch: `"$valheimExe`" +connect $Endpoint  (with COMFY_AUTOJOIN=1)" -ForegroundColor Gray
  return
}

# COMFY_AUTOJOIN drives CHARACTER SELECT only (the mod picks the profile + clicks Start). The server
# address comes from +connect; the password panel is left for the operator. Start-Process inherits
# this process env, so the launched client sees COMFY_AUTOJOIN and the Lumberjacks passthrough.
$env:COMFY_AUTOJOIN = "1"
Write-Host "COMFY_AUTOJOIN=1; launching Valheim ..." -ForegroundColor Gray
Start-Process -FilePath $valheimExe -ArgumentList "+connect", $Endpoint -WorkingDirectory $ValheimDir | Out-Null

Write-Host ""
Write-Host "======================================================================" -ForegroundColor Yellow
Write-Host ">>> MANUAL STEP (the one intended manual touch):" -ForegroundColor Yellow
Write-Host ">>> When the Valheim password dialog appears, TYPE THE JOIN PASSWORD" -ForegroundColor Yellow
Write-Host ">>> and click Connect. Character select + server address are automated." -ForegroundColor Yellow
Write-Host "======================================================================" -ForegroundColor Yellow
Write-Host "Waiting up to ${ConnectTimeoutSeconds}s for a connect confirmation in the client log ..." -ForegroundColor Gray

# Re-resolve the log path in case it was just created by the launch.
if (-not $logPath) { Start-Sleep 8; $logPath = Get-CurrentPlayerLog; $startOffset = 0 }
if (-not $logPath) {
  Write-Host "WARN: still no client log to tail; cannot auto-confirm connect. Verify the join by eye." -ForegroundColor Yellow
  Write-Host ""
  Write-Host "Next: .\run-loopback-window.ps1 -Stage gate -WindowId $WindowId  (after the ~150s probe window)" -ForegroundColor Cyan
  return
}

$deadline = (Get-Date).AddSeconds($ConnectTimeoutSeconds)
$offset = $startOffset
$connected = $false
$warned = $false
$accumulated = ""
while ((Get-Date) -lt $deadline -and -not $connected) {
  Start-Sleep 3
  try { $chunk = Read-LogSafely -Path $logPath -FromOffset $offset } catch { continue }
  $offset = $chunk.Offset
  if (-not [string]::IsNullOrEmpty($chunk.Text)) {
    $accumulated += $chunk.Text
    foreach ($ln in ($chunk.Text -split "`r?`n")) {
      if ($ln -match $connectBadPattern) {
        Write-Host "  WARN log: $ln" -ForegroundColor Red
        $warned = $true
      } elseif ($ln -match 'Starting to connect to|k_ESteamNetworkingConnectionState_Connecting|Connected') {
        Write-Host "  log: $ln" -ForegroundColor Gray
      }
    }
    if ($accumulated -match $connectOkPattern) { $connected = $true }
  }
}

Write-Host ""
if ($connected) {
  Write-Host "CONNECT CONFIRMED (client reached the server / began world load)." -ForegroundColor Green
  if ($warned) { Write-Host "  ...but warn-signatures appeared above - inspect before trusting the gate." -ForegroundColor Yellow }
} else {
  $msg = "No connect confirmation within ${ConnectTimeoutSeconds}s. Did the join password go in? Check the client log."
  if ($StrictConnect) { throw $msg }
  Write-Host "WARN: $msg" -ForegroundColor Red
}

Write-Host ""
Write-Host "Next: after the ~150s armed probe/rehearsal window completes, read the four gates:" -ForegroundColor Cyan
Write-Host "  .\run-loopback-window.ps1 -Stage gate -WindowId $WindowId" -ForegroundColor Cyan

if (-not $connected -and $StrictConnect) { exit 1 }
