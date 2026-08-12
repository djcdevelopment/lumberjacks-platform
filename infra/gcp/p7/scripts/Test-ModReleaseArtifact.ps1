#Requires -Version 5.1
<#
.SYNOPSIS
Verifies one downloaded NetworkSense release bundle without contacting another system.

.DESCRIPTION
Reads only the four files in ArtifactDirectory. The producer contract is
comfy-mod-release-manifest/v1 plus comfy-mod-boundary-receipt/v1 and a GNU-style
SHA256SUMS file. Optional expected values turn the producer attestations into
consumer-side pins.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string] $ArtifactDirectory,
  [string] $ExpectedTag = '',
  [string] $ExpectedSourceRevision = '',
  [string] $ExpectedReleaseId = '',
  [ValidateSet('', 'public', 'interim')][string] $ExpectedDependencyProfile = ''
)

$ErrorActionPreference = 'Stop'

function Require-Property {
  param(
    [Parameter(Mandatory = $true)] $Value,
    [Parameter(Mandatory = $true)][string] $Name,
    [Parameter(Mandatory = $true)][string] $Context
  )

  if ($null -eq $Value) { throw "$Context is null" }
  $property = $Value.PSObject.Properties[$Name]
  if ($null -eq $property) { throw "$Context is missing required property '$Name'" }
  return $property.Value
}

function Get-LowerSha256 {
  param([Parameter(Mandatory = $true)][string] $Path)
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StringSha256 {
  param([Parameter(Mandatory = $true)][string] $Value)

  $algorithm = [Security.Cryptography.SHA256]::Create()
  try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
  }
  finally { $algorithm.Dispose() }
}

function Assert-LowerSha256 {
  param([string] $Value, [string] $Context)
  if ($Value -notmatch '^[0-9a-f]{64}$') { throw "$Context is not a lowercase SHA-256" }
}

function Read-Checksums {
  param([Parameter(Mandatory = $true)][string] $Path)

  $result = @{}
  foreach ($line in [IO.File]::ReadAllLines($Path)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/]+)$') {
      throw "invalid SHA256SUMS row: $line"
    }
    $name = $Matches[2]
    if ($result.ContainsKey($name)) { throw "duplicate SHA256SUMS asset: $name" }
    $result[$name] = $Matches[1].ToLowerInvariant()
  }
  return $result
}

function Assert-FileEvidenceShape {
  param($Evidence, [string] $Context, [switch] $AllowZeroBytes)

  $relativePath = [string](Require-Property $Evidence 'path' $Context)
  $sha256 = [string](Require-Property $Evidence 'sha256' $Context)
  $bytes = [long](Require-Property $Evidence 'bytes' $Context)
  if ([string]::IsNullOrWhiteSpace($relativePath) -or
      [IO.Path]::IsPathRooted($relativePath) -or
      $relativePath.Contains('\') -or
      @($relativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
    throw "$Context path is not a safe repository-relative slash path: $relativePath"
  }
  Assert-LowerSha256 $sha256 "$Context.sha256"
  if (($AllowZeroBytes -and $bytes -lt 0) -or (-not $AllowZeroBytes -and $bytes -le 0)) {
    throw "$Context has an invalid byte count: $bytes"
  }
}

if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) {
  throw "release artifact directory does not exist: $ArtifactDirectory"
}
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$requiredNames = @(
  'ComfyNetworkSense.dll',
  'release-manifest.json',
  'boundary-receipt.json',
  'SHA256SUMS'
)
$actualItems = @(Get-ChildItem -LiteralPath $artifactRoot -Force)
$actualNames = @($actualItems | Select-Object -ExpandProperty Name)
$missing = @($requiredNames | Where-Object { $_ -notin $actualNames })
$unexpected = @($actualNames | Where-Object { $_ -notin $requiredNames })
if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or
    @($actualItems | Where-Object { $_.PSIsContainer }).Count -gt 0) {
  throw ('release bundle must contain exactly the four declared files; missing=[{0}] unexpected=[{1}]' -f
    ($missing -join ', '), ($unexpected -join ', '))
}

$dllPath = Join-Path $artifactRoot 'ComfyNetworkSense.dll'
$manifestPath = Join-Path $artifactRoot 'release-manifest.json'
$receiptPath = Join-Path $artifactRoot 'boundary-receipt.json'
$checksumsPath = Join-Path $artifactRoot 'SHA256SUMS'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json

