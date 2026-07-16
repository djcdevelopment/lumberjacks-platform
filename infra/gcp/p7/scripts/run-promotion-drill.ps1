#Requires -Version 5.1
<#
.SYNOPSIS
  M0/A4 promotion drill: snapshot, no-build cold start, rollback, restore.

.DESCRIPTION
  Executes the A4 exit drill against the P7 VM using only prebuilt release
  artifacts. Nothing is compiled or docker-built on the VM. Default mode is
  plan-only: it validates the bundle, resolves every identity, writes
  drill-plan.json, and exits without touching the VM. Pass -Execute during the
  scheduled GCP mutation window to run the four remote phases:

    1 SNAPSHOT    stop valheim-server, hash+archive world/config state,
                  restart valheim-server, fetch the snapshot manifest.
    2 COLD-START  scp + docker load the bundle Gateway OCI archive, pin the
                  image in a compose override, up -d --no-build, verify
                  /health and the exact image ID; deploy the candidate mod
                  DLL and verify its SHA-256 at both runtime paths.
    3 ROLLBACK    re-pin the historical rollback image (already on the VM),
                  up -d --no-build, verify health + exact image ID; restore
                  the historical mod DLL and verify its SHA-256.
    4 RESTORE     re-pin the candidate image, verify again, leaving the
                  promoted release running.

  Every phase writes a JSON receipt under <BundleRoot>\drill\. Any identity or
  health mismatch stops the drill fail-closed; recovery is phase 3 (rollback),
  which restores the validated historical runtime.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $ManifestPath,
  [Parameter(Mandatory = $true)]
  [string] $BundleRoot,
  [string] $SshTarget = 'comfy-p7',
  [string] $ComposeRoot = '/opt/comfy/infra/gcp/p7',
  [string] $EnvironmentFile = '/etc/comfy-p7/environment',
  [string] $GatewayContainer = 'comfy-lumberjacks-p7-gateway-1',
  [string] $ValheimContainer = 'comfy-lumberjacks-p7-valheim-server-1',
  # Historical validated runtime identities (rollback artifacts).
  [string] $RollbackImageId = 'sha256:358f5e11e35b54367a83d4e52ea3d47c0346e62a82ed357c2ff403eafafcd0a2',
  [string] $RollbackModSha256 = 'b31697d2a0cbe47b86c32b33d19fb9445e21af0cfe51687cb5afe871a3d7d77b',
  # VM path holding the historical mod runtime.dll/fallback.dll backup pair.
  [string] $RollbackModBackupPath = '',
  [switch] $Execute
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Fail([string] $Message) { throw "Promotion drill failed: $Message" }
function Hash([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function NowUtc { return (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }

function Write-Receipt([string] $Name, $Body) {
  $receiptDir = Join-Path $BundleRoot 'drill'
  if (!(Test-Path -LiteralPath $receiptDir)) { New-Item -ItemType Directory -Path $receiptDir | Out-Null }
  $path = Join-Path $receiptDir $Name
  $Body | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
  Write-Output "receipt: $path"
}

function Invoke-Remote([string] $Script, [string] $Label) {
  # Long multi-line commands can be dropped during IAP proxy stdin teardown;
  # ship the transaction as a file, as deploy-gateway.ps1 does.
  $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')
  $remotePath = "/tmp/promotion-drill-$stamp.sh"
  $localPath = Join-Path ([IO.Path]::GetTempPath()) "promotion-drill-$stamp.sh"
  try {
    [IO.File]::WriteAllText($localPath, ($Script -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    $ErrorActionPreference = 'Continue'
    scp $localPath "${SshTarget}:$remotePath" 2>$null
    $scpExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($scpExit -ne 0) { Fail "$Label transaction upload failed" }
    Start-Sleep -Seconds 3
    $ErrorActionPreference = 'Continue'
    $output = ssh $SshTarget "bash '$remotePath'" 2>$null
    $sshExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($sshExit -ne 0) { Fail "$Label remote transaction exited $sshExit`n$(($output -join "`n"))" }
    return ($output -join "`n")
  }
  finally {
    if (Test-Path -LiteralPath $localPath) { Remove-Item -LiteralPath $localPath -Force }
    $ErrorActionPreference = 'Continue'
    ssh $SshTarget "rm -f '$remotePath'" 2>$null | Out-Null
    $ErrorActionPreference = 'Stop'
  }
}

function Set-GatewayImage([string] $ImageReference, [string] $ExpectedImageId, [string] $Label) {
  $overridePath = "$ComposeRoot/docker-compose.promotion.yml"
  $script = @"
set -eu
cd '$ComposeRoot'
printf 'services:\n  gateway:\n    image: %s\n' '$ImageReference' | sudo tee '$overridePath' > /dev/null
sudo docker compose --env-file '$EnvironmentFile' -f docker-compose.yml -f docker-compose.promotion.yml up -d --no-build --no-deps gateway
attempt=0
until curl --fail --silent http://127.0.0.1:4000/health | grep -q '"status":"ok"'; do
  attempt=`$((attempt + 1))
  if test `$attempt -ge 60; then
    sudo docker logs --tail 100 '$GatewayContainer' >&2 || true
    exit 1
  fi
  sleep 1
done
printf 'image_id=%s\n' "`$(sudo docker inspect '$GatewayContainer' --format '{{.Image}}')"
printf 'health=%s\n' "`$(curl --fail --silent http://127.0.0.1:4000/health)"
"@
  $output = Invoke-Remote $script "$Label gateway switch"
  $imageMatch = [regex]::Match($output, '(?m)^image_id=(sha256:[0-9a-fA-F]{64})$')
  if (!$imageMatch.Success) { Fail "$Label did not report a running image id" }
  $actual = $imageMatch.Groups[1].Value
  if ($actual -ne $ExpectedImageId) { Fail "$Label image mismatch: running $actual, expected $ExpectedImageId" }
  if ($output -notmatch '"status":"ok"') { Fail "$Label health check did not return ok" }
  return $actual
}

# ---------------------------------------------------------------- phase 0: plan
$startedUtc = NowUtc
if (!(Test-Path -LiteralPath $BundleRoot -PathType Container)) { Fail "bundle root missing: $BundleRoot" }
$validation = & (Join-Path $scriptRoot 'validate-release-bundle.ps1') -ManifestPath $ManifestPath -BundleRoot $BundleRoot
if ($validation.status -ne 'valid') { Fail 'bundle validation did not return valid' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseId = [string]$manifest.release_id
$candidateImageId = [string]$manifest.gateway.image_id
$candidateModSha = ([string]$manifest.mod.clean_build_sha256).ToLowerInvariant()
$ociTar = Join-Path $BundleRoot 'gateway/gateway.oci.tar'
$modDll = Join-Path $BundleRoot 'mod/ComfyNetworkSense.dll'
if ((Hash $modDll) -ne $candidateModSha) { Fail 'bundle mod DLL does not match manifest' }
if ($candidateImageId -eq $RollbackImageId) { Fail 'candidate and rollback image ids are identical; drill would prove nothing' }
$candidateTag = "lumberjacks-gateway:drill-$releaseId"

$plan = [ordered]@{
  schema = 'comfy-p7-promotion-drill-plan/v1'
  release_id = $releaseId
  planned_utc = $startedUtc
  bundle_root = $BundleRoot
  bundle_validation = 'valid'
  candidate = [ordered]@{ image_id = $candidateImageId; image_tag = $candidateTag; mod_sha256 = $candidateModSha }
  rollback = [ordered]@{ image_id = $RollbackImageId; mod_sha256 = $RollbackModSha256; mod_backup_path = $RollbackModBackupPath }
  target = [ordered]@{ ssh = $SshTarget; compose_root = $ComposeRoot; gateway = $GatewayContainer; valheim = $ValheimContainer }
  phases = @('snapshot', 'cold_start', 'rollback', 'restore')
  execute = [bool]$Execute
  preconditions = @(
    'Scheduled GCP mutation window is open and the Valheim server is empty.',
    'RollbackModBackupPath points at an existing runtime.dll/fallback.dll backup pair on the VM.',
    'No volunteer or strict evidence window is active.'
  )
}
Write-Receipt 'drill-plan.json' $plan

if (-not $Execute) {
  Write-Output "Plan-only run complete for $releaseId. Re-run with -Execute inside the scheduled window."
  return
}
if ([string]::IsNullOrWhiteSpace($RollbackModBackupPath)) { Fail 'RollbackModBackupPath is required with -Execute' }

# ------------------------------------------------------------- phase 1: snapshot
$snapStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$snapRoot = "/mnt/comfy-p7/backups/promotion-drill/$snapStamp"
$snapshotScript = @"
set -eu
test -f '$RollbackModBackupPath/runtime.dll'
test -f '$RollbackModBackupPath/fallback.dll'
sudo docker stop '$ValheimContainer'
sudo install -d -m 0750 '$snapRoot'
sudo tar -czf '$snapRoot/valheim-config.tgz' -C /mnt/comfy-p7/valheim config
sudo cp -a '$EnvironmentFile' '$snapRoot/environment'
sudo cp -a '$ComposeRoot/docker-compose.yml' '$snapRoot/docker-compose.yml'
sudo docker start '$ValheimContainer'
cd '$snapRoot'
sudo sha256sum valheim-config.tgz environment docker-compose.yml | sudo tee '$snapRoot/snapshot.sha256'
printf 'snapshot_root=%s\n' '$snapRoot'
"@
$snapshotOutput = Invoke-Remote $snapshotScript 'snapshot'
Write-Receipt 'snapshot-manifest.json' ([ordered]@{
  schema = 'comfy-p7-promotion-drill-snapshot/v1'
  release_id = $releaseId
  captured_utc = NowUtc
  snapshot_root = $snapRoot
  note = 'Environment/config snapshots stay on the VM; this receipt records their hashes only.'
  remote_hashes = ($snapshotOutput -split "`n" | Where-Object { $_ -match '^[0-9a-f]{64}\s' })
})

# ----------------------------------------------------------- phase 2: cold start
$remoteTar = "/tmp/gateway-$releaseId.oci.tar"
$ErrorActionPreference = 'Continue'
scp $ociTar "${SshTarget}:$remoteTar" 2>$null
$scpExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($scpExit -ne 0) { Fail 'gateway OCI archive upload failed' }
Start-Sleep -Seconds 3

$loadScript = @"
set -eu
loaded=`$(sudo docker load --input '$remoteTar' | tail -n 1)
printf 'loaded=%s\n' "`$loaded"
image_id=`$(sudo docker images --no-trunc --quiet | grep -F '$candidateImageId' | head -n 1)
test -n "`$image_id"
sudo docker tag '$candidateImageId' '$candidateTag'
rm -f '$remoteTar'
printf 'tagged=%s\n' '$candidateTag'
"@
Invoke-Remote $loadScript 'image load' | Write-Verbose
$coldImage = Set-GatewayImage $candidateTag $candidateImageId 'cold start'

# Candidate mod deploy + hash verification at both runtime paths.
function Deploy-CandidateMod([string] $Label) {
  $remoteModDll = "/tmp/ComfyNetworkSense-$releaseId.dll"
  $ErrorActionPreference = 'Continue'
  scp $modDll "${SshTarget}:$remoteModDll" 2>$null
  $modScpExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  if ($modScpExit -ne 0) { Fail "$Label mod DLL upload failed" }
  Start-Sleep -Seconds 3
  $modDeployScript = @"
set -eu
sudo docker cp '$remoteModDll' '${ValheimContainer}:/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll'
sudo install -o 1000 -g 1000 -m 0644 '$remoteModDll' /mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll
sudo docker exec '$ValheimContainer' supervisorctl restart valheim-server
printf 'runtime %s\n' "`$(sudo docker exec '$ValheimContainer' sha256sum /opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll | cut -d' ' -f1)"
printf 'fallback %s\n' "`$(sudo sha256sum /mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll | cut -d' ' -f1)"
rm -f '$remoteModDll'
"@
  $modOutput = Invoke-Remote $modDeployScript "$Label mod deploy"
  foreach ($place in 'runtime', 'fallback') {
    $m = [regex]::Match($modOutput, "(?m)^$place ([0-9a-f]{64})$")
    if (!$m.Success -or $m.Groups[1].Value -ne $candidateModSha) { Fail "$Label mod hash mismatch at $place" }
  }
}
Deploy-CandidateMod 'cold start'
Write-Receipt 'cold-start-receipt.json' ([ordered]@{
  schema = 'comfy-p7-promotion-drill-cold-start/v1'
  release_id = $releaseId
  completed_utc = NowUtc
  gateway_image_id = $coldImage
  gateway_started_via = 'docker load + compose image pin, --no-build'
  mod_sha256 = $candidateModSha
  checks = @('bundle_validation=valid', 'gateway_health=ok', "gateway_image=$coldImage", 'mod_runtime_hash=match', 'mod_fallback_hash=match')
})

# ------------------------------------------------------------- phase 3: rollback
$rollbackImage = Set-GatewayImage $RollbackImageId $RollbackImageId 'rollback'
$modRollbackScript = @"
set -eu
sudo docker cp '$RollbackModBackupPath/runtime.dll' '${ValheimContainer}:/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll'
sudo install -o 1000 -g 1000 -m 0644 '$RollbackModBackupPath/fallback.dll' /mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll
sudo docker exec '$ValheimContainer' supervisorctl restart valheim-server
printf 'runtime %s\n' "`$(sudo docker exec '$ValheimContainer' sha256sum /opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll | cut -d' ' -f1)"
printf 'fallback %s\n' "`$(sudo sha256sum /mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll | cut -d' ' -f1)"
"@
$modRollbackOutput = Invoke-Remote $modRollbackScript 'mod rollback'
foreach ($place in 'runtime', 'fallback') {
  $m = [regex]::Match($modRollbackOutput, "(?m)^$place ([0-9a-f]{64})$")
  if (!$m.Success -or $m.Groups[1].Value -ne $RollbackModSha256.ToLowerInvariant()) { Fail "rollback mod hash mismatch at $place" }
}
Write-Receipt 'rollback-receipt.json' ([ordered]@{
  schema = 'comfy-p7-promotion-drill-rollback/v1'
  release_id = $releaseId
  completed_utc = NowUtc
  gateway_image_id = $rollbackImage
  mod_sha256 = $RollbackModSha256.ToLowerInvariant()
  checks = @('gateway_health=ok', "gateway_image=$rollbackImage", 'mod_runtime_hash=match', 'mod_fallback_hash=match', 'no_source_rebuild=true')
})

# -------------------------------------------------------------- phase 4: restore
$restoredImage = Set-GatewayImage $candidateTag $candidateImageId 'restore'
Deploy-CandidateMod 'restore'
Write-Receipt 'restored-state-receipt.json' ([ordered]@{
  schema = 'comfy-p7-promotion-drill-restore/v1'
  release_id = $releaseId
  completed_utc = NowUtc
  gateway_image_id = $restoredImage
  mod_sha256 = $candidateModSha
  checks = @('gateway_health=ok', "gateway_image=$restoredImage", 'mod_runtime_hash=match', 'mod_fallback_hash=match')
})

Write-Output "Promotion drill complete for $releaseId. Receipts under $(Join-Path $BundleRoot 'drill')."
