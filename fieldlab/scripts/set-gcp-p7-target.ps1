param(
  [Parameter(Mandatory = $true)]
  [string]$PublicIp,
  [string]$SshHost = "comfy-p7",
  [string]$Container = "comfy-lumberjacks-p7-valheim-server-1",
  [string]$GatewayLocalUrl = "http://127.0.0.1:14000"
)

$ErrorActionPreference = "Stop"

# Run this in the PowerShell session that will execute run-loopback-window.ps1.
# Configure the comfy-p7 SSH alias first so it reaches the VM through IAP.
$env:FIELDLAB_VALHEIM_SSH = $SshHost
$env:FIELDLAB_VALHEIM_CONTAINER = $Container
$env:FIELDLAB_VALHEIM_DOCKER = "sudo docker"
$env:COMFY_AM4_DOCKER = "sudo docker"
$env:FIELDLAB_VALHEIM_BEPINEX = "/mnt/comfy-p7/valheim/config/bepinex"
$env:FIELDLAB_VALHEIM_WORLD_DB = "/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.db"
$env:FIELDLAB_VALHEIM_WORLD_FWL = "/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.fwl"
$env:FIELDLAB_VALHEIM_ENDPOINT = "${PublicIp}:2456"
$env:FIELDLAB_LUMBERJACKS_URL = $GatewayLocalUrl.TrimEnd('/')
$env:FIELDLAB_LUMBERJACKS_SERVER_URL = "http://gateway:4000"

Write-Host "Fieldlab target: Valheim $env:FIELDLAB_VALHEIM_ENDPOINT; Lumberjacks $env:FIELDLAB_LUMBERJACKS_URL (private OMEN tunnel)" -ForegroundColor Green