if ([string](Require-Property $manifest 'schema' 'release manifest') -ne 'comfy-mod-release-manifest/v1') {
  throw 'release manifest schema is not comfy-mod-release-manifest/v1'
}
if ([string](Require-Property $receipt 'schema' 'boundary receipt') -ne 'comfy-mod-boundary-receipt/v1') {
  throw 'boundary receipt schema is not comfy-mod-boundary-receipt/v1'
}
if ([string](Require-Property $manifest 'repository' 'release manifest') -ne 'djcdevelopment/networksense' -or
    [string](Require-Property $receipt 'repository' 'boundary receipt') -ne 'djcdevelopment/networksense') {
  throw 'release repository identity is not djcdevelopment/networksense'
}

$tag = [string](Require-Property $manifest 'tag' 'release manifest')
$tagMatch = [regex]::Match($tag, '^mod-v(?<version>\d+\.\d+\.\d+)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')
if (-not $tagMatch.Success) { throw "invalid NetworkSense release tag: $tag" }
$version = $tagMatch.Groups['version'].Value
if ([string](Require-Property $receipt 'tag' 'boundary receipt') -ne $tag) {
  throw 'manifest and boundary receipt tags do not agree'
}
if ($ExpectedTag -and $tag -ne $ExpectedTag) {
  throw "release tag mismatch: expected=$ExpectedTag actual=$tag"
}

$sourceRevision = [string](Require-Property $manifest 'source_revision' 'release manifest')
if ($sourceRevision -notmatch '^[0-9a-f]{40}$') {
  throw 'release source_revision is not a lowercase 40-character commit id'
}
if ([string](Require-Property $receipt 'source_revision' 'boundary receipt') -ne $sourceRevision) {
  throw 'manifest and boundary receipt source revisions do not agree'
}
if ($ExpectedSourceRevision -and $sourceRevision -ne $ExpectedSourceRevision.ToLowerInvariant()) {
  throw "release source revision mismatch: expected=$ExpectedSourceRevision actual=$sourceRevision"
}
if (-not [bool](Require-Property $receipt 'source_clean' 'boundary receipt')) {
  throw 'boundary receipt does not attest a clean source checkout'
}

$plugin = Require-Property $manifest 'plugin' 'release manifest'
if ([string](Require-Property $plugin 'name' 'release manifest plugin') -ne 'ComfyNetworkSense' -or
    [string](Require-Property $plugin 'guid' 'release manifest plugin') -ne 'djcdevelopment.valheim.comfynetworksense') {
  throw 'release manifest plugin name or GUID is not the NetworkSense identity'
}
if ([string](Require-Property $plugin 'version' 'release manifest plugin') -ne $version -or
    [string](Require-Property $receipt 'plugin_version' 'boundary receipt') -ne $version) {
  throw 'release tag, manifest plugin version, and boundary receipt version do not agree'
}
$releaseId = [string](Require-Property $plugin 'baked_release_id' 'release manifest plugin')
if ($releaseId -notmatch '^m\d+-[a-z0-9][a-z0-9-]*-\d{8}-r\d+$') {
  throw "invalid baked NetworkSense release id: $releaseId"
}
if ([string](Require-Property $receipt 'baked_release_id' 'boundary receipt') -ne $releaseId) {
  throw 'manifest and boundary receipt baked release ids do not agree'
}
if ($ExpectedReleaseId -and $releaseId -ne $ExpectedReleaseId) {
  throw "baked release id mismatch: expected=$ExpectedReleaseId actual=$releaseId"
}

