[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $ManifestPath,
  [Parameter(Mandatory = $true)]
  [string] $OutputRoot,
  [Parameter(Mandatory = $true)]
  [string] $BaselineRepo,
  [Parameter(Mandatory = $true)]
  [string] $ModDllPath,
  [Parameter(Mandatory = $true)]
  [string] $GatewayImage
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
$baselineFull = (Resolve-Path -LiteralPath $BaselineRepo).Path
$manifestFull = (Resolve-Path -LiteralPath $ManifestPath).Path

$dirty = Invoke-Git $baselineFull @('status', '--porcelain', '--untracked-files=all')
if ($dirty) { Fail 'baseline checkout is dirty' }
if ((Invoke-Git $baselineFull @('rev-parse', 'HEAD')) -ne $manifest.source.baseline_commit) { Fail 'baseline HEAD does not match the manifest' }

$expectedMod = ([string]$manifest.mod.clean_build_sha256).ToLowerInvariant()
if ((Hash $ModDllPath) -ne $expectedMod) { Fail 'mod DLL does not match manifest' }
$image = docker image inspect $GatewayImage | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$image) { Fail "Gateway image is unavailable: $GatewayImage" }
$imageId = [string]$image[0].Id
if ($imageId -ne [string]$manifest.gateway.image_id) { Fail 'Gateway image does not match manifest' }

New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$bundleFull = (Resolve-Path -LiteralPath $OutputRoot).Path
New-Item -ItemType Directory -Path (Join-Path $bundleFull 'mod'), (Join-Path $bundleFull 'gateway'), (Join-Path $bundleFull 'source') | Out-Null
Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $bundleFull 'manifest.json')
Copy-Item -LiteralPath $ModDllPath -Destination (Join-Path $bundleFull 'mod/ComfyNetworkSense.dll')
Copy-Item -LiteralPath (Join-Path $baselineFull 'Lumberjacks/Dockerfile') -Destination (Join-Path $bundleFull 'source/Dockerfile')
Copy-Item -LiteralPath (Join-Path $baselineFull 'Lumberjacks/Directory.Build.props') -Destination (Join-Path $bundleFull 'source/Directory.Build.props')
Copy-Item -LiteralPath (Join-Path $baselineFull 'Lumberjacks/Directory.Packages.props') -Destination (Join-Path $bundleFull 'source/Directory.Packages.props')
Copy-Item -LiteralPath (Join-Path $baselineFull 'network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj') -Destination (Join-Path $bundleFull 'source/ComfyNetworkSense.csproj')
docker save --output (Join-Path $bundleFull 'gateway/gateway.oci.tar') $GatewayImage
if ($LASTEXITCODE -ne 0) { Fail 'docker save failed' }

$fileEntries = @()
Get-ChildItem -LiteralPath $bundleFull -File -Recurse | Where-Object { $_.Name -ne 'bundle-index.json' } | ForEach-Object {
  $relative = $_.FullName.Substring($bundleFull.Length + 1).Replace('\', '/')
  $fileEntries += [ordered]@{ path = $relative; sha256 = Hash $_.FullName; bytes = $_.Length }
}
$index = [ordered]@{
  schema = 'comfy-p7-bundle/v1'
  release_id = $releaseId
  created_utc = (Get-Date).ToUniversalTime().ToString('o')
  manifest_sha256 = Hash (Join-Path $bundleFull 'manifest.json')
  files = $fileEntries
}
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundleFull 'bundle-index.json') -Encoding UTF8
Write-Output "Bundle created: $bundleFull"
