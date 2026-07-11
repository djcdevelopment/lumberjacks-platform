# P7/I7 loopback-integrity (compose-all) armed-window driver. PowerShell 5.1, ASCII only.
#
# Composes I2 pin + I3 redirect + I4 injection + I5 handshake into ONE window (I6 Steam-only is
# already am4 env). This is a NEW driver because the per-rung scripts cannot be chained -
# run-injection-window.ps1 -Stage arm explicitly DISARMS redirect+pin. Here all four arm together
# in a single am4 cfg-write + single restart, and all four Lumberjacks windows share one window id.
#
# Usage (each stage is one command; Derek's fresh-launch+join happens between arm and gate):
#   .\run-loopback-window.ps1 -Stage check  [-WindowId i7-w1]   # read-only full-stack preflight
#   .\run-loopback-window.ps1 -Stage arm     -WindowId i7-w1     # relaunch LJ + stage 3 windows +
#                                                                # arm am4(pin+redirect+handshake) +
#                                                                # OMEN(injection) + snapshot + restart
#   # >>> Derek FRESH-LAUNCHES Valheim (not a rejoin) and joins wary.fool -> 100.116.82.60:2456
#   .\run-loopback-window.ps1 -Stage gate    -WindowId i7-w1     # all four subsystem gates + save +
#                                                                # client-stability + desync scan
#   .\run-loopback-window.ps1 -Stage disarm                      # all four flags off -> observe-only
#
# Timing is load-bearing (see fieldlab/routes/p7-compose.tsv header): redirect+injection active
# windows open at ARM time (~connect+25s) and close ~90s later, so the route's first stop is short.
#
# Rung ownership: pin+redirect+handshake are am4 (server) flags; injection is the OMEN (client) flag.

param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("check", "stage-lj", "arm", "gate", "disarm")]
  [string]$Stage,
  [string]$WindowId = "i7-live-w1",
  [string]$OmenSteamHost = "76561198088711642",
  [string]$ConiferPrefabs = "FirTree_small,FirTree,Pinetree_01",
  [string]$InjectionPrefab = "Wood",
  [long]$AuthorityId = 5497853135698,
  [uint32]$UidId = 1,
  [float]$X = 9376,
  [float]$Y = 105,
  [float]$Z = 544,
  [int]$ActiveSeconds = 90,
  [int]$ProbeSeconds = 150,
  [int]$ProbeStartDelaySeconds = 25,
  [int]$RehearsalDelaySeconds = 20
)

$ErrorActionPreference = "Stop"

# --- paths + hosts (env overrides match the sibling drivers) --------------------------------------
$repo        = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$lumberjacks = "C:\work\Lumberjacks"
$dotnet9     = "C:\work\dotnet9\dotnet.exe"
$python      = if ($env:FIELDLAB_PYTHON) { $env:FIELDLAB_PYTHON } else { "C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe" }
$valheim     = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
$localCfg    = Join-Path $valheim "BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg"
$routeDir    = Join-Path $valheim "BepInEx\config\comfy-network-sense"
$clientLog   = Join-Path $valheim "BepInEx\LogOutput.log"
$routeSource = Join-Path $repo "fieldlab\routes\p7-compose.tsv"
$routeName   = "p7-compose.tsv"
$gatewayDll  = Join-Path $lumberjacks "src\Game.Gateway\bin\Release\net9.0\Game.Gateway.dll"
$gatewayOut  = Join-Path $repo "fieldlab\status\lumberjacks-gateway-stdout.log"
$gatewayErr  = Join-Path $repo "fieldlab\status\lumberjacks-gateway-stderr.log"
$am4         = if ($env:FIELDLAB_AM4_SSH) { $env:FIELDLAB_AM4_SSH } else { "derek@am4" }
$container   = if ($env:FIELDLAB_VALHEIM_CONTAINER) { $env:FIELDLAB_VALHEIM_CONTAINER } else { "comfy-valheim-server-am4-valheim-server-1" }
$bepinex     = if ($env:FIELDLAB_AM4_BEPINEX) { $env:FIELDLAB_AM4_BEPINEX } else { "~/comfy-valheim-lab/server-state/config/bepinex" }
$remoteCfg   = "$bepinex/djcdevelopment.valheim.comfynetworksense.cfg"   # ROOT - NOT config/ (decoy)
$ljLocal     = "http://127.0.0.1:4000"    # OMEN-local (injection + staging POSTs run from here)
$ljTailnet   = "http://100.124.12.37:4000" # am4-container reaches Lumberjacks over the tailnet

