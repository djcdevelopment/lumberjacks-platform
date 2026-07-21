[CmdletBinding()]
param(
  [string] $SshTarget = 'comfy-p7',
  [string] $LocalRoot = 'C:\work\lumberjacks',
  [string] $SourceRoot = '/opt/lumberjacks-ed83bd8',
  [string] $ComposeRoot = '/opt/comfy/infra/gcp/p7',
  [string] $EnvironmentFile = '/etc/comfy-p7/environment',
  [string[]] $SourceFiles = @(
    'src/Game.Gateway/Valheim/ValheimZdoRedirectService.cs',
    'src/Game.Gateway/Valheim/ValheimTelemetryHeartbeatService.cs'
  ),
  [switch] $SkipCompaction
)

$ErrorActionPreference = 'Stop'
$localRootPath = [IO.Path]::GetFullPath($LocalRoot)
$backupStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$backupRoot = "/mnt/comfy-p7/backups/gateway/$backupStamp"
$remoteArchive = "/tmp/lumberjacks-gateway-$backupStamp.tgz"
$localArchive = Join-Path ([IO.Path]::GetTempPath()) "lumberjacks-gateway-$backupStamp.tgz"
$remoteScript = "/tmp/lumberjacks-gateway-$backupStamp.sh"
$localScript = Join-Path ([IO.Path]::GetTempPath()) "lumberjacks-gateway-$backupStamp.sh"

foreach ($relativePath in $SourceFiles) {
  if ($relativePath -notmatch '^[A-Za-z0-9._/-]+$' -or $relativePath.Contains('..')) {
    throw "Unsafe source path: $relativePath"
  }
  $localPath = Join-Path $localRootPath ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
  if (!(Test-Path -LiteralPath $localPath -PathType Leaf)) {
    throw "Gateway source file does not exist: $localPath"
  }
}

$expectedHashes = [ordered]@{}
foreach ($relativePath in $SourceFiles) {
  $localPath = Join-Path $localRootPath ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
  $expectedHashes[$relativePath] = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

try {
  & tar -C $localRootPath -czf $localArchive @SourceFiles
  if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $localArchive)) {
    throw 'Could not create the Gateway source deployment archive'
  }

  # gcloud's Windows IAP ProxyCommand can write a benign stdin ReadFile warning
  # to stderr after a successful native command. Do not let PowerShell promote
  # that stderr record to a terminating error; the native exit code remains the
  # deployment truth.
  $ErrorActionPreference = 'Continue'
  scp $localArchive "${SshTarget}:$remoteArchive"
  $scpExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  if ($scpExit -ne 0) { throw "SCP to $SshTarget failed" }
  # The Windows gcloud IAP ProxyCommand can outlive scp briefly; starting a new
  # proxy immediately races its stdin teardown and drops the first SSH command.
  Start-Sleep -Seconds 3

  $backupCommands = foreach ($relativePath in $SourceFiles) {
    $lastSlash = $relativePath.LastIndexOf('/')
    $parent = if ($lastSlash -gt 0) { $relativePath.Substring(0, $lastSlash) } else { '' }
    if ($parent) { "sudo install -d -m 0750 '$backupRoot/$parent'" }
    "sudo cp -a '$SourceRoot/$relativePath' '$backupRoot/$relativePath'"
  }
  $backupScript = $backupCommands -join "`n"
  $compactionScript = if ($SkipCompaction) {
    "printf '%s\n' 'compaction skipped'"
  } else {
    "curl --fail --silent --show-error -X POST http://127.0.0.1:4000/valheim/zdo-redirect/compact"
  }

  $remote = @"
set -eu
test -d '$SourceRoot'
test -f '$ComposeRoot/docker-compose.yml'
test -f '$EnvironmentFile'
sudo install -d -m 0750 '$backupRoot'
$backupScript
printf 'compaction='
$compactionScript
printf '\n'
sudo tar -xzf '$remoteArchive' -C '$SourceRoot'
cd '$ComposeRoot'
old_image=`$(sudo docker inspect comfy-lumberjacks-p7-gateway-1 --format '{{.Image}}' 2>/dev/null || true)
sudo docker compose --env-file '$EnvironmentFile' build gateway
sudo docker compose --env-file '$EnvironmentFile' up -d --no-deps gateway
attempt=0
until curl --fail --silent http://127.0.0.1:4000/health | grep -q '"status":"ok"'; do
  attempt=`$((attempt + 1))
  if test `$attempt -ge 60; then
    sudo docker logs --tail 100 comfy-lumberjacks-p7-gateway-1 >&2 || true
    exit 1
  fi
  sleep 1
done
new_image=`$(sudo docker inspect comfy-lumberjacks-p7-gateway-1 --format '{{.Image}}')
printf 'old_image=%s\nnew_image=%s\nbackup=%s\n' "`$old_image" "`$new_image" '$backupRoot'
"@

  # Upload the transaction as a file. Passing a long multi-line command through
  # Windows OpenSSH and gcloud's IAP ProxyCommand can drop the remote command
  # during proxy stdin teardown even when the preceding scp succeeded.
  $remote = $remote -replace "`r`n", "`n"
  Write-Verbose $remote
  [IO.File]::WriteAllText($localScript, $remote, [Text.UTF8Encoding]::new($false))
  $ErrorActionPreference = 'Continue'
  scp $localScript "${SshTarget}:$remoteScript" 2>$null
  $scriptScpExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  if ($scriptScpExit -ne 0) { throw "Transaction script SCP to $SshTarget failed" }
  Start-Sleep -Seconds 3
  $ErrorActionPreference = 'Continue'
  ssh $SshTarget "bash '$remoteScript'"
  $deploymentExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'

  $hashLines = foreach ($relativePath in $SourceFiles) {
    "printf '$relativePath '; sudo sha256sum '$SourceRoot/$relativePath'"
  }
  $ErrorActionPreference = 'Continue'
  $remoteHashesOutput = ssh $SshTarget (($hashLines + "printf 'health '; curl --fail --silent http://127.0.0.1:4000/health") -join '; ') 2>$null
  $hashExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  $remoteHashText = ($remoteHashesOutput -join "`n")
  if ($remoteHashText -notmatch 'health .*"status":"ok"') {
    throw "Gateway health verification failed (deploy exit $deploymentExit, verify exit $hashExit)"
  }
  foreach ($relativePath in $SourceFiles) {
    $match = [regex]::Match($remoteHashText, "(?m)^$([regex]::Escape($relativePath)) ([0-9a-fA-F]{64})")
    if (!$match.Success -or $match.Groups[1].Value.ToLowerInvariant() -ne $expectedHashes[$relativePath]) {
      throw "Deployed source hash mismatch for $relativePath"
    }
  }

  [pscustomobject]@{
    status = 'deployed_and_verified'
    target = $SshTarget
    backup_path = $backupRoot
    source_root = $SourceRoot
    source_hashes = $expectedHashes
  }
}
finally {
  if (Test-Path -LiteralPath $localArchive) {
    Remove-Item -LiteralPath $localArchive -Force
  }
  if (Test-Path -LiteralPath $localScript) {
    Remove-Item -LiteralPath $localScript -Force
  }
  $ErrorActionPreference = 'Continue'
  ssh $SshTarget "rm -f '$remoteArchive' '$remoteScript'" 2>$null | Out-Null
  $ErrorActionPreference = 'Stop'
}
