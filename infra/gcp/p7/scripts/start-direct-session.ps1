[CmdletBinding()]
param(
  [string] $Steam = 'C:\Program Files (x86)\Steam\steam.exe',
  [string] $Valheim = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim.exe',
  [string] $Server = '8.231.129.249:2456',
  [string] $Gateway = 'http://8.231.129.249:42317',
  [int] $ReadyTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
if (!(Test-Path $Steam)) { throw "Steam executable not found at $Steam" }
if (!(Test-Path $Valheim)) { throw "Valheim executable not found at $Valheim" }

$health = Invoke-RestMethod -Uri "$Gateway/health" -TimeoutSec 10
if ($health.status -ne 'ok') { throw "Direct Lumberjacks Gateway is not healthy at $Gateway" }

$running = Get-Process valheim -ErrorAction SilentlyContinue
if (!$running) {
  Start-Process $Steam -ArgumentList @('-applaunch', '892970', '+connect', $Server)
  $steamDeadline = (Get-Date).AddSeconds([Math]::Min(10, $ReadyTimeoutSeconds))
  do {
    Start-Sleep -Milliseconds 500
    $running = Get-Process valheim -ErrorAction SilentlyContinue
  } while (!$running -and (Get-Date) -lt $steamDeadline)
}

if (!$running) {
  Start-Process $Valheim -ArgumentList @('+connect', $Server) -WorkingDirectory (Split-Path $Valheim)
  $directDeadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
  do {
    Start-Sleep -Milliseconds 500
    $running = Get-Process valheim -ErrorAction SilentlyContinue
  } while (!$running -and (Get-Date) -lt $directDeadline)
}

if (!$running) { throw "Valheim did not start within $ReadyTimeoutSeconds seconds" }
[pscustomobject]@{
  status = 'started'
  process_id = @($running)[0].Id
  server = $Server
  gateway = $Gateway
  tunnel_required = $false
}
