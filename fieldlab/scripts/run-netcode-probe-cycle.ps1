# One-command netcode-probe cycle orchestrator - P1 step 11 (TEST-PROGRAM.md).
#
# Chains the full hands-free loop for the I1 headless server probe:
#   deploy -> flags -> restart -> (client walk NOTE) -> pull -> gate -> status+dashboard
# Every am4/ssh action is skippable so this also runs headless against an idle server
# (P1 step 12: headless dry-run / structural-zero, labeled).
#
# Usage:
#   # Full deploy + measure cycle (needs am4 reachable; Derek's client joined for a real gate):
#   pwsh -File fieldlab\scripts\run-netcode-probe-cycle.ps1
#
#   # P1.12 dry-run path - no build/scp/restart; just pull + gate + dashboard (read-only):
#   pwsh -File fieldlab\scripts\run-netcode-probe-cycle.ps1 -SkipDeploy
#
#   # Rehearse everything without touching am4 - print the deploy/flag/restart commands only:
#   pwsh -File fieldlab\scripts\run-netcode-probe-cycle.ps1 -DryRun
#
# Env overrides:
#   FIELDLAB_AM4_SSH             - ssh target for the am4 server        (default: derek@am4)
#   FIELDLAB_VALHEIM_CONTAINER   - docker container on am4              (default: comfy-valheim-server-am4-valheim-server-1)
#   FIELDLAB_AM4_BEPINEX         - remote bepinex ROOT dir on am4       (default: ~/comfy-valheim-lab/server-state/config/bepinex)
#   FIELDLAB_PYTHON              - python.exe for render-dashboard.py   (default: Python312 full path; PATH `python` is a Store stub)
#
# NOTE ON THE ROOT-CFG-NOT-DECOY TRAP (MULTIPLAYER-NETWORK-SETUP.md Trap 2):
#   The mod reads   <bepinex>/djcdevelopment.valheim.comfynetworksense.cfg  (bepinex ROOT).
#   The DECOY it NEVER reads is  <bepinex>/config/djcdevelopment.valheim.comfynetworksense.cfg.
#   This script only ever edits the ROOT cfg.

param(
  [switch]$SkipDeploy,
  [switch]$DryRun,
  [string]$MinVersion = "0.5.7",
  [int]$AutoStopSeconds = 150,
  [string]$RunDir
)

$ErrorActionPreference = "Stop"

# --- Paths & config -------------------------------------------------------------------
$fieldLabRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot     = (Resolve-Path (Join-Path $fieldLabRoot "..")).Path

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if (-not $RunDir) { $RunDir = Join-Path $fieldLabRoot "runs\i1-airtight\$stamp" }

$csproj      = Join-Path $repoRoot "network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj"
$builtDll    = Join-Path $repoRoot "network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll"
$verifyScript= Join-Path $PSScriptRoot "verify-netcode-probe.ps1"
$renderScript= Join-Path $fieldLabRoot "scripts\render-dashboard.py"
$statusPath  = Join-Path $fieldLabRoot "status\program-status.json"

$python = if ($env:FIELDLAB_PYTHON) { $env:FIELDLAB_PYTHON } else { "C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe" }

# am4 / remote - no secrets, key-based ssh.
$am4       = if ($env:FIELDLAB_AM4_SSH) { $env:FIELDLAB_AM4_SSH } else { "derek@am4" }
$container = if ($env:FIELDLAB_VALHEIM_CONTAINER) { $env:FIELDLAB_VALHEIM_CONTAINER } else { "comfy-valheim-server-am4-valheim-server-1" }
$bepinex   = if ($env:FIELDLAB_AM4_BEPINEX) { $env:FIELDLAB_AM4_BEPINEX } else { "~/comfy-valheim-lab/server-state/config/bepinex" }

# ROOT cfg (mod actually reads this), remote plugin path, remote probe jsonl.
$remoteCfg    = "$bepinex/djcdevelopment.valheim.comfynetworksense.cfg"   # ROOT - NOT $bepinex/config/... (decoy)
$remotePlugin = "$bepinex/plugins/ComfyNetworkSense.dll"
$remoteProbe  = "$bepinex/comfy-network-sense/netcode-probe.jsonl"