$artifact = Require-Property $manifest 'artifact' 'release manifest'
$receiptDll = Require-Property $receipt 'dll' 'boundary receipt'
if ([string](Require-Property $artifact 'name' 'release manifest artifact') -ne 'ComfyNetworkSense.dll' -or
    [string](Require-Property $receiptDll 'name' 'boundary receipt dll') -ne 'ComfyNetworkSense.dll') {
  throw 'release artifact name is not ComfyNetworkSense.dll'
}
$dllHash = Get-LowerSha256 $dllPath
$dllBytes = [long](Get-Item -LiteralPath $dllPath).Length
$manifestDllHash = [string](Require-Property $artifact 'sha256' 'release manifest artifact')
$receiptDllHash = [string](Require-Property $receiptDll 'sha256' 'boundary receipt dll')
Assert-LowerSha256 $manifestDllHash 'release manifest artifact.sha256'
Assert-LowerSha256 $receiptDllHash 'boundary receipt dll.sha256'
if ($manifestDllHash -ne $dllHash -or $receiptDllHash -ne $dllHash) {
  throw 'ComfyNetworkSense.dll SHA-256 mismatch'
}
if ([long](Require-Property $artifact 'bytes' 'release manifest artifact') -ne $dllBytes -or
    [long](Require-Property $receiptDll 'bytes' 'boundary receipt dll') -ne $dllBytes) {
  throw 'ComfyNetworkSense.dll byte-count mismatch'
}

. (Join-Path $PSScriptRoot 'lib\ReleaseIdentity.ps1')
$managed = Get-ManagedAssemblyIdentity -DllPath $dllPath
$metadataReleaseId = Get-AssemblyMetadataValue -DllPath $dllPath -Key 'LumberjacksModReleaseId'
$expectedAssemblyVersion = "$version.0"
if ($managed.AssemblyName -ne 'ComfyNetworkSense' -or
    $managed.AssemblyVersion -ne $expectedAssemblyVersion -or
    $managed.FileVersion -ne $version) {
  throw 'managed assembly name or version does not match the release tag'
}
if ($metadataReleaseId -ne $releaseId) {
  throw 'managed assembly baked LumberjacksModReleaseId does not match the release manifest'
}
if ([string](Require-Property $artifact 'assembly_version' 'release manifest artifact') -ne $managed.AssemblyVersion -or
    [string](Require-Property $artifact 'file_version' 'release manifest artifact') -ne $managed.FileVersion -or
    [string](Require-Property $receiptDll 'assembly_version' 'boundary receipt dll') -ne $managed.AssemblyVersion -or
    [string](Require-Property $receiptDll 'file_version' 'boundary receipt dll') -ne $managed.FileVersion -or
    [string](Require-Property $receiptDll 'module_version_id' 'boundary receipt dll') -ne $managed.ModuleVersionId) {
  throw 'recorded managed assembly evidence does not match ComfyNetworkSense.dll'
}

$source = Require-Property $receipt 'source' 'boundary receipt'
$sourceHash = [string](Require-Property $source 'sha256' 'boundary receipt source')
Assert-LowerSha256 $sourceHash 'boundary receipt source.sha256'
$sourceFiles = @(Require-Property $source 'files' 'boundary receipt source')
if ($sourceFiles.Count -eq 0) { throw 'boundary receipt source.files is empty' }
$sourcePaths = @()
foreach ($sourceFile in $sourceFiles) {
  Assert-FileEvidenceShape $sourceFile 'boundary receipt source file' -AllowZeroBytes
  $sourcePaths += [string]$sourceFile.path
}
if (@($sourcePaths | Select-Object -Unique).Count -ne $sourcePaths.Count) {
  throw 'boundary receipt source.files contains duplicate paths'
}
$sortedSourcePaths = @($sourcePaths | Sort-Object -Unique)
if (($sourcePaths -join "`n") -cne ($sortedSourcePaths -join "`n")) {
  throw 'boundary receipt source.files is not in canonical path order'
}
$sourceComposite = @($sourceFiles | ForEach-Object {
  '{0} {1} {2}' -f [string]$_.sha256, [long]$_.bytes, [string]$_.path
}) -join "`n"
if ((Get-StringSha256 ($sourceComposite + "`n")) -ne $sourceHash) {
  throw 'boundary receipt source digest does not match its source file evidence'
}

