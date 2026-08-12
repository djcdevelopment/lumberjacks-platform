[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $ManifestPath,
  [Parameter(Mandatory = $true)]
  [string] $OutputRoot,
  [Parameter(Mandatory = $true)]
  [Alias('BaselineRepo')]
  [string] $PlatformRepo,
  # $ModDllPath is already artifact-mode -- it is always an explicit path, hash-checked below
  # against the manifest, never built here. The alias only gives it the same -ModArtifact name
  # the other p7 scripts use.
  [Parameter(Mandatory = $true)]
  [Alias('ModArtifact')]
  [string] $ModDllPath,
  [Parameter(Mandatory = $true)]
  [string] $GatewayImage,
  [Parameter(Mandatory = $true)]
  [string] $EventlogImage,
  [Parameter(Mandatory = $true)]
  [string] $ProgressionImage,
  [Parameter(Mandatory = $true)]
  [string] $OperatorApiImage,
  [string] $ModSourceRevision = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) { throw "Release bundle build failed: $Message" }
function Invoke-Git([string] $Root, [string[]] $GitArguments) {
  $value = & git -C $Root @GitArguments
  if ($LASTEXITCODE -ne 0) { Fail "git $($GitArguments -join ' ') failed in $Root" }
  return (($value -join "`n").Trim())
}
function Hash([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

if (!(Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { Fail "manifest missing: $ManifestPath" }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseId = [string]$manifest.release_id
if ([string]::IsNullOrWhiteSpace($releaseId)) { Fail 'manifest release_id missing' }
$target = (Resolve-Path -LiteralPath $OutputRoot -ErrorAction SilentlyContinue)
if ($target) { Fail "refusing to overwrite existing bundle: $OutputRoot" }
$platformFull = (Resolve-Path -LiteralPath $PlatformRepo).Path
$manifestFull = (Resolve-Path -LiteralPath $ManifestPath).Path

$dirty = Invoke-Git $platformFull @('status', '--porcelain', '--untracked-files=all')
if ($dirty) { Fail 'platform checkout is dirty' }
$recordedPlatformCommit = if ($manifest.source.platform_commit) { $manifest.source.platform_commit } else { $manifest.source.baseline_commit }
if ((Invoke-Git $platformFull @('rev-parse', 'HEAD')) -ne $recordedPlatformCommit) { Fail 'platform HEAD does not match the manifest' }
if ($ModSourceRevision -and $ModSourceRevision -notmatch '^[0-9a-f]{40}$') { Fail 'ModSourceRevision must be a full commit SHA' }

$expectedMod = ([string]$manifest.mod.clean_build_sha256).ToLowerInvariant()
if ((Hash $ModDllPath) -ne $expectedMod) { Fail 'mod DLL does not match manifest' }

# gateway keeps its own check (above the loop) because it is the one artifact with a manifest field
# ($manifest.gateway) with that exact name from before the other four existed; folding it into the
# loop below would just rename gateway.image_id without changing what it checks.
$image = docker image inspect $GatewayImage | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$image) { Fail "Gateway image is unavailable: $GatewayImage" }
$imageId = [string]$image[0].Id
if ($imageId -ne [string]$manifest.gateway.image_id) { Fail 'Gateway image does not match manifest' }

# eventlog/progression/operatorapi: build+hash+pin, no admission check - see New-ReleaseCut.ps1 §4.
$otherServices = [ordered]@{
  eventlog     = $EventlogImage
  progression  = $ProgressionImage
  operatorapi  = $OperatorApiImage
}
$otherImageIds = @{}
foreach ($svc in $otherServices.Keys) {
  $svcImage = $otherServices[$svc]
  $svcInspect = docker image inspect $svcImage | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or !$svcInspect) { Fail "$svc image is unavailable: $svcImage" }
  $svcImageId = [string]$svcInspect[0].Id
  $manifestImageId = [string]$manifest.$svc.image_id
  if ($svcImageId -ne $manifestImageId) { Fail "$svc image does not match manifest" }
  $otherImageIds[$svc] = $svcImageId
}

New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$bundleFull = (Resolve-Path -LiteralPath $OutputRoot).Path
New-Item -ItemType Directory -Path (Join-Path $bundleFull 'mod'), (Join-Path $bundleFull 'gateway'), (Join-Path $bundleFull 'eventlog'), (Join-Path $bundleFull 'progression'), (Join-Path $bundleFull 'operatorapi'), (Join-Path $bundleFull 'source') | Out-Null
Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $bundleFull 'manifest.json')
Copy-Item -LiteralPath $ModDllPath -Destination (Join-Path $bundleFull 'mod/ComfyNetworkSense.dll')
Copy-Item -LiteralPath (Join-Path $platformFull 'Lumberjacks/Dockerfile') -Destination (Join-Path $bundleFull 'source/Dockerfile')
Copy-Item -LiteralPath (Join-Path $platformFull 'Lumberjacks/Directory.Build.props') -Destination (Join-Path $bundleFull 'source/Directory.Build.props')
Copy-Item -LiteralPath (Join-Path $platformFull 'Lumberjacks/Directory.Packages.props') -Destination (Join-Path $bundleFull 'source/Directory.Packages.props')
docker save --output (Join-Path $bundleFull 'gateway/gateway.oci.tar') $GatewayImage
if ($LASTEXITCODE -ne 0) { Fail 'docker save failed (gateway)' }
foreach ($svc in $otherServices.Keys) {
  docker save --output (Join-Path $bundleFull "$svc/$svc.oci.tar") $otherServices[$svc]
  if ($LASTEXITCODE -ne 0) { Fail "docker save failed ($svc)" }
}

$fileEntries = @()
Get-ChildItem -LiteralPath $bundleFull -File -Recurse | Where-Object { $_.Name -ne 'bundle-index.json' } | ForEach-Object {
  $relative = $_.FullName.Substring($bundleFull.Length + 1).Replace('\', '/')
  $fileEntries += [ordered]@{ path = $relative; sha256 = Hash $_.FullName; bytes = $_.Length }
}
$index = [ordered]@{
  schema = 'comfy-p7-bundle/v1'
  release_id = $releaseId
  mod_source_revision = $ModSourceRevision
  created_utc = (Get-Date).ToUniversalTime().ToString('o')
  manifest_sha256 = Hash (Join-Path $bundleFull 'manifest.json')
  files = $fileEntries
}
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundleFull 'bundle-index.json') -Encoding UTF8
Write-Output "Bundle created: $bundleFull"
