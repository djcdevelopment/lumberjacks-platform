<#
.SYNOPSIS
Publishes a generic Companion bootstrap bundle as an immutable GitHub release.

.DESCRIPTION
Builds the credential-free Docker bootstrap zip and its adjacent SHA-256 manifest, then
attaches both to a GitHub release. This is intentionally separate from Publish-Modpack:
the small Companion bootstrap changes slowly and is a tester download, while mod/config
packages remain on the Gateway's no-restart client-pull lane.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ReleaseId,

    [string]$Repository,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),

    [switch]$Draft,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot 'New-CompanionBootstrap.ps1'
if (-not (Test-Path -LiteralPath $builder)) { throw 'New-CompanionBootstrap.ps1 was not found.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required to publish the bootstrap release.' }

if ([string]::IsNullOrWhiteSpace($Repository)) {
    $repositoryInfo = (& gh repo view --json nameWithOwner | ConvertFrom-Json)
    $Repository = $repositoryInfo.nameWithOwner
}
if ([string]::IsNullOrWhiteSpace($Repository)) { throw 'Could not determine the GitHub repository. Supply -Repository owner/name.' }

$artifact = & $builder -ReleaseId $ReleaseId -OutputDirectory $OutputDirectory
if (-not $artifact.package -or -not $artifact.manifest) { throw 'Bootstrap builder did not return package paths.' }

$notes = @"
Generic Windows Docker bootstrap for Lumberjacks Companion.

Extract the zip and run bootstrap/Start-LumberjacksCompanion.cmd. The bundle contains no Steam credential, access key, or machine-specific configuration. Its SHA-256 is recorded in the adjacent JSON manifest.
"@
$arguments = @('release', 'create', $ReleaseId, '--repo', $Repository, '--target', 'main', '--title', "Lumberjacks Companion $ReleaseId", '--notes', $notes)
if ($Draft) { $arguments += '--draft' }
$arguments += @($artifact.package, $artifact.manifest)

if ($DryRun) {
    [pscustomobject]@{
        repository = $Repository
        release = $ReleaseId
        draft = [bool]$Draft
        package = $artifact.package
        manifest = $artifact.manifest
        sha256 = $artifact.sha256
        command = 'gh ' + (($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }) -join ' ')
    }
    return
}

$previousErrorAction = $ErrorActionPreference
try {
    # A missing release is the expected precondition for an immutable create. Windows
    # PowerShell otherwise promotes gh's stderr line into a terminating NativeCommandError.
    $ErrorActionPreference = 'Continue'
    $existing = & gh release view $ReleaseId --repo $Repository 2>$null
}
finally {
    $ErrorActionPreference = $previousErrorAction
}
if ($LASTEXITCODE -eq 0) { throw "GitHub release '$ReleaseId' already exists in $Repository; choose a new immutable release id." }
& gh @arguments
if ($LASTEXITCODE -ne 0) { throw 'GitHub release publish failed.' }

$packageName = Split-Path -Leaf $artifact.package
$manifestName = Split-Path -Leaf $artifact.manifest
$releaseUrl = "https://github.com/$Repository/releases/tag/$ReleaseId"
$latest = [ordered]@{
    schema_version = 1
    release = $ReleaseId
    repository = $Repository
    published_utc = [DateTime]::UtcNow.ToString('O')
    release_url = $releaseUrl
    package_url = "https://github.com/$Repository/releases/download/$ReleaseId/$packageName"
    manifest_url = "https://github.com/$Repository/releases/download/$ReleaseId/$manifestName"
    package_file = $packageName
    package_sha256 = $artifact.sha256
    package_size_bytes = (Get-Item -LiteralPath $artifact.package).Length
    entrypoint = 'bootstrap/Start-LumberjacksCompanion.cmd'
}
$latestPath = Join-Path $PSScriptRoot 'latest-bootstrap.json'
[IO.File]::WriteAllText($latestPath, ($latest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    repository = $Repository
    release = $ReleaseId
    package = $artifact.package
    manifest = $artifact.manifest
    sha256 = $artifact.sha256
    url = $releaseUrl
    latest_manifest = $latestPath
}
