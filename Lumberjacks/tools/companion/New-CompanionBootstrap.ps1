<#
.SYNOPSIS
Builds the generic Windows Companion bootstrap zip and its SHA-256 manifest.

.DESCRIPTION
The output deliberately excludes Valheim configuration and credentials. It is a generic
release artifact that an already-enrolled tester can extract and launch; their existing
ComfyNetworkSense config remains in the game directory and is read locally by Companion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseId,

    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'dist' }
$lumberjacksRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repoRoot = Split-Path -Parent $lumberjacksRoot
$packageVerifier = Join-Path $PSScriptRoot 'Test-CompanionBootstrapPackage.ps1'
$safeRelease = $ReleaseId -replace '[^A-Za-z0-9._-]', '-'
if ([string]::IsNullOrWhiteSpace($safeRelease)) { throw 'ReleaseId must contain letters or numbers.' }

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("lumberjacks-companion-" + [Guid]::NewGuid().ToString('N'))
$bundleRoot = Join-Path $temporaryRoot 'Lumberjacks-Companion'
$packageName = "Lumberjacks-Companion-$safeRelease.zip"
$packagePath = Join-Path $OutputDirectory $packageName

try {
    New-Item -ItemType Directory -Force -Path $bundleRoot, $OutputDirectory | Out-Null
    foreach ($file in 'Directory.Build.props', 'Directory.Packages.props') {
        Copy-Item -LiteralPath (Join-Path $lumberjacksRoot $file) -Destination $bundleRoot
    }
    $bundleSource = Join-Path $bundleRoot 'src'
    New-Item -ItemType Directory -Force -Path $bundleSource | Out-Null
    Copy-Item -LiteralPath (Join-Path $lumberjacksRoot 'src\Game.Companion') -Destination $bundleSource -Recurse

    # Copy only the runtime inputs.  In particular, never capture a prior bootstrap
    # artifact from tools\companion\dist or a machine-specific compose override.
    $bundleTools = Join-Path $bundleRoot 'tools\companion'
    New-Item -ItemType Directory -Force -Path $bundleTools | Out-Null
    foreach ($file in 'docker-compose.yml', 'docker-compose.valheim.yml.example', 'README.md', 'Start-WorkbenchHostRunner.ps1') {
        Copy-Item -LiteralPath (Join-Path $lumberjacksRoot "tools\companion\$file") -Destination $bundleTools
    }
    Copy-Item -LiteralPath (Join-Path $lumberjacksRoot 'tools\companion\bootstrap') -Destination $bundleTools -Recurse

    # The Companion Wave 0 handoff points at repo-level operator scripts. Include the
    # credential-free scripts it names so a downloaded bootstrap is a runnable workbench,
    # not just a UI with commands that only work from the developer checkout.
    $bundleRepoTools = Join-Path $bundleRoot 'tools'
    New-Item -ItemType Directory -Force -Path $bundleRepoTools | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\wave0') -Destination $bundleRepoTools -Recurse
    # The i5 README is an operator-local lane record and intentionally contains
    # machine-specific enrollment details. The distributable needs the runnable
    # scripts, not that private documentation.
    $bundleI5Tools = Join-Path $bundleRepoTools 'i5'
    New-Item -ItemType Directory -Force -Path $bundleI5Tools | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tools\i5') -File -Filter '*.ps1' |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $bundleI5Tools }
    $bundleWorkbenchTools = Join-Path $bundleRepoTools 'workbench'
    New-Item -ItemType Directory -Force -Path $bundleWorkbenchTools | Out-Null
    foreach ($file in 'Test-WorkbenchZipPrivacy.ps1', 'Test-WorkbenchSupportExport.ps1', 'Test-WorkbenchProfileBoundary.ps1', 'Test-WorkbenchMcpIdentity.ps1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot "tools\workbench\$file") -Destination $bundleWorkbenchTools
    }

    $bootstrapRelease = [ordered]@{
        schema_version = 1
        release = $safeRelease
        created_utc = [DateTime]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText((Join-Path $bundleTools 'bootstrap-release.json'), ($bootstrapRelease | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    Get-ChildItem -LiteralPath $bundleRoot -Force | Compress-Archive -DestinationPath $packagePath -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $packageVerifier)) { throw "Companion bootstrap package verifier not found: $packageVerifier" }
    & $packageVerifier -PackagePath $packagePath | Out-Host
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schema_version = 1
        release = $safeRelease
        package_file = $packageName
        package_sha256 = $hash
        package_size_bytes = (Get-Item -LiteralPath $packagePath).Length
        created_utc = [DateTime]::UtcNow.ToString('O')
        entrypoint = 'bootstrap/Start-LumberjacksCompanion.cmd'
    }
    $manifestPath = Join-Path $OutputDirectory "Lumberjacks-Companion-$safeRelease.json"
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ package = $packagePath; manifest = $manifestPath; sha256 = $hash; bytes = $manifest.package_size_bytes }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
