[CmdletBinding()]
param(
  [string] $Server = "8.231.129.249:2456",
  [string] $Steam = "C:\Program Files (x86)\Steam\steam.exe",
  [int] $ReadyTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

& "$PSScriptRoot\start-gateway-tunnel.ps1" | Out-Null

$running = Get-Process valheim -ErrorAction SilentlyContinue
if ($running) {
  [pscustomobject]@{ AlreadyRunning = $true; ProcessId = $running.Id; Server = $Server }
  return
}

if (!(Test-Path $Steam)) { throw "Steam executable not found at $Steam" }
Start-Process $Steam -ArgumentList @("-applaunch", "892970", "+connect", $Server)

$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
do {
  Start-Sleep -Seconds 1
  $running = Get-Process valheim -ErrorAction SilentlyContinue
  if ($running) {
    [pscustomobject]@{ AlreadyRunning = $false; ProcessId = $running.Id; Server = $Server }
    return
  }
} while ((Get-Date) -lt $deadline)

throw "Valheim did not start within $ReadyTimeoutSeconds seconds"
