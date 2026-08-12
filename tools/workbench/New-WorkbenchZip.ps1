<#
.SYNOPSIS
Build the platform-owned telemetry-starter Workbench zip.

.DESCRIPTION
Quest Lab and Quest Picker packages are published by comfy-quest. This builder
owns only telemetry-starter and stages no files from sibling checkouts.
#>
[CmdletBinding()]
param(
    [ValidateSet('telemetry-starter')]
    [string]$Tool = 'telemetry-starter',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $root 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $root

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist' }
$staging = Join-Path ([IO.Path]::GetTempPath()) (
    'wbzip-telemetry-starter-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $staging | Out-Null
Write-Host "staging: $staging"

try {
    $samples = Join-Path $PSScriptRoot 'samples\telemetry-starter'
    Copy-Item (Join-Path $samples 'STARTER.md') -Destination $staging
    Copy-Item (Join-Path $samples 'poll_telemetry.py') -Destination $staging
    Copy-Item (Join-Path $root 'Lumberjacks\docs\api\telemetry-v0.md') -Destination $staging

    $entries = @()
    Get-ChildItem -Path $staging -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($staging.Length + 1) -replace '\\', '/'
        $entries += [ordered]@{
            path = $rel
            sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
            bytes = $_.Length
        }
    }
    $manifest = [ordered]@{
        tool = $Tool
        built_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        files = $entries
    }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        (New-Object Text.UTF8Encoding($false)))

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $PSScriptRoot 'Test-WorkbenchZipPrivacy.ps1') -Path $staging
    if ($LASTEXITCODE -ne 0) {
        throw "privacy scan failed (exit $LASTEXITCODE); staging retained at $staging"
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zipPath = Join-Path $OutDir 'telemetry-starter.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    try {
        $archive = New-Object IO.Compression.ZipArchive(
            $zipStream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem -Path $staging -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($staging.Length + 1) -replace '\\', '/'
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $_.FullName, $rel) | Out-Null
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $zipStream.Dispose() }

    [pscustomobject]@{
        Path = $zipPath
        Sha256 = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
        Bytes = (Get-Item -LiteralPath $zipPath).Length
    }
    Remove-Item -LiteralPath $staging -Recurse -Force
}
catch {
    Write-Error "FAILED: $_; staging retained at $staging"
    exit 1
}
