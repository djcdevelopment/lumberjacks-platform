# P4/I3 redirect gate-window wrapper (the one-command guardrail stage driver).
#
# Usage (each stage is one command; Derek's join happens between arm and gate):
#   .\run-redirect-window.ps1 -Stage arm      -WindowId i3-w1 [-Prefabs Beech1] [-ActiveSeconds 90]
#   .\run-redirect-window.ps1 -Stage gate     -WindowId i3-w1
#   .\run-redirect-window.ps1 -Stage disarm
#   .\run-redirect-window.ps1 -Stage arm-pin              # optional window A: I2 repeatability
#   .\run-redirect-window.ps1 -Stage gate-pin             # (pin re-arm on the proven 0.5.10 path,
#                                                         #  now carried unchanged in 0.5.11)
#
# arm      = set ROOT cfg flags (never the config/ decoy) + save-integrity snapshot + restart +
#            wait for world-up. The ~30-min UPDATE_IF_IDLE clock effectively restarts with the
#            container, so a join right after "JOIN NOW" is clear of the boundary.
# gate     = one-call redirect gate (mod jsonl vs Lumberjacks receipts) + rollback-rehearsal
#            check + OMEN client-stability grep + save-integrity check.
# disarm   = redirect AND pin flags off + restart + readback (observe-only baseline restored).
#
# ASCII only; PowerShell 5.1.

param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("arm", "gate", "disarm", "arm-pin", "gate-pin")]
  [string]$Stage,
  [string]$WindowId = "",
  [string]$Prefabs = "Beech1",
  [int]$ActiveSeconds = 90,
  [int]$ProbeSeconds = 150
)

$ErrorActionPreference = "Stop"

$am4       = if ($env:FIELDLAB_AM4_SSH) { $env:FIELDLAB_AM4_SSH } else { "derek@am4" }
$container = if ($env:FIELDLAB_VALHEIM_CONTAINER) { $env:FIELDLAB_VALHEIM_CONTAINER } else { "comfy-valheim-server-am4-valheim-server-1" }
$bepinex   = if ($env:FIELDLAB_AM4_BEPINEX) { $env:FIELDLAB_AM4_BEPINEX } else { "~/comfy-valheim-lab/server-state/config/bepinex" }
$remoteCfg = "$bepinex/djcdevelopment.valheim.comfynetworksense.cfg"   # ROOT - NOT config/ (decoy)
$python    = if ($env:FIELDLAB_PYTHON) { $env:FIELDLAB_PYTHON } else { "C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe" }
$clientLog = "C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\LogOutput.log"

$env:PYTHONPATH = "C:\work\comfy\network\mcp"
$env:PYTHONUTF8 = "1"

function Invoke-Tool {
  param([string]$Expr)
  & $python -c "import json; from comfy_gateway.toolsurface import valheim as v; print(json.dumps($Expr, indent=2, default=str))"
}

function Restart-AndWait {
  Write-Host "Restarting ${container} ..." -ForegroundColor Gray
  ssh $am4 "docker restart $container" | Out-Null
  $deadline = (Get-Date).AddMinutes(4)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    $up = ssh $am4 "docker logs --since 4m $container 2>&1 | grep -c 'Game server connected'"
    if ([int]$up -ge 1) {
      Write-Host "World up (Game server connected)." -ForegroundColor Green
      return
    }
  }
  throw "Server did not reach 'Game server connected' within 4 minutes - inspect docker logs."
}

function Set-Flags {
  param([string]$SedArgs, [string]$ReadbackPattern)
  ssh $am4 "sed -i $SedArgs $remoteCfg"
  Write-Host "ROOT cfg readback:" -ForegroundColor Gray
  ssh $am4 "grep -E '$ReadbackPattern' $remoteCfg"
}

