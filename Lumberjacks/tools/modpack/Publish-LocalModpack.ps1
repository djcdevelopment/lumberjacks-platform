#Requires -Version 5.1
<#
.SYNOPSIS
Publish an immutable modpack pointer to the local Docker Gateway.

.DESCRIPTION
Stages a package and schema-v1 current.json in the existing local Gateway data
volume, verifies SHA-256 inside the container, and replaces the pointer atomically.
This is the local/Lab counterpart to the P7 publisher; it never contacts GCP and
does not install files into Valheim.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')]
    [string]$ReleaseId,

    [Parameter(Mandatory)]
    [ValidatePattern('^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')]
    [string]$ModRelease,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackagePath,

    [string]$ProjectName = 'lumberjacks-local',
    [string]$Notes = 'local Lab client-pull release',
    [switch]$NoValheimRestartRequired,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$LumberjacksRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ComposeFile = (Resolve-Path (Join-Path $LumberjacksRoot 'infra\docker\docker-compose.yml')).Path
$package = Get-Item -LiteralPath $PackagePath
$hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$packageName = 'Comfy-P7-Alpha-Mods.zip'
$relativePackage = "releases/$ReleaseId/$packageName"
$containerRoot = '/data/modpack'
$releaseRoot = "$containerRoot/releases/$ReleaseId"
$manifest = [ordered]@{
    schema_version = 1
    release = $ReleaseId
    mod_release = $ModRelease
    package_kind = 'comfy_p7_alpha_modpack'
    package_file = $relativePackage
    package_sha256 = $hash
    package_size_bytes = $package.Length
    created_utc = (Get-Date).ToUniversalTime().ToString('o')
    requires_valheim_restart = -not $NoValheimRestartRequired
    notes = $Notes
}

if ($DryRun) {
    [pscustomobject]@{
        schema_version = 1
        verdict = 'dry_run'
        project = $ProjectName
        compose_file = $ComposeFile
        manifest = $manifest
        gateway_restart_required = $false
        valheim_files_changed = $false
    }
    return
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker CLI not found' }
$containers = @(& docker compose -p $ProjectName -f $ComposeFile ps -q gateway 2>$null)
$dockerExit = $LASTEXITCODE
$container = $containers | Select-Object -First 1
if ($dockerExit -ne 0 -or [string]::IsNullOrWhiteSpace($container)) {
    throw "local Gateway is not running for Compose project '$ProjectName'"
}
$container = $container.ToString().Trim()
$labels = (& docker inspect $container --format '{{json .Config.Labels}}' 2>$null | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $labels.'com.docker.compose.project' -ne $ProjectName -or
    $labels.'com.docker.compose.service' -ne 'gateway') {
    throw "container '$container' is not the expected $ProjectName Gateway"
}

$temporaryManifest = Join-Path ([IO.Path]::GetTempPath()) "lumberjacks-$ReleaseId-local-current.json"
$incomingPackage = "$containerRoot/.incoming-$ReleaseId-package"
$incomingManifest = "$containerRoot/.incoming-$ReleaseId-manifest"
try {
    [IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    & docker exec $container test -e "$releaseRoot/manifest.json"
    $manifestExists = $LASTEXITCODE -eq 0
    & docker exec $container test -e "$releaseRoot/$packageName"
    $packageExists = $LASTEXITCODE -eq 0
    if ($manifestExists -or $packageExists) {
        throw "local release '$ReleaseId' already exists; release identities are immutable"
    }
    & docker exec $container mkdir -p $releaseRoot
    if ($LASTEXITCODE -ne 0) { throw 'could not create the local release directory' }
    & docker cp $package.FullName "${container}:$incomingPackage"
    if ($LASTEXITCODE -ne 0) { throw 'local package staging failed' }
    & docker cp $temporaryManifest "${container}:$incomingManifest"
    if ($LASTEXITCODE -ne 0) { throw 'local manifest staging failed' }

    $insideHashOutput = @(& docker exec $container sha256sum $incomingPackage 2>$null)
    $dockerExit = $LASTEXITCODE
    $insideHash = $insideHashOutput | Select-Object -First 1
    if ($dockerExit -ne 0 -or [string]::IsNullOrWhiteSpace($insideHash)) {
        throw 'could not hash the staged local package'
    }
    $insideHash = ($insideHash.ToString() -split '\s+', 2)[0].ToLowerInvariant()
    if ($insideHash -ne $hash) {
        throw "staged package hash mismatch: expected $hash, got $insideHash"
    }

    & docker exec $container mv -f $incomingPackage "$releaseRoot/$packageName"
    if ($LASTEXITCODE -ne 0) { throw 'could not admit the staged local package' }
    & docker exec $container cp $incomingManifest "$releaseRoot/manifest.json"
    if ($LASTEXITCODE -ne 0) { throw 'could not retain the immutable local manifest' }
    & docker exec $container cp $incomingManifest "$containerRoot/current.json.tmp"
    if ($LASTEXITCODE -ne 0) { throw 'could not stage the local release pointer' }
    & docker exec $container mv -f "$containerRoot/current.json.tmp" "$containerRoot/current.json"
    if ($LASTEXITCODE -ne 0) { throw 'could not atomically replace the local release pointer' }

    [pscustomobject]@{
        schema_version = 1
        verdict = 'published'
        project = $ProjectName
        container = $container
        release = $ReleaseId
        mod_release = $ModRelease
        package_sha256 = $hash
        package_size_bytes = $package.Length
        runtime_manifest = "$containerRoot/current.json"
        gateway_restart_required = $false
        valheim_files_changed = $false
    }
}
finally {
    Remove-Item -LiteralPath $temporaryManifest -Force -ErrorAction SilentlyContinue
    if ($container) {
        & docker exec $container rm -f $incomingPackage $incomingManifest 2>$null | Out-Null
    }
}
