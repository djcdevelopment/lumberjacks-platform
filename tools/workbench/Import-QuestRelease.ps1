#Requires -Version 5.1
<#
.SYNOPSIS
Import a published comfy-quest release into the platform-owned Workbench boundary.

.DESCRIPTION
Without -Check, resolves one GitHub release by its exact Quest tag and requires the
operator's independently obtained release-manifest SHA-256. It refuses drafts,
prereleases, asset-set drift, or a manifest digest mismatch, verifies the four
payload assets and both metadata files, then stages the four consumed assets, the
upstream manifest, the Workbench catalog hashes, and the local lock.

With -Check, performs a deterministic offline verification of the committed lock,
vendored manifest/assets, and Workbench catalog. An unpinned lock is a valid
readiness state and explicitly makes no live-release claim.
#>
[CmdletBinding()]
param(
    [string]$ReleaseTag,
    [string]$ExpectedManifestSha256,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lockPath = Join-Path $repoRoot 'Lumberjacks\docs\workbench\quest-release.lock.json'
$catalogPath = Join-Path $repoRoot 'Lumberjacks\docs\workbench\workbench.json'
Import-Module (Join-Path $PSScriptRoot 'QuestRelease.psm1') -Force

if ($Check) {
    if (-not [string]::IsNullOrWhiteSpace($ReleaseTag) -or
        -not [string]::IsNullOrWhiteSpace($ExpectedManifestSha256)) {
        throw '-ReleaseTag and -ExpectedManifestSha256 cannot be combined with -Check; the check reads the committed lock.'
    }
    $result = Test-QuestReleaseLock -RepoRoot $repoRoot -LockPath $lockPath -CatalogPath $catalogPath
    $result | ConvertTo-Json -Compress
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    throw '-ReleaseTag is required unless -Check is used.'
}
if ([string]::IsNullOrWhiteSpace($ExpectedManifestSha256)) {
    throw '-ExpectedManifestSha256 is required unless -Check is used.'
}
$ExpectedManifestSha256 = $ExpectedManifestSha256.ToLowerInvariant()
if ($ExpectedManifestSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw '-ExpectedManifestSha256 must be exactly 64 hexadecimal characters.'
}

. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

$repository = 'djcdevelopment/comfy-quest'
$expectedFiles = @(Get-QuestReleaseFileNames)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-quest-import-' + [guid]::NewGuid().ToString('N'))

try {
    $savedErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $releaseOutput = @(& gh release view $ReleaseTag --repo $repository --json 'tagName,isDraft,isPrerelease,publishedAt,assets' 2>&1)
        $releaseExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorAction
    }
    $releaseJson = ($releaseOutput | Out-String)
    if ($releaseExit -ne 0) {
        throw "No published comfy-quest release is available for tag '$ReleaseTag'; the lock was not changed. gh: $($releaseJson.Trim())"
    }
    $release = $releaseJson | ConvertFrom-Json
    if ([string]$release.tagName -cne $ReleaseTag) { throw 'GitHub returned a different release tag.' }
    if ([bool]$release.isDraft) { throw 'Draft Quest releases cannot be imported.' }
    if ([bool]$release.isPrerelease) { throw 'Prerelease Quest releases cannot be imported by the stable split-proof lane.' }
    $actualFiles = @($release.assets | ForEach-Object { [string]$_.name })
    $difference = Compare-Object -ReferenceObject @($expectedFiles | Sort-Object) -DifferenceObject @($actualFiles | Sort-Object)
    if ($difference) {
        throw "GitHub Quest release asset set drifted: expected [$(@($expectedFiles | Sort-Object) -join ', ')], got [$(@($actualFiles | Sort-Object) -join ', ')]."
    }

    New-Item -ItemType Directory -Path $temporary | Out-Null
    $arguments = @('release', 'download', $ReleaseTag, '--repo', $repository, '--dir', $temporary)
    foreach ($name in $expectedFiles) { $arguments += @('--pattern', $name) }
    $savedErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $downloadOutput = @(& gh @arguments 2>&1)
        $downloadExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorAction
    }
    if ($downloadExit -ne 0) {
        throw ('gh release download failed with exit code {0}: {1}' -f $downloadExit, ($downloadOutput -join [Environment]::NewLine))
    }

    $verification = Test-QuestReleaseBundle -ReleaseDirectory $temporary -ExpectedTag $ReleaseTag
    $installParameters = @{
        Verification = $verification
        PublishedAt = [string]$release.publishedAt
        ExpectedManifestSha256 = $ExpectedManifestSha256
        RepoRoot = $repoRoot
        LockPath = $lockPath
        CatalogPath = $catalogPath
    }
    $lock = Install-QuestReleaseBundle @installParameters
    $checked = Test-QuestReleaseLock -RepoRoot $repoRoot -LockPath $lockPath -CatalogPath $catalogPath
    [pscustomobject][ordered]@{
        verdict = 'imported'
        repository = $repository
        tag = [string]$lock.release_tag
        revision = [string]$lock.revision
        release_manifest_sha256 = [string]$lock.release_manifest_sha256
        assets = [int]$checked.Assets
        note = 'Commit workbench.json first, then render and commit workbench.html using the established two-commit provenance flow.'
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemporary.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected path '$resolvedTemporary'."
        }
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
