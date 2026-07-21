[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $BackupPath,
  [string] $SshTarget = 'comfy-p7',
  [string] $Container = 'comfy-lumberjacks-p7-valheim-server-1'
)

$ErrorActionPreference = 'Stop'
$runtimeDll = '/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll'
$fallbackDll = '/mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll'
$hostConfig = '/mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg'

$remote = @"
set -eu
test -f '$BackupPath/runtime.dll'
test -f '$BackupPath/fallback.dll'
sudo docker cp '$BackupPath/runtime.dll' '${Container}:$runtimeDll'
sudo install -o 1000 -g 1000 -m 0644 '$BackupPath/fallback.dll' '$fallbackDll'
if sudo test -f '$BackupPath/config.cfg'; then
  sudo install -o 1000 -g 1000 -m 0664 '$BackupPath/config.cfg' '$hostConfig'
fi
sudo docker exec '$Container' supervisorctl restart valheim-server
"@

ssh $SshTarget $remote
if ($LASTEXITCODE -ne 0) { throw "Rollback failed for $BackupPath" }

[pscustomobject]@{
  status = 'restored'
  target = $SshTarget
  backup_path = $BackupPath
  container = $Container
  note = 'Valheim restart required readiness/hash verification before primary traffic resumes.'
}
