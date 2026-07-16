[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $BackupPath,
  [string] $SshTarget = 'comfy-p7',
  [string] $SourceRoot = '/opt/lumberjacks-ed83bd8',
  [string] $ComposeRoot = '/opt/comfy/infra/gcp/p7'
)

$ErrorActionPreference = 'Stop'
$remote = @"
set -eu
test -d '$BackupPath'
test -f '$ComposeRoot/docker-compose.yml'
sudo cp -a '$BackupPath/.' '$SourceRoot/'
cd '$ComposeRoot'
sudo docker compose build gateway
sudo docker compose up -d --no-deps gateway
curl --fail --silent http://127.0.0.1:4000/health | grep -q '\"status\":\"ok\"'
"@

ssh $SshTarget $remote
if ($LASTEXITCODE -ne 0) { throw "Gateway rollback failed for $BackupPath" }

[pscustomobject]@{
  status = 'restored_and_verified'
  target = $SshTarget
  backup_path = $BackupPath
  source_root = $SourceRoot
  compose_root = $ComposeRoot
}