$env:PYTHONPATH = Join-Path $repo "network\mcp"
$env:PYTHONUTF8 = "1"

# --- helpers (mirrors run-injection-window.ps1 / run-redirect-window.ps1) --------------------------
function Set-CfgValue {
  param([string]$Path, [string]$Section, [string]$Key, [string]$Value)
  $lines = @(Get-Content -LiteralPath $Path)
  $pattern = '^' + [regex]::Escape($Key) + ' = .*$'
  $currentSection = ""; $sectionSeen = $false; $written = $false
  $updated = New-Object System.Collections.Generic.List[string]
  foreach ($line in $lines) {
    if ($line -match '^\[(.+)\]$') {
      if ($currentSection -eq $Section -and -not $written) { $updated.Add("$Key = $Value"); $updated.Add(""); $written = $true }
      $currentSection = $Matches[1]
      if ($currentSection -eq $Section) { $sectionSeen = $true }
      $updated.Add($line); continue
    }
    if ($line -match $pattern) {
      if ($currentSection -eq $Section -and -not $written) { $updated.Add("$Key = $Value"); $written = $true }
      continue
    }
    $updated.Add($line)
  }
  if (-not $written) {
    if (-not $sectionSeen) { $updated.Add(""); $updated.Add("[$Section]") }
    $updated.Add("$Key = $Value")
  }
  Set-Content -LiteralPath $Path -Value $updated -Encoding UTF8
}

function Invoke-Tool {
  param([string]$Expr)
  & $python -c "import json; from comfy_gateway.toolsurface import valheim as v; print(json.dumps($Expr, indent=2, default=str))"
}

function Wait-Http {
  param([string]$Url)
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $deadline) {
    try { Invoke-RestMethod -Uri $Url -TimeoutSec 3 | Out-Null; return } catch { Start-Sleep 2 }
  }
  throw "Timed out waiting for $Url"
}

function Restart-ServerAndWait {
  # Restart the game-server PROCESS only (NOT the container). A `docker restart` respawns the
  # container's valheim-updater, whose steamcmd startup-validate restarts the server AGAIN ~4 min
  # later (clean exit 0) and clips the window - the root cause of the i7-live-w1 DC. supervisorctl
  # restarts just the game process (re-reads the mod cfg, saves the world on the clean SIGTERM) and
  # leaves the long-running updater untouched, so no startup-validate restart. It is the same
  # mechanism the container's own daily cron uses (10 5 * * * supervisorctl restart valheim-server).
  $before = [int](ssh $am4 "docker logs --since 40m $container 2>&1 | grep -c 'Game server connected'")
  Write-Host "Restarting valheim-server process (supervisorctl, not docker) ..." -ForegroundColor Gray
  ssh $am4 "docker exec $container /usr/local/bin/supervisorctl restart valheim-server" | Out-Null
  $deadline = (Get-Date).AddMinutes(5)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 15
    $now = [int](ssh $am4 "docker logs --since 40m $container 2>&1 | grep -c 'Game server connected'")
    if ($now -gt $before) { Write-Host "am4 world up (fresh Game server connected)." -ForegroundColor Green; return }
  }
  throw "am4 server did not reach a fresh 'Game server connected' within 5 minutes."
}

