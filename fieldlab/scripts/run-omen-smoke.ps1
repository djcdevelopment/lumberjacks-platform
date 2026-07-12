# One-command OMEN authenticity smoke. PowerShell 5.1, ASCII only.
#
# Chains the whole loopback-integrity smoke around the SINGLE manual touch (the Valheim join
# password). It composes the two proven drivers - run-loopback-window.ps1 (server arm/gate/disarm)
# and omen-client-smoke.ps1 (the client launch handoff) - into one runnable, so the operator's only
# job is to type the join password when the dialog appears.
#
# Flow:
#   1. run-loopback-window.ps1 -Stage arm     (relaunch LJ + stage windows + arm am4/P7 + restart)
#   2. omen-client-smoke.ps1                  (force-quit + COMFY_AUTOJOIN launch + [MANUAL password] + confirm)
#   3. wait the armed probe/rehearsal window  (mod auto-walks the p7-compose route hands-free)
#   4. run-loopback-window.ps1 -Stage gate    (read the four gates + save-integrity + stability)
#   5. run-loopback-window.ps1 -Stage disarm  (all four rungs off -> observe-only baseline)   [unless -SkipDisarm]
#
# Run ON OMEN, in the PS session that ran set-gcp-p7-target.ps1 (so FIELDLAB_* env is set):
#   .\set-gcp-p7-target.ps1 -PublicIp <P7-ip>
#   .\run-omen-smoke.ps1 -WindowId i7-live-w1
#
# Prereqs: the P7/am4 server reachable via the FIELDLAB_* env; real GPU desktop on OMEN; Steam logged
# into floooooobcakes. This does NOT touch Steam creds/2FA and does NOT automate the join password.

param(
  [string]$WindowId = "i7-live-w1",
  [string]$Endpoint = $env:FIELDLAB_VALHEIM_ENDPOINT,
  [int]$PostJoinWaitSeconds = 165,
  [int]$ConnectTimeoutSeconds = 180,
  [switch]$StrictConnect,
  [switch]$SkipDisarm
)

$ErrorActionPreference = "Stop"
$loopback = Join-Path $PSScriptRoot "run-loopback-window.ps1"
$clientSmoke = Join-Path $PSScriptRoot "omen-client-smoke.ps1"
foreach ($p in @($loopback, $clientSmoke)) {
  if (-not (Test-Path -LiteralPath $p)) { throw "Missing required script: $p" }
}

Write-Host "############################################################" -ForegroundColor Magenta
Write-Host "# OMEN authenticity smoke (one command, one manual touch)  #" -ForegroundColor Magenta
Write-Host "# window: $WindowId   endpoint: $Endpoint" -ForegroundColor Magenta
Write-Host "############################################################" -ForegroundColor Magenta

Write-Host ""
Write-Host "[1/5] ARM server+mod window ..." -ForegroundColor Cyan
& $loopback -Stage arm -WindowId $WindowId
if ($LASTEXITCODE) { throw "arm stage failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "[2/5] Client launch handoff (MANUAL password join happens here) ..." -ForegroundColor Cyan
$smokeArgs = @{ WindowId = $WindowId; ConnectTimeoutSeconds = $ConnectTimeoutSeconds }
if ($Endpoint) { $smokeArgs["Endpoint"] = $Endpoint }
if ($StrictConnect) { $smokeArgs["StrictConnect"] = $true }
& $clientSmoke @smokeArgs
if ($StrictConnect -and $LASTEXITCODE) { throw "client smoke did not confirm connect (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "[3/5] Armed window running - the mod auto-walks the p7-compose route." -ForegroundColor Cyan
Write-Host "      Waiting ${PostJoinWaitSeconds}s for the probe/rehearsal window to complete ..." -ForegroundColor Gray
$end = (Get-Date).AddSeconds($PostJoinWaitSeconds)
while ((Get-Date) -lt $end) {
  $left = [int]([Math]::Ceiling(($end - (Get-Date)).TotalSeconds))
  Write-Host "      ...$left s remaining" -ForegroundColor DarkGray
  Start-Sleep -Seconds ([Math]::Min(15, [Math]::Max(1, $left)))
}

Write-Host ""
Write-Host "[4/5] GATE - reading the four rungs ..." -ForegroundColor Cyan
& $loopback -Stage gate -WindowId $WindowId

if ($SkipDisarm) {
  Write-Host ""
  Write-Host "[5/5] -SkipDisarm set: leaving the window armed. Disarm later with:" -ForegroundColor Yellow
  Write-Host "      .\run-loopback-window.ps1 -Stage disarm" -ForegroundColor Yellow
} else {
  Write-Host ""
  Write-Host "[5/5] DISARM -> observe-only baseline ..." -ForegroundColor Cyan
  & $loopback -Stage disarm
}

Write-Host ""
Write-Host "OMEN authenticity smoke complete for window $WindowId." -ForegroundColor Green
