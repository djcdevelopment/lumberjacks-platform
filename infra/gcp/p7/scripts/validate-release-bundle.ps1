[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $ManifestPath,
  [string] $BundleRoot
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
  throw "Release bundle validation failed: $Message"
}

function Require-Property($Object, [string] $Name) {
  $property = $Object.PSObject.Properties[$Name]
  if ($null -eq $property -or $null -eq $property.Value -or ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace([string] $property.Value))) {
    Fail "missing $Name"
  }
  return $property.Value
}

function Require-Hash([string] $Value, [string] $Name) {
  if ($Value -notmatch '^[0-9a-fA-F]{64}$') { Fail "$Name is not a SHA-256" }
  return $Value.ToLowerInvariant()
}

if (!(Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { Fail "manifest does not exist: $ManifestPath" }
$manifestFullPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$raw = Get-Content -LiteralPath $manifestFullPath -Raw -Encoding UTF8

# This file is public-facing release metadata. Reject identities and credential-shaped values
# before parsing so a future schema field cannot accidentally bypass the policy.
foreach ($pattern in @(
  '\b7656119\d{10}\b',
  '(?i)(?:client[_ -]?access[_ -]?key|admin[_ -]?key|bearer|invite[_ -]?token|password)\s*[=:]\s*[A-Za-z0-9+/_=-]{12,}'
)) {
  if ($raw -match $pattern) { Fail "public manifest contains a prohibited identity or credential-shaped value" }
}

$manifest = $raw | ConvertFrom-Json
if ((Require-Property $manifest 'schema') -notmatch '^comfy-p7-release/v2(?:-candidate)?$') { Fail 'unsupported schema' }
$releaseId = Require-Property $manifest 'release_id'
if ($releaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$') { Fail 'invalid release_id' }

$source = Require-Property $manifest 'source'
foreach ($name in @('lumberjacks_commit', 'comfy_commit')) {
  $commit = Require-Property $source $name
  if ($commit -notmatch '^[0-9a-fA-F]{40}$') { Fail "$name is not a full commit id" }
}
if ($source.lumberjacks_clean -ne $true -or $source.comfy_clean -ne $true) { Fail 'source checkouts were not clean' }

$mod = Require-Property $manifest 'mod'
Require-Hash (Require-Property $mod 'clean_build_sha256') 'mod.clean_build_sha256' | Out-Null
Require-Property $mod 'version' | Out-Null
$gateway = Require-Property $manifest 'gateway'
if ((Require-Property $gateway 'image_id') -notmatch '^sha256:[0-9a-fA-F]{64}$') { Fail 'gateway.image_id is not an image digest' }
Require-Property $gateway 'build_command' | Out-Null
$toolchain = Require-Property $manifest 'toolchain'
Require-Property $toolchain 'mod_sdk' | Out-Null
Require-Property $toolchain 'gateway_sdk_digest' | Out-Null
Require-Property $toolchain 'gateway_aspnet_digest' | Out-Null

if ($BundleRoot) {
  if (!(Test-Path -LiteralPath $BundleRoot -PathType Container)) { Fail "bundle root does not exist: $BundleRoot" }
  $bundleFullPath = (Resolve-Path -LiteralPath $BundleRoot).Path
  $indexPath = Join-Path $bundleFullPath 'bundle-index.json'
  if (!(Test-Path -LiteralPath $indexPath -PathType Leaf)) { Fail 'bundle-index.json is missing' }
  $index = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if ((Require-Property $index 'release_id') -ne $releaseId) { Fail 'bundle release_id does not match manifest' }
  $manifestHash = (Get-FileHash -LiteralPath $manifestFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ((Require-Hash (Require-Property $index 'manifest_sha256') 'bundle manifest_sha256') -ne $manifestHash) { Fail 'bundle manifest hash mismatch' }
  $files = Require-Property $index 'files'
  foreach ($file in $files) {
    $relative = [string](Require-Property $file 'path')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) { Fail "unsafe bundle path: $relative" }
    $path = Join-Path $bundleFullPath $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { Fail "bundle file is missing: $relative" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = Require-Hash (Require-Property $file 'sha256') "bundle file $relative"
    if ($actual -ne $expected) { Fail "bundle file hash mismatch: $relative" }
  }
}

[pscustomobject]@{
  status = 'valid'
  release_id = $releaseId
  schema = $manifest.schema
  bundle_root = if ($BundleRoot) { (Resolve-Path -LiteralPath $BundleRoot).Path } else { $null }
  mod_sha256 = $mod.clean_build_sha256.ToLowerInvariant()
  gateway_image = $gateway.image_id
}