function Relaunch-Lumberjacks {
  # Stop any existing gateway on :4000 first (so the build can overwrite the DLL), then rebuild from
  # CURRENT source (the handshake endpoints landed in Lumberjacks 935095b - a stale Release DLL would
  # lack them), then launch the fresh Release DLL detached with the mandatory 0.0.0.0 bind.
  $listeners = @(Get-NetTCPConnection -LocalPort 4000 -State Listen -ErrorAction SilentlyContinue)
  foreach ($listener in $listeners) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if ($process.CommandLine -notmatch 'Game.Gateway') {
      throw "Port 4000 is owned by an unexpected process: $($process.CommandLine)"
    }
    Stop-Process -Id $listener.OwningProcess -Force
    Start-Sleep 2
  }
  $env:DOTNET_ROOT = "C:\work\dotnet9"
  Write-Host "Building Lumberjacks gateway (Release) from source ..." -ForegroundColor Gray
  & $dotnet9 build (Join-Path $lumberjacks "src\Game.Gateway\Game.Gateway.csproj") -c Release --nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Lumberjacks Release build failed (exit $LASTEXITCODE)" }
  Start-Process -FilePath $dotnet9 -ArgumentList @($gatewayDll, '--urls', 'http://0.0.0.0:4000') `
    -WorkingDirectory $lumberjacks -WindowStyle Hidden `
    -RedirectStandardOutput $gatewayOut -RedirectStandardError $gatewayErr
  Wait-Http "$ljLocal/health"
  Write-Host "Lumberjacks gateway rebuilt + relaunched (0.0.0.0:4000)." -ForegroundColor Green
}

function Stage-InjectionFixture {
  param([string]$Wid)
  # Reset any prior state for this window, then stage the synthetic fixture at the conifer stand.
  Invoke-RestMethod -Method Post -Uri "$ljLocal/valheim/zdo-injection/reset/$Wid" | Out-Null
  $body = @{
    window_id = $Wid
    command = @{ command_id = "synthetic-wood-1"; action = "upsert"; prefab = $InjectionPrefab
      uid_user = $AuthorityId; uid_id = $UidId; owner = $AuthorityId
      owner_rev = 1; data_rev = 1; pos = @($X, $Y, $Z) }
  } | ConvertTo-Json -Depth 5
  Invoke-RestMethod -Method Post -Uri "$ljLocal/valheim/zdo-injection/stage" -ContentType "application/json" -Body $body | Out-Null
  Write-Host "Injection fixture staged (synthetic $InjectionPrefab @ $X,$Y,$Z)." -ForegroundColor Green
}

function Configure-HandshakeWindow {
  param([string]$Wid)
  # Fronted ACCEPT via permit-list (the discriminator vanilla am4 lacks): gate B admits ONLY the OMEN
  # client's SteamID host, so admission is provably Lumberjacks-decided. POST /handshake/config fully
  # REPLACES the window context, so every other field falls to its default - notably password stays "".
  # CRITICAL: never set a non-empty context password - the live mod sends password_hash="" and delegates
  # password/ticket to vanilla, so a context password fails gate F and rejects the real client with code
  # 6 (the 0.5.17 misconfig). permitted_hosts is the safe accept discriminator. The single-element array
  # is built as a JSON literal to dodge the PS 5.1 ConvertTo-Json single-element-array-to-scalar trap.
  Invoke-RestMethod -Method Post -Uri "$ljLocal/valheim/handshake/reset/$Wid" | Out-Null
  $body = '{"window_id":"' + $Wid + '","context":{"permitted_hosts":["' + $OmenSteamHost + '"]}}'
  Invoke-RestMethod -Method Post -Uri "$ljLocal/valheim/handshake/config" -ContentType "application/json" -Body $body | Out-Null
  Write-Host "Handshake fronted-ACCEPT window configured (permit-list: $OmenSteamHost)." -ForegroundColor Green
}