switch ($Stage) {
  "arm" {
    if (-not $WindowId) { $WindowId = "i3-w-" + (Get-Date -Format "yyyyMMdd-HHmmss") }
    Write-Host "=== ARM redirect window '${WindowId}' (prefabs=$Prefabs activeSeconds=$ActiveSeconds probe=$ProbeSeconds) ===" -ForegroundColor Cyan
    $sed = "-e 's/^zdoRedirectEnabled = .*/zdoRedirectEnabled = true/' " +
           "-e 's/^zdoRedirectPrefabs = .*/zdoRedirectPrefabs = $Prefabs/' " +
           "-e 's/^zdoRedirectWindowId = .*/zdoRedirectWindowId = $WindowId/' " +
           "-e 's/^zdoRedirectActiveSeconds = .*/zdoRedirectActiveSeconds = $ActiveSeconds/' " +
           "-e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = false/' " +
           "-e 's/^netcodeProbeAutoStartEnabled = .*/netcodeProbeAutoStartEnabled = true/' " +
           "-e 's/^netcodeProbeAutoStopSeconds = .*/netcodeProbeAutoStopSeconds = $ProbeSeconds/'"
    Set-Flags -SedArgs $sed -ReadbackPattern '^(zdoRedirect[A-Za-z]*|ownershipPinEnabled|netcodeProbeAutoS[a-z]*) ='
    Write-Host "Save-integrity snapshot ..." -ForegroundColor Gray
    Invoke-Tool "v.valheim_save_integrity(action='snapshot', label='$WindowId')"
    Restart-AndWait
    Write-Host ""
    Write-Host ">>> JOIN NOW (wary.fool -> 100.116.82.60:2456). Window auto-runs: probe ${ProbeSeconds}s," -ForegroundColor Yellow
    Write-Host ">>> redirect auto-disarms at ${ActiveSeconds}s (rollback rehearsal). Then: -Stage gate -WindowId $WindowId" -ForegroundColor Yellow
  }
  "gate" {
    if (-not $WindowId) { throw "-WindowId required for gate" }
    Write-Host "=== GATE redirect window '${WindowId}' ===" -ForegroundColor Cyan
    Write-Host "-- one-call gate (mod vs Lumberjacks receipts):" -ForegroundColor Gray
    Invoke-Tool "v.valheim_zdo_redirect_gate(window_id='$WindowId')"
    Write-Host "-- rollback rehearsal (want stop_event=redirect_auto_stop):" -ForegroundColor Gray
    Invoke-Tool "{k: v.valheim_zdo_redirect_status('am4').get(k) for k in ('stop_event','counters_are_authoritative','caveat')}"
    Write-Host "-- OMEN client stability (want zero matches):" -ForegroundColor Gray
    $bad = Select-String -Path $clientLog -Pattern 'INVALID_MESSAGE|socket error|Got connection error' -SimpleMatch:$false
    if ($bad) { $bad | Select-Object -Last 10 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red } }
    else { Write-Host "clean (no INVALID_MESSAGE / socket errors in client log)" -ForegroundColor Green }
    Write-Host "-- save-integrity check:" -ForegroundColor Gray
    Invoke-Tool "v.valheim_save_integrity(action='check')"
  }
  "disarm" {
    Write-Host "=== DISARM (redirect + pin off, observe-only baseline) ===" -ForegroundColor Cyan
    $sed = "-e 's/^zdoRedirectEnabled = .*/zdoRedirectEnabled = false/' " +
           "-e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = false/'"
    Set-Flags -SedArgs $sed -ReadbackPattern '^(zdoRedirectEnabled|ownershipPinEnabled) ='
    Restart-AndWait
  }
  "arm-pin" {
    Write-Host "=== ARM window A: I2 repeatability pin (redirect stays OFF) ===" -ForegroundColor Cyan
    $sed = "-e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = true/' " +
           "-e 's/^zdoRedirectEnabled = .*/zdoRedirectEnabled = false/' " +
           "-e 's/^netcodeProbeAutoStartEnabled = .*/netcodeProbeAutoStartEnabled = true/' " +
           "-e 's/^netcodeProbeAutoStopSeconds = .*/netcodeProbeAutoStopSeconds = $ProbeSeconds/'"
    Set-Flags -SedArgs $sed -ReadbackPattern '^(zdoRedirectEnabled|ownershipPin[A-Za-z]*|netcodeProbeAutoS[a-z]*) ='
    Write-Host "Save-integrity snapshot ..." -ForegroundColor Gray
    Invoke-Tool "v.valheim_save_integrity(action='snapshot', label='i2-repeat')"
    Restart-AndWait
    Write-Host ""
    Write-Host ">>> JOIN NOW for window A. After the ${ProbeSeconds}s window: -Stage gate-pin, then" -ForegroundColor Yellow
    Write-Host ">>> -Stage arm -WindowId i3-w1 for window B (server restart kicks the client; rejoin)." -ForegroundColor Yellow
  }
  "gate-pin" {
    Write-Host "=== GATE window A: I2 repeatability ===" -ForegroundColor Cyan
    Invoke-Tool "{k: v.valheim_ownership_pin_status('am4').get(k) for k in ('gate','counters_are_authoritative','counters','caveat')}"
    Write-Host "-- save-integrity check:" -ForegroundColor Gray
    Invoke-Tool "v.valheim_save_integrity(action='check')"
  }
}
