<#
.SYNOPSIS
Build a Gateway/service image set that admits one frozen networksense release.

.DESCRIPTION
The mod is an immutable cross-repository input. This command never edits or
builds mod source; it verifies the supplied DLL's baked release identity, then
builds and verifies the platform images against that identity.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $ReleaseId,
  [Parameter(Mandatory)][Alias('ModDll')][string] $ModArtifact,
  [Parameter(Mandatory)][string] $ExpectedSha256,
  [string] $LumberjacksRoot = '',
  [ValidateSet('candidate', 'final')][string] $ArtifactStage = 'final',
  [string] $ArtifactBoundaryReceiptPath = '',
  [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($LumberjacksRoot)) {
  $LumberjacksRoot = Join-Path $repoRoot 'Lumberjacks'
} else {
  $LumberjacksRoot = [IO.Path]::GetFullPath($LumberjacksRoot)
}
if ($ReleaseId -notmatch '^m\d+-[a-z0-9]+-\d{8}-r\d+$') {
  throw "ReleaseId '$ReleaseId' does not match <milestone>-<label>-<yyyymmdd>-r<n>."
}
if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
  throw 'ExpectedSha256 must be 64 hexadecimal characters.'
}
$modDll = (Resolve-Path -LiteralPath $ModArtifact -ErrorAction Stop).Path
$actualHash = (Get-FileHash -LiteralPath $modDll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
  throw "mod artifact hash mismatch: expected=$ExpectedSha256 actual=$actualHash"
}

$releaseIdentityLib = Join-Path $PSScriptRoot 'lib\ReleaseIdentity.ps1'
. $releaseIdentityLib
$modBaked = Get-AssemblyMetadataValue -DllPath $modDll -Key 'LumberjacksModReleaseId'
if ($modBaked -ne $ReleaseId) {
  throw "mod artifact release mismatch: expected=$ReleaseId actual=$modBaked"
}

if ($ArtifactBoundaryReceiptPath) {
  $receipt = [ordered]@{
    schema = 'lumberjacks-artifact-boundary/v1'
    stage = $ArtifactStage
    producer = 'djcdevelopment/networksense'
    release_id = $modBaked
    sha256 = $actualHash
    bytes = (Get-Item -LiteralPath $modDll).Length
  }
  $receiptDir = Split-Path -Parent $ArtifactBoundaryReceiptPath
  if ($receiptDir) { New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null }
  [IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($ArtifactBoundaryReceiptPath),
    ($receipt | ConvertTo-Json -Depth 5),
    (New-Object Text.UTF8Encoding($false)))
}

if ($WhatIf) {
  [pscustomobject]@{ ReleaseId = $ReleaseId; ModSha256 = $actualHash; Stage = $ArtifactStage; Verdict = 'would_build' }
  exit 0
}

$imageTag = "lumberjacks-gateway:$ReleaseId"
$platformRevision = [string](& git -C $repoRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $platformRevision.Trim() -notmatch '^[0-9a-f]{40}$') {
  throw 'Could not resolve the platform source revision for the image identity.'
}
$platformRevision = $platformRevision.Trim()
Push-Location $LumberjacksRoot
try {
  & docker build --target gateway -t $imageTag `
      --build-arg "LUMBERJACKS_EXPECTED_MOD_RELEASE=$ReleaseId" `
      --build-arg "LUMBERJACKS_SOURCE_REVISION=$platformRevision" `
      --build-arg 'LUMBERJACKS_REQUIRE_RELEASE=1' .
  if ($LASTEXITCODE -ne 0) { throw 'gateway image build failed' }

  foreach ($service in @('eventlog', 'progression', 'operatorapi')) {
    $tag = "lumberjacks-${service}:$ReleaseId"
    & docker build --target $service -t $tag .
    if ($LASTEXITCODE -ne 0) { throw "$service image build failed" }
  }
}
finally { Pop-Location }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    (Join-Path $PSScriptRoot 'Test-GatewayImageRelease.ps1') `
    -Image $imageTag -ExpectedRelease $ReleaseId
if ($LASTEXITCODE -ne 0) { throw 'gateway image release verification failed' }

[pscustomobject]@{
  Schema = 'lumberjacks-release-cut/v1'
  ReleaseId = $ReleaseId
  ModSha256 = $actualHash
  GatewayImage = $imageTag
  Stage = $ArtifactStage
  Verdict = 'passed'
}
