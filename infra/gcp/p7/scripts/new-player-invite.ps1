[CmdletBinding()]
param([string] $SshTarget = 'comfy-p7')

$ErrorActionPreference = 'Stop'
$command = 'set -eu; . /etc/comfy-p7/environment; curl --fail --silent --show-error -X POST -H X-Lumberjacks-Admin-Key:$LUMBERJACKS_ADMIN_KEY http://127.0.0.1:4000/api/v0/enrollment/invites'
$json = ssh $SshTarget "sudo bash -lc '$command'"
if ($LASTEXITCODE -ne 0) { throw 'Invite generation failed' }
$receipt = ($json -join "`n") | ConvertFrom-Json
[pscustomobject]@{ invite_url = $receipt.invite_url; expires_utc = $receipt.expires_utc }
