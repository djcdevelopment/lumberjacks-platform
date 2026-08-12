[CmdletBinding()]
param(
  [string] $SshTarget = 'comfy-p7',
  [string] $ManifestPath = (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))) 'fieldlab\runs\releases\p7-primary-v1-20260715.json'),
  # $LocalDll already is artifact-mode (an explicit path, defaulting to the local install); the
  # alias only gives it the same -ModArtifact name the other p7 scripts use.
  [Alias('ModArtifact')]
  [string] $LocalDll = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\ComfyNetworkSense.dll',
  [string] $LocalConfig = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg',
  # Bypasses the network/mod/ComfyNetworkSense source-tree pin below (the `git -C $sourceRoot`
  # read) when a caller already knows the source revision an artifact was built from -- e.g. a
  # networksense release manifest, once that tree is no longer checked out alongside this
  # script. Omitted (empty, the default), behavior is unchanged: read HEAD from the local tree.
  [string] $ModSourceRevision = ''
)

$ErrorActionPreference = 'Stop'
function Hash-File([string] $Path) {
  if (!(Test-Path -LiteralPath $Path)) { return $null }
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Repo-relative, so the recorded source revision is this checkout's, not a
# retired pre-cutover one that still happens to exist on disk.
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$sourceRoot = Join-Path $repoRoot 'network\mod\ComfyNetworkSense'
$sourceRevision = if (-not [string]::IsNullOrWhiteSpace($ModSourceRevision)) {
  $ModSourceRevision
} else {
  (& git -C $sourceRoot rev-parse HEAD 2>$null | Out-String).Trim()
}
$localAssembly = [Reflection.AssemblyName]::GetAssemblyName($LocalDll)
$remoteHashes = ssh $SshTarget @"
set -eu
printf 'runtime '; sudo docker exec comfy-lumberjacks-p7-valheim-server-1 sha256sum /opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll
printf 'cold_start '; sudo sha256sum /mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll
printf 'image '; sudo docker image inspect comfy-lumberjacks-p7-gateway:latest --format '{{.Id}}'
printf 'version '; sudo grep -E '^COMFY_NETWORKSENSE_VERSION=' /etc/comfy-p7/environment
printf 'cutover '; sudo grep -E '^COMFY_LUMBERJACKS_(CUTOVER_MODE|ENROLLMENT_MANIFEST_ID)=' /etc/comfy-p7/environment
"@
$text = ($remoteHashes -join "`n")
function Extract([string] $Prefix) {
  $line = @($text -split "`n" | Where-Object { $_ -like "$Prefix*" } | Select-Object -First 1)
  if ($line.Count -eq 0) { return $null }
  return ($line[0].Substring($Prefix.Length).Trim() -split '\s+')[0]
}

$manifest = [ordered]@{
  schema = 'comfy-p7-release/v1'
  captured_utc = (Get-Date).ToUniversalTime().ToString('o')
  target = $SshTarget
  source_revision = $sourceRevision
  plugin = [ordered]@{
    version = $localAssembly.Version.ToString(3)
    local_sha256 = (Hash-File $LocalDll)
    runtime_sha256 = (Extract 'runtime')
    cold_start_sha256 = (Extract 'cold_start')
  }
  local_config_sha256 = (Hash-File $LocalConfig)
  gateway_image_digest = (Extract 'image')
  remote_networksense_version = (Extract 'version')
  cutover = [ordered]@{
    mode = (Extract 'cutover' | Out-String).Trim()
    manifest = 'p7-primary-v1'
  }
}

$dir = Split-Path -Parent $ManifestPath
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
Write-Output $ManifestPath