# Local run layout - verifier reads <probeLocalDir>/netcode-probe.jsonl.
$probeLocalDir = Join-Path $RunDir "comfy-network-sense"
$summaryPath   = Join-Path $RunDir "netcode-probe-summary.json"

# Mode: dryrun (echo remote, run read-only) | idle (SkipDeploy, run read-only) | deploy (full).
$mode = if ($DryRun) { "dryrun" } elseif ($SkipDeploy) { "idle" } else { "deploy" }
$doDeploy = -not $SkipDeploy

New-Item -ItemType Directory -Force $probeLocalDir | Out-Null

# --- Helpers --------------------------------------------------------------------------
function Show-Banner {
  param([string]$Name)
  Write-Host ""
  Write-Host "======== $Name ========" -ForegroundColor Cyan
}

# Execute a stage command unless -DryRun, in which case just print what WOULD run.
# Used only for the mutating am4 stages (deploy/flags/restart).
function Invoke-OrEcho {
  param(
    [string]$Desc,
    [string]$CommandText,
    [scriptblock]$Action
  )
  if ($DryRun) {
    Write-Host "  [dry-run] $Desc" -ForegroundColor DarkGray
    Write-Host "            $CommandText" -ForegroundColor DarkGray
    return
  }
  Write-Host "  $Desc" -ForegroundColor Gray
  Write-Host "  > $CommandText" -ForegroundColor DarkGray
  & $Action
}

Write-Host ""
Write-Host "### run-netcode-probe-cycle - mode=$mode  minVersion=$MinVersion  autostop=${AutoStopSeconds}s" -ForegroundColor White
Write-Host "### run dir : $RunDir"
Write-Host "### am4     : $am4  container=$container"

# --- Stage 1: DEPLOY (skippable) ------------------------------------------------------
Show-Banner "1/7 DEPLOY  (build + scp DLL)"
if (-not $doDeploy) {
  Write-Host "  [skip] -SkipDeploy set - not building/shipping; idle/read-only cycle." -ForegroundColor Yellow
} else {
  Invoke-OrEcho `
    -Desc "Build ComfyNetworkSense (Release)" `
    -CommandText "dotnet build -c Release `"$csproj`"" `
    -Action { dotnet build -c Release $csproj }

  Invoke-OrEcho `
    -Desc "Ship DLL to am4 (bepinex/plugins)" `
    -CommandText "scp `"$builtDll`" ${am4}:'$remotePlugin'" `
    -Action { scp $builtDll "${am4}:$remotePlugin" }
}

# --- Stage 2: FLAGS (skippable) - ROOT cfg only, never the config/ decoy --------------
Show-Banner "2/7 FLAGS  (ssh sed the ROOT bepinex cfg)"
if (-not $doDeploy) {
  Write-Host "  [skip] -SkipDeploy set - leaving server probe flags as-is." -ForegroundColor Yellow
} else {
  # Whole-line replacements so reruns are idempotent regardless of current values.
  $sedCmd = "sed -i " +
    "-e 's/^netcodeProbeAutoStartEnabled = .*/netcodeProbeAutoStartEnabled = true/' " +
    "-e 's/^netcodeProbeAutoStopSeconds = .*/netcodeProbeAutoStopSeconds = $AutoStopSeconds/' " +
    "-e 's/^netcodeProbeMaxDetailRows = .*/netcodeProbeMaxDetailRows = 20000/' " +
    "$remoteCfg"
  Invoke-OrEcho `
    -Desc "Set probe flags in ROOT cfg (autostart=true, autostop=$AutoStopSeconds, maxDetailRows=20000)" `
    -CommandText "ssh $am4 `"$sedCmd`"" `
    -Action { ssh $am4 $sedCmd }

  # Read back exactly the three keys we just set (proof we edited the ROOT, not the decoy).
  $grepCmd = "grep -E '^(netcodeProbeAutoStartEnabled|netcodeProbeAutoStopSeconds|netcodeProbeMaxDetailRows) =' $remoteCfg"
  Invoke-OrEcho `
    -Desc "Read back the three flags from the ROOT cfg" `
    -CommandText "ssh $am4 `"$grepCmd`"" `
    -Action { ssh $am4 $grepCmd }
}