$manifestBuild = Require-Property $manifest 'build' 'release manifest'
$receiptBuild = Require-Property $receipt 'build' 'boundary receipt'
$profile = [string](Require-Property $receiptBuild 'dependency_profile' 'boundary receipt build')
if ($profile -notin @('public', 'interim')) { throw "invalid dependency profile: $profile" }
if ([string](Require-Property $manifestBuild 'dependency_profile' 'release manifest build') -ne $profile) {
  throw 'manifest and boundary receipt dependency profiles do not agree'
}
if ($ExpectedDependencyProfile -and $profile -ne $ExpectedDependencyProfile) {
  throw "dependency profile mismatch: expected=$ExpectedDependencyProfile actual=$profile"
}
if ([string](Require-Property $manifestBuild 'configuration' 'release manifest build') -ne 'Release' -or
    [string](Require-Property $receiptBuild 'configuration' 'boundary receipt build') -ne 'Release' -or
    -not [bool](Require-Property $receiptBuild 'copy_to_plugins_disabled' 'boundary receipt build')) {
  throw 'release bundle was not recorded as a copy-disabled Release build'
}
$livePluginBefore = [string](Require-Property $receiptBuild 'live_plugin_sha256_before' 'boundary receipt build')
$livePluginAfter = [string](Require-Property $receiptBuild 'live_plugin_sha256_after' 'boundary receipt build')
if ($livePluginBefore -ne $livePluginAfter) {
  throw 'boundary receipt says the live plugin changed during the copy-disabled build'
}
if ($livePluginBefore) { Assert-LowerSha256 $livePluginBefore 'boundary receipt build.live_plugin_sha256_before' }
$livePluginCheck = Require-Property (Require-Property $receipt 'checks' 'boundary receipt') `
  'live_plugin_unchanged' 'boundary receipt checks'
if (-not [bool]$livePluginCheck) {
  throw 'boundary receipt does not attest that the live plugin remained unchanged'
}
$sdk = [string](Require-Property $receiptBuild 'dotnet_sdk' 'boundary receipt build')
if ([string]::IsNullOrWhiteSpace($sdk) -or
    [string](Require-Property $manifestBuild 'dotnet_sdk' 'release manifest build') -ne $sdk) {
  throw 'manifest and boundary receipt SDK records do not agree'
}
$manifestConfig = Require-Property $manifestBuild 'nuget_config' 'release manifest build'
$receiptConfig = Require-Property $receiptBuild 'nuget_config' 'boundary receipt build'
$expectedConfigPath = if ($profile -eq 'public') { 'nuget.config' } else { 'nuget.interim.config' }
if ([string](Require-Property $manifestConfig 'path' 'release manifest NuGet config') -ne $expectedConfigPath -or
    [string](Require-Property $receiptConfig 'path' 'boundary receipt NuGet config') -ne $expectedConfigPath) {
  throw "dependency profile $profile must use $expectedConfigPath"
}
$configHash = [string](Require-Property $manifestConfig 'sha256' 'release manifest NuGet config')
Assert-LowerSha256 $configHash 'release manifest NuGet config.sha256'
if ([string](Require-Property $receiptConfig 'sha256' 'boundary receipt NuGet config') -ne $configHash -or
    [long](Require-Property $receiptConfig 'bytes' 'boundary receipt NuGet config') -le 0) {
  throw 'manifest and boundary receipt NuGet configuration evidence does not agree'
}

$packages = @(Require-Property $receiptBuild 'packages' 'boundary receipt build')
$expectedVersions = if ($profile -eq 'public') {
  @{ 'Comfy.Quest.Contracts' = '0.1.0'; 'Comfy.Transport.Contracts' = '0.1.0' }
} else {
  @{ 'Comfy.Quest.Contracts' = '0.1.0-local'; 'Comfy.Transport.Contracts' = '0.1.0-local' }
}
if ($packages.Count -ne $expectedVersions.Count) {
  throw 'boundary receipt must record exactly the two contract packages'
}
foreach ($packageId in $expectedVersions.Keys) {
  $matches = @($packages | Where-Object { [string]$_.id -eq $packageId })
  if ($matches.Count -ne 1 -or [string]$matches[0].version -ne $expectedVersions[$packageId]) {
    throw "invalid contract package version for $packageId"
  }
  Assert-FileEvidenceShape $matches[0] "boundary receipt package $packageId"
  $expectedPackageSource = if ($profile -eq 'public') { 'nuget-global-packages' } else { 'packages-local' }
  $expectedPackagePath = if ($profile -eq 'public') {
    '{0}/{1}/{0}.{1}.nupkg' -f $packageId.ToLowerInvariant(), $expectedVersions[$packageId]
  } else {
    'packages-local/{0}.{1}.nupkg' -f $packageId, $expectedVersions[$packageId]
  }
  if ([string](Require-Property $matches[0] 'source' "boundary receipt package $packageId") -ne $expectedPackageSource -or
      [string]$matches[0].path -cne $expectedPackagePath) {
    throw "invalid contract package source/path for $packageId"
  }
}

$expectedCompileInputs = @{
  'BepInEx/core/0Harmony.dll' = 'bepinex'
  'BepInEx/core/BepInEx.dll' = 'bepinex'
  'valheim_Data/Managed/assembly_valheim.dll' = 'valheim'
  'valheim_Data/Managed/assembly_utils.dll' = 'valheim'
  'valheim_Data/Managed/System.Runtime.Serialization.dll' = 'valheim'
  'valheim_Data/Managed/UnityEngine.dll' = 'unity'
  'valheim_Data/Managed/UnityEngine.CoreModule.dll' = 'unity'
  'valheim_Data/Managed/UnityEngine.IMGUIModule.dll' = 'unity'
  'valheim_Data/Managed/UnityEngine.JSONSerializeModule.dll' = 'unity'
  'valheim_Data/Managed/UnityEngine.InputLegacyModule.dll' = 'unity'
  'valheim_Data/Managed/UnityEngine.PhysicsModule.dll' = 'unity'
  'valheim_Data/Managed/UnityEngine.TextRenderingModule.dll' = 'unity'
}
$compileInputs = @(Require-Property $receiptBuild 'compile_inputs' 'boundary receipt build')
if ($compileInputs.Count -ne $expectedCompileInputs.Count) {
  throw 'boundary receipt must record exactly the 12 NetworkSense compile inputs'
}
foreach ($inputPath in $expectedCompileInputs.Keys) {
  $matches = @($compileInputs | Where-Object { [string]$_.path -eq $inputPath })
  if ($matches.Count -ne 1 -or [string]$matches[0].scope -ne $expectedCompileInputs[$inputPath]) {
    throw "invalid NetworkSense compile-input evidence: $inputPath"
  }
  Assert-FileEvidenceShape $matches[0] "boundary receipt compile input $inputPath"
}

$checks = Require-Property $receipt 'checks' 'boundary receipt'
foreach ($checkName in @(
  'tag_matches_plugin_version',
  'manifest_matches_plugin_version',
  'assembly_matches_source',
  'source_boundary_recorded',
  'live_plugin_unchanged')) {
  if (-not [bool](Require-Property $checks $checkName 'boundary receipt checks')) {
    throw "boundary receipt check did not pass: $checkName"
  }
}
if ([string](Require-Property $receipt 'result' 'boundary receipt') -ne 'pass') {
  throw 'boundary receipt result is not pass'
}

$receiptHash = Get-LowerSha256 $receiptPath
$boundaryReceipt = Require-Property $manifest 'boundary_receipt' 'release manifest'
if ([string](Require-Property $boundaryReceipt 'name' 'release manifest boundary_receipt') -ne 'boundary-receipt.json' -or
    [string](Require-Property $boundaryReceipt 'sha256' 'release manifest boundary_receipt') -ne $receiptHash) {
  throw 'release manifest boundary_receipt hash does not match boundary-receipt.json'
}

$checksums = Read-Checksums $checksumsPath
if ($checksums.Count -ne 3) { throw 'SHA256SUMS must contain exactly three rows' }
$expectedChecksums = @{
  'ComfyNetworkSense.dll' = $dllHash
  'release-manifest.json' = Get-LowerSha256 $manifestPath
  'boundary-receipt.json' = $receiptHash
}
foreach ($assetName in $expectedChecksums.Keys) {
  if (-not $checksums.ContainsKey($assetName) -or $checksums[$assetName] -ne $expectedChecksums[$assetName]) {
    throw "SHA256SUMS mismatch for $assetName"
  }
}

[pscustomobject]@{
  Schema = 'lumberjacks-mod-release-artifact-verification/v1'
  ArtifactDirectory = $artifactRoot
  ModArtifact = $dllPath
  ManifestPath = $manifestPath
  ChecksumsPath = $checksumsPath
  Repository = 'djcdevelopment/networksense'
  Tag = $tag
  SourceRevision = $sourceRevision
  ReleaseId = $releaseId
  Version = $version
  DependencyProfile = $profile
  Sha256 = $dllHash
  Bytes = $dllBytes
  AssemblyName = $managed.AssemblyName
  AssemblyVersion = $managed.AssemblyVersion
  FileVersion = $managed.FileVersion
  Verdict = 'passed'
}
