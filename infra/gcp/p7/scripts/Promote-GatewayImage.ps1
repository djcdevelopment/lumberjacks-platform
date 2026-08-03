#Requires -Version 5.1
<#
.SYNOPSIS
  Promote a locally verified Gateway Docker image to P7 without building source on the VM.

.DESCRIPTION
  Saves an existing local Gateway image to an OCI archive, hashes it, uploads it to P7,
  verifies the remote hash, docker-loads it, re-pins LUMBERJACKS_GATEWAY_IMAGE in
  /etc/comfy-p7/environment, and restarts only the gateway service with --no-build.

  This is the small alpha promotion lane for Gateway-only UI/API changes. It deliberately
  avoids copying source into /opt and avoids docker compose build on the VM.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidatePattern('^lumberjacks-gateway:m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')]
  [string] $Image,

  [Parameter(Mandatory)]
  [ValidatePattern('^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')]
  [string] $AdmittedModRelease,

  [string] $SshTarget = 'comfy-p7',
  [string] $ComposeRoot = '/opt/comfy/infra/gcp/p7',
  [string] $EnvironmentFile = '/etc/comfy-p7/environment',
  [string] $GatewayContainer = 'comfy-lumberjacks-p7-gateway-1',
  [switch] $DeferFailureRecoveryToPair,
  [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$verifier = Join-Path $scriptRoot 'Test-GatewayImageRelease.ps1'
if (!(Test-Path -LiteralPath $verifier -PathType Leaf)) { throw "missing verifier: $verifier" }

function Invoke-NativeJob([string] $Exe, [string[]] $Arguments, [int] $TimeoutSeconds, [int] $Retries = 2) {
  $serializedArguments = ConvertTo-Json -InputObject @($Arguments) -Compress
  for ($attempt = 1; $attempt -le $Retries; $attempt++) {
    $job = Start-Job -ScriptBlock {
      param($exe, $exeArgsJson)
      $exeArgs = [object[]](ConvertFrom-Json $exeArgsJson)
      $out = & $exe @exeArgs 2>&1
      [pscustomobject]@{ Output = (@($out) -join "`n"); Code = $LASTEXITCODE }
    } -ArgumentList @($Exe, $serializedArguments)
    $done = Wait-Job $job -Timeout $TimeoutSeconds
    if ($done) {
      $result = Receive-Job $job
      Remove-Job $job -Force
      return $result
    }
    Stop-Job $job
    Remove-Job $job -Force
  }
  return $null
}

function Invoke-Remote([string] $Script, [string] $Label, [int] $TimeoutSeconds = 600) {
  $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n")))
  $remote = "echo $encoded | base64 -d | bash"
  $result = Invoke-NativeJob 'ssh' @('-n', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=4', $SshTarget, $remote) $TimeoutSeconds 2
  if ($null -eq $result) { throw "$Label timed out after $TimeoutSeconds seconds" }
  if ($result.Code -ne 0) { throw "$Label failed with exit $($result.Code)`n$($result.Output)" }
  return [string]$result.Output
}

Write-Host "verifying local image release identity..." -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier -Image $Image -ExpectedRelease $AdmittedModRelease
if ($LASTEXITCODE -ne 0) { throw "local image identity verification failed: $Image" }

$localImageId = (& docker image inspect --format '{{.Id}}' $Image | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($localImageId)) { throw "local image not found: $Image" }
$localImageId = $localImageId.Trim()

if ($DryRun) {
  [pscustomobject]@{
    status = 'dry_run'
    image = $Image
    image_id = $localImageId
    admitted_mod_release = $AdmittedModRelease
    target = $SshTarget
    compose_root = $ComposeRoot
    environment_file = $EnvironmentFile
    defer_failure_recovery_to_pair = [bool]$DeferFailureRecoveryToPair
    action = 'would docker save, sha256 verify, docker load, re-pin LUMBERJACKS_GATEWAY_IMAGE, up -d --no-build --no-deps gateway'
  }
  return
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$releaseId = $Image.Split(':')[-1]
$deferFailureRecovery =
  $DeferFailureRecoveryToPair.IsPresent.ToString().ToLowerInvariant()
$archiveName = "gateway-image-$($Image.Split(':')[-1])-$stamp.oci.tar"
$localArchive = Join-Path ([IO.Path]::GetTempPath()) $archiveName
$remoteArchive = "/tmp/$archiveName"

try {
  Write-Host "saving local image archive..." -ForegroundColor Cyan
  & docker save --output $localArchive $Image
  if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $localArchive -PathType Leaf)) {
    throw "docker save failed for $Image"
  }
  $archiveHash = (Get-FileHash -LiteralPath $localArchive -Algorithm SHA256).Hash.ToLowerInvariant()
  $archiveSize = (Get-Item -LiteralPath $localArchive).Length

  Write-Host "uploading image archive to $SshTarget..." -ForegroundColor Cyan
  $upload = Invoke-NativeJob 'scp' @('-o', 'BatchMode=yes', '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=8', $localArchive, "${SshTarget}:$remoteArchive") 1800 2
  if ($null -eq $upload) { throw 'image archive upload timed out' }
  if ($upload.Code -ne 0) { throw "image archive upload failed`n$($upload.Output)" }

  $remoteScript = @"
set -euo pipefail
image='$Image'
release_id='$releaseId'
expected_image_id='$localImageId'
archive='$remoteArchive'
expected_archive_hash='$archiveHash'
compose_root='$ComposeRoot'
environment_file='$EnvironmentFile'
gateway_container='$GatewayContainer'
defer_failure_recovery='$deferFailureRecovery'
stamp='$stamp'
backup_root="/mnt/comfy-p7/backups/gateway-image-promote/`$stamp"
backup_file="`$backup_root/environment"
old_ref="`$(sudo grep -E '^LUMBERJACKS_GATEWAY_IMAGE=' "`$environment_file" | tail -n 1 | cut -d= -f2- || true)"
sudo install -d -m 0750 "`$backup_root"
sudo cp -a "`$environment_file" "`$backup_file"
rollback() {
  status="`$?"
  if test "`$status" != "0"; then
    if test -f "`$backup_file"; then
      sudo cp -a "`$backup_file" "`$environment_file"
      cd "`$compose_root"
      if test "`$defer_failure_recovery" != true && test -n "`$old_ref"; then
        sudo docker compose --env-file "`$environment_file" up -d --no-build --no-deps gateway >/dev/null 2>&1 || true
      fi
    fi
  fi
  exit "`$status"
}
trap rollback EXIT

actual_archive_hash="`$(sha256sum "`$archive" | awk '{print `$1}')"
test "`$actual_archive_hash" = "`$expected_archive_hash"
sudo docker load --input "`$archive" >/dev/null
remote_image_id="`$(sudo docker image inspect --format '{{.Id}}' "`$image")"
test "`$remote_image_id" = "`$expected_image_id"

tmp_env="`$(mktemp)"
sudo cp -a "`$environment_file" "`$tmp_env"
sudo sed -i '/^LUMBERJACKS_GATEWAY_IMAGE=/d;/^LUMBERJACKS_VERSION=/d' "`$tmp_env"
printf '%s\n' "LUMBERJACKS_GATEWAY_IMAGE=`$image" | sudo tee -a "`$tmp_env" >/dev/null
printf '%s\n' "LUMBERJACKS_VERSION=`$release_id" | sudo tee -a "`$tmp_env" >/dev/null
sudo install -m 0600 -o root -g root "`$tmp_env" "`$environment_file"
sudo rm -f "`$tmp_env"
pin_count="`$(sudo grep -c '^LUMBERJACKS_GATEWAY_IMAGE=' "`$environment_file")"
test "`$pin_count" = "1"
version_count="`$(sudo grep -c '^LUMBERJACKS_VERSION=' "`$environment_file")"
test "`$version_count" = "1"

cd "`$compose_root"
sudo docker compose --env-file "`$environment_file" up -d --no-build --no-deps gateway
attempt=0
until curl --fail --silent http://127.0.0.1:4000/health | grep -q '"status":"ok"'; do
  attempt="`$((attempt + 1))"
  if test "`$attempt" -ge 60; then
    sudo docker logs --tail 100 "`$gateway_container" >&2 || true
    exit 1
  fi
  sleep 1
done
running_ref="`$(sudo docker inspect "`$gateway_container" --format '{{.Config.Image}}')"
running_id="`$(sudo docker inspect "`$gateway_container" --format '{{.Image}}')"
test "`$running_ref" = "`$image"
test "`$running_id" = "`$expected_image_id"
rm -f "`$archive"
trap - EXIT
printf 'status=promoted\n'
printf 'image=%s\n' "`$image"
printf 'image_id=%s\n' "`$running_id"
printf 'old_ref=%s\n' "`$old_ref"
printf 'lumberjacks_version=%s\n' "`$release_id"
printf 'environment_backup=%s\n' "`$backup_file"
printf 'archive_sha256=%s\n' "`$actual_archive_hash"
"@

  Write-Host "promoting image on P7..." -ForegroundColor Cyan
  $output = Invoke-Remote $remoteScript 'remote gateway image promotion' 900
  Write-Output ($output.Trim())

  [pscustomobject]@{
    status = 'promoted'
    image = $Image
    image_id = $localImageId
    archive_sha256 = $archiveHash
    archive_size_bytes = $archiveSize
    target = $SshTarget
    environment_file = $EnvironmentFile
  }
}
finally {
  if (Test-Path -LiteralPath $localArchive -PathType Leaf) {
    Remove-Item -LiteralPath $localArchive -Force -ErrorAction SilentlyContinue
  }
}
