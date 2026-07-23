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

    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$lumberjacksRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
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
    foreach ($file in 'docker-compose.yml', 'docker-compose.valheim.yml.example', 'README.md') {
        Copy-Item -LiteralPath (Join-Path $lumberjacksRoot "tools\companion\$file") -Destination $bundleTools
    }
    Copy-Item -LiteralPath (Join-Path $lumberjacksRoot 'tools\companion\bootstrap') -Destination $bundleTools -Recurse
    $bootstrapRelease = [ordered]@{
        schema_version = 1
        release = $safeRelease
        created_utc = [DateTime]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText((Join-Path $bundleTools 'bootstrap-release.json'), ($bootstrapRelease | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    Get-ChildItem -LiteralPath $bundleRoot -Force | Compress-Archive -DestinationPath $packagePath -CompressionLevel Optimal
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