# --- Stage 3: RESTART (skippable) - reloads a 9M-ZDO world (~1 min) -------------------
Show-Banner "3/7 RESTART  (docker restart + wait for boot log)"
if (-not $doDeploy) {
  Write-Host "  [skip] -SkipDeploy set - not restarting am4; using whatever is already loaded." -ForegroundColor Yellow
} else {
  $restartCmd = "docker restart $container"
  Invoke-OrEcho `
    -Desc "Restart container (reloads ComfyEra16 / ~9M ZDOs - expect ~1 min)" `
    -CommandText "ssh $am4 `"$restartCmd`"" `
    -Action { ssh $am4 $restartCmd }

  $logCmd = "docker logs --since 5m $container 2>&1 | grep 'Loading \[ComfyNetworkSense'"
  if ($DryRun) {
    Write-Host "  [dry-run] Poll boot log up to ~120s for 'Loading [ComfyNetworkSense $MinVersion]'" -ForegroundColor DarkGray
    Write-Host "            ssh $am4 `"$logCmd`"" -ForegroundColor DarkGray
  } else {
    Write-Host "  Waiting for boot log to show ComfyNetworkSense $MinVersion (world reload ~1 min)..." -ForegroundColor Gray
    $deadline = (Get-Date).AddSeconds(120)
    $loaded = $false
    while ((Get-Date) -lt $deadline) {
      $line = ssh $am4 $logCmd
      if ($line -match [regex]::Escape("ComfyNetworkSense $MinVersion")) {
        Write-Host "  boot log: $line" -ForegroundColor Green
        $loaded = $true
        break
      } elseif ($line) {
        Write-Host "    (saw: $line) - waiting for $MinVersion..." -ForegroundColor DarkYellow
      } else {
        Write-Host "    (no boot line yet - world still reloading)..." -ForegroundColor DarkGray
      }
      Start-Sleep -Seconds 5
    }
    if (-not $loaded) {
      throw "Timed out (~120s) waiting for 'Loading [ComfyNetworkSense $MinVersion]' in am4 boot log."
    }
  }
}

# --- Stage 4: WALK  (client-side, config-coupled - orchestrator does NOT drive it) ----
Show-Banner "4/7 WALK  (client auto-rehearsal - NOTE only)"
Write-Host "  NOTE: the dense-route walk runs on the OMEN rendered client and is armed by the mod" -ForegroundColor Yellow
Write-Host "        config flag 'coupleAutoRehearsalToNetcodeProbe' (CoupleAutoRehearsalToNetcodeProbe)." -ForegroundColor Yellow
Write-Host "        This orchestrator does not launch or drive the client walk." -ForegroundColor Yellow
Write-Host "        A real gate needs Derek's client JOINED so the probe sees a peer + traffic." -ForegroundColor Yellow
Write-Host "        In $mode mode there is no peer - this stage is a structural no-op." -ForegroundColor Yellow

# --- Stage 5: PULL  (always attempted - read-only) ------------------------------------
Show-Banner "5/7 PULL  (scp probe jsonl from am4)"
$localProbe = Join-Path $probeLocalDir "netcode-probe.jsonl"
Write-Host "  scp ${am4}:'$remoteProbe' `"$localProbe`"" -ForegroundColor DarkGray
$pulled = $false
try {
  scp "${am4}:$remoteProbe" $localProbe
  $pulled = $true
} catch {
  Write-Host "  [structural-zero] scp failed (server idle/unreachable or file absent): $($_.Exception.Message)" -ForegroundColor Yellow
}
if ($pulled -and (Test-Path $localProbe) -and ((Get-Item $localProbe).Length -gt 0)) {
  Write-Host "  pulled $((Get-Item $localProbe).Length) bytes -> $localProbe" -ForegroundColor Green
} else {
  Write-Host "  [structural-zero] no probe jsonl present/non-empty locally - expected in $mode mode with no joined peer." -ForegroundColor Yellow
  Write-Host "                    Continuing so the gate + dashboard still run end-to-end." -ForegroundColor Yellow
}

# --- Stage 6: GATE  (verify-netcode-probe.ps1 - read-only) ----------------------------
Show-Banner "6/7 GATE  (verify-netcode-probe.ps1)"
Write-Host "  & `"$verifyScript`" -LogDir `"$probeLocalDir`" -OutSummary `"$summaryPath`" -MinVersion $MinVersion" -ForegroundColor DarkGray
$verifyOut  = & $verifyScript -LogDir $probeLocalDir -OutSummary $summaryPath -MinVersion $MinVersion
$summaryObj = $verifyOut | Select-Object -Last 1
$gateStatus = if ($summaryObj -and $summaryObj.status) { [string]$summaryObj.status } else { "unknown" }
Write-Host "  gate status: $gateStatus" -ForegroundColor Gray

# --- Stage 7: STATUS + DASHBOARD  (safe, idempotent, non-destructive JSON edit) -------
Show-Banner "7/7 STATUS + DASHBOARD"

# Non-destructive update: ConvertFrom-Json yields a PSCustomObject that preserves the
# original property order; we mutate ONLY the P1 step-12 status in place and APPEND a
# top-level `last_probe_cycle` note via Add-Member (append never reorders/drops keys).
# Nothing else in program-status.json is touched. ConvertTo-Json -Depth 20 round-trips it.
$statusObj = Get-Content -Raw -Path $statusPath | ConvertFrom-Json

$p1 = $statusObj.phases | Where-Object { $_.id -eq "P1" }
$step12 = $p1.steps | Where-Object { $_.n -eq 12 }
# A completed dry-run/idle cycle IS the P1.12 gate ("pipeline end-to-end", structural zero
# is the accepted result with no peer). A deploy-mode run leaves it active for the real gate.
$step12Status = if ($mode -eq "deploy") { "active" } else { "done" }
if ($step12) { $step12.s = $step12Status }

$lastProbeCycle = [ordered]@{
  ran_utc      = (Get-Date).ToUniversalTime().ToString("o")
  mode         = $mode
  gate_status  = $gateStatus
  run_dir      = $RunDir
  summary_path = $summaryPath
}
if ($statusObj.PSObject.Properties.Name -contains "last_probe_cycle") {
  $statusObj.last_probe_cycle = $lastProbeCycle
} else {
  $statusObj | Add-Member -NotePropertyName "last_probe_cycle" -NotePropertyValue $lastProbeCycle
}

# Write UTF-8 WITHOUT a BOM: PS 5.1 `Set-Content -Encoding UTF8` prepends a BOM that
# render-dashboard.py (and the committed file's format) do not expect.
[System.IO.File]::WriteAllText($statusPath, ($statusObj | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "  stamped program-status.json: P1.step12='$step12Status', +last_probe_cycle" -ForegroundColor Gray

# Render the dashboard exactly as the repo does (python fieldlab/scripts/render-dashboard.py),
# using the full Python312 path (PATH `python` is a broken Store stub).
$dashboardRendered = $false
Write-Host "  & `"$python`" `"$renderScript`"" -ForegroundColor DarkGray
if (Test-Path $python) {
  & $python $renderScript
  $dashboardRendered = $true
} else {
  Write-Host "  [warn] python not found at '$python' - set FIELDLAB_PYTHON; dashboard NOT re-rendered." -ForegroundColor Yellow
}

# --- SUMMARY --------------------------------------------------------------------------
Show-Banner "SUMMARY"
Write-Host ("  mode              : {0}" -f $mode)
Write-Host ("  gate status       : {0}" -f $gateStatus)
Write-Host ("  run dir           : {0}" -f $RunDir)
Write-Host ("  summary path      : {0}" -f $summaryPath)
Write-Host ("  dashboard rendered: {0}" -f $dashboardRendered)
Write-Host ""