# --- stages ---------------------------------------------------------------------------------------
switch ($Stage) {
  "check" {
    Write-Host "=== P7/I7 full-stack preflight (read-only) ===" -ForegroundColor Cyan
    Invoke-Tool "v.valheim_loopback_preflight(window_id='$WindowId')"
  }
  "stage-lj" {
    # Lumberjacks-only staging (no am4 arm, no restart): relaunch + stage the injection fixture +
    # configure the fronted handshake window. Safe to run any time (idempotent per window id) - use
    # it to validate the staging path and to re-stage if the background-fragile gateway drops.
    Write-Host "=== Stage Lumberjacks windows for '$WindowId' (no am4 arm) ===" -ForegroundColor Cyan
    Relaunch-Lumberjacks
    Stage-InjectionFixture $WindowId
    Configure-HandshakeWindow $WindowId
    Write-Host "-- handshake window status:" -ForegroundColor Gray
    Invoke-RestMethod -Uri "$ljLocal/valheim/handshake/status/$WindowId" | ConvertTo-Json -Depth 6
    Write-Host "-- full-stack preflight (expect lumberjacks handshake_fronted + injection_fixture_staged true):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_loopback_preflight(window_id='$WindowId')"
  }
  "arm" {
    Write-Host "=== ARM loopback window '$WindowId' (pin+redirect+injection+handshake) ===" -ForegroundColor Cyan

    # 1. Lumberjacks: relaunch + stage the injection fixture + configure the fronted handshake window.
    #    (The redirect window is created implicitly on the mod's first receipts POST - no pre-stage.)
    Relaunch-Lumberjacks
    Stage-InjectionFixture $WindowId
    Configure-HandshakeWindow $WindowId

    # 2. OMEN client (I4 injection + the compose-all route + timing knobs).
    New-Item -ItemType Directory -Force $routeDir | Out-Null
    Copy-Item -LiteralPath $routeSource -Destination (Join-Path $routeDir $routeName) -Force
    Set-CfgValue $localCfg "Netcode" "zdoInjectionEnabled" "true"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionPrefabs" $InjectionPrefab
    Set-CfgValue $localCfg "Netcode" "zdoInjectionEndpoint" $ljLocal
    Set-CfgValue $localCfg "Netcode" "zdoInjectionWindowId" $WindowId
    Set-CfgValue $localCfg "Netcode" "zdoInjectionClientId" "omen"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionAuthorityId" "$AuthorityId"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionPollSeconds" "1"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionActiveSeconds" "$ActiveSeconds"
    Set-CfgValue $localCfg "Netcode" "netcodeProbeAutoStartEnabled" "true"
    Set-CfgValue $localCfg "Netcode" "netcodeProbeAutoStartDelaySeconds" "$ProbeStartDelaySeconds"
    Set-CfgValue $localCfg "Netcode" "netcodeProbeAutoStopSeconds" "$ProbeSeconds"
    Set-CfgValue $localCfg "Automation" "autoRehearsalRouteFile" $routeName
    Set-CfgValue $localCfg "Automation" "autoRehearsalDelaySeconds" "$RehearsalDelaySeconds"
    Set-CfgValue $localCfg "Automation" "coupleAutoRehearsalToNetcodeProbe" "true"
    Set-CfgValue $localCfg "Automation" "routeGodFlySafeguard" "true"
    Write-Host "OMEN client armed (injection + p7-compose route)." -ForegroundColor Green

    # 3. am4 server (I2 pin + I3 redirect + I5 handshake + observe negative-control + timing knobs).
    $sed =
      "-e 's/^ownershipObserveEnabled = .*/ownershipObserveEnabled = true/' " +
      "-e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = true/' " +
      "-e 's/^ownershipPinAutoCaptureMax = .*/ownershipPinAutoCaptureMax = 25/' " +
      "-e 's/^ownershipPinPrefabs = .*/ownershipPinPrefabs = /' " +
      "-e 's#^zdoRedirectEnabled = .*#zdoRedirectEnabled = true#' " +
      "-e 's#^zdoRedirectPrefabs = .*#zdoRedirectPrefabs = $ConiferPrefabs#' " +
      "-e 's#^zdoRedirectEndpoint = .*#zdoRedirectEndpoint = $ljTailnet#' " +
      "-e 's#^zdoRedirectWindowId = .*#zdoRedirectWindowId = $WindowId#' " +
      "-e 's/^zdoRedirectActiveSeconds = .*/zdoRedirectActiveSeconds = $ActiveSeconds/' " +
      "-e 's/^zdoInjectionEnabled = .*/zdoInjectionEnabled = false/' " +
      "-e 's/^handshakeResponderEnabled = .*/handshakeResponderEnabled = true/' " +
      "-e 's#^handshakeResponderEndpoint = .*#handshakeResponderEndpoint = $ljTailnet#' " +
      "-e 's#^handshakeResponderWindowId = .*#handshakeResponderWindowId = $WindowId#' " +
      "-e 's/^netcodeProbeAutoStartEnabled = .*/netcodeProbeAutoStartEnabled = true/' " +
      "-e 's/^netcodeProbeAutoStartDelaySeconds = .*/netcodeProbeAutoStartDelaySeconds = $ProbeStartDelaySeconds/' " +
      "-e 's/^netcodeProbeAutoStopSeconds = .*/netcodeProbeAutoStopSeconds = $ProbeSeconds/'"
    ssh $am4 "sed -i $sed $remoteCfg"
    Write-Host "am4 ROOT cfg readback:" -ForegroundColor Gray
    ssh $am4 "grep -E '^(ownership(Observe|Pin)[A-Za-z]*|zdoRedirect[A-Za-z]*|zdoInjectionEnabled|handshakeResponder(Enabled|Endpoint|WindowId)|netcodeProbeAutoS[A-Za-z]*) =' $remoteCfg"

    # 4. Save-integrity snapshot on a fresh idle-restart clock, then restart + wait.
    Invoke-Tool "v.valheim_save_integrity(action='snapshot', label='$WindowId')"
    Restart-ServerAndWait

    Write-Host ""
    Write-Host ">>> FRESH-LAUNCH Valheim (full quit+relaunch, NOT a menu rejoin) and join wary.fool" -ForegroundColor Yellow
    Write-Host ">>> -> 100.116.82.60:2456. Handshake decides admission at connect; the p7-compose walk" -ForegroundColor Yellow
    Write-Host ">>> then churns the pin, floats the conifer stand for the redirect, and dwells for the" -ForegroundColor Yellow
    Write-Host ">>> injection poll. After ~${ProbeSeconds}s run: -Stage gate -WindowId $WindowId" -ForegroundColor Yellow
  }
  "gate" {
    Write-Host "=== GATE loopback window '$WindowId' (all four rungs + save + stability) ===" -ForegroundColor Cyan
    Write-Host "-- I5 handshake (want handshake_satisfied or a Lumberjacks-decided accept):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_handshake_gate(window_id='$WindowId')"
    Write-Host "-- I2 pin (want held_with_negative_control):" -ForegroundColor Gray
    Invoke-Tool "{k: v.valheim_ownership_pin_status('am4').get(k) for k in ('gate','counters_are_authoritative','counters','caveat')}"
    Write-Host "-- I3 redirect (want receipts_match_no_loss):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_zdo_redirect_gate(window_id='$WindowId')"
    Write-Host "-- I4 injection (want rendered_with_lumberjacks_owner):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_zdo_injection_gate(window_id='$WindowId')"
    Write-Host "-- HARD GATE save-integrity (want exact / delta 0):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_save_integrity(action='check')"
    Write-Host "-- OMEN client stability (want zero matches):" -ForegroundColor Gray
    $bad = Select-String -Path $clientLog -Pattern 'INVALID_MESSAGE|socket error|Got connection error|desync' -ErrorAction SilentlyContinue
    if ($bad) { $bad | Select-Object -Last 10 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red } }
    else { Write-Host "clean (no INVALID_MESSAGE / socket / desync in client log)" -ForegroundColor Green }
    Write-Host "-- am4 server-log scan (join/peer/error lines):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_server_log_tail(lines=60)"
  }
  "disarm" {
    Write-Host "=== DISARM (all four rungs off -> observe-only baseline on 0.5.18) ===" -ForegroundColor Cyan
    Set-CfgValue $localCfg "Netcode" "zdoInjectionEnabled" "false"
    Set-CfgValue $localCfg "Automation" "autoRehearsalRouteFile" "teleport-route.tsv"
    $sed =
      "-e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = false/' " +
      "-e 's/^zdoRedirectEnabled = .*/zdoRedirectEnabled = false/' " +
      "-e 's/^zdoInjectionEnabled = .*/zdoInjectionEnabled = false/' " +
      "-e 's/^handshakeResponderEnabled = .*/handshakeResponderEnabled = false/'"
    ssh $am4 "sed -i $sed $remoteCfg"
    ssh $am4 "grep -E '^(ownershipPinEnabled|zdoRedirectEnabled|zdoInjectionEnabled|handshakeResponderEnabled) =' $remoteCfg"
    Restart-ServerAndWait
    Write-Host "Observe-only baseline restored. Quit/relaunch clears the client-only synthetic ZDO." -ForegroundColor Green
  }
}
