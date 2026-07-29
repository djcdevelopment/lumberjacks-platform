<#
.SYNOPSIS
Build a Community Workbench cold-start zip. Windows PowerShell 5.1 compatible.

.DESCRIPTION
Stages the tool's files into a temp dir, runs any generation steps (synthetic sample
data — never real community data), writes a manifest with per-file SHA-256, runs the
privacy scanner as a MANDATORY gate, then produces tools/workbench/dist/<tool>.zip and
prints its SHA-256 + size (the values workbench.json's access block needs).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\workbench\New-WorkbenchZip.ps1 -Tool quest-picker
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('quest-picker', 'telemetry-starter')]
    [string]$Tool,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$samples = Join-Path $PSScriptRoot 'samples'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist' }

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("wbzip-$Tool-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $staging | Out-Null
Write-Host "staging: $staging"

function Invoke-Python {
    param([string]$WorkDir, [string[]]$Arguments)
    Push-Location $WorkDir
    try {
        & python @Arguments
        if ($LASTEXITCODE -ne 0) { throw "python $($Arguments -join ' ') exited $LASTEXITCODE" }
    } finally { Pop-Location }
}

try {
    if ($Tool -eq 'quest-picker') {
        $qc = Join-Path $staging 'quest-catalogs'
        $sample = Join-Path $staging 'sample'
        New-Item -ItemType Directory -Path $qc, $sample | Out-Null

        $toolFiles = @('harvest.py', 'render_quest_picker.py', 'validate.py', 'schema.md', 'quest-view-schema.md')
        foreach ($f in $toolFiles) {
            Copy-Item (Join-Path $root "recipes\quest-catalogs\$f") -Destination $qc
        }
        $exampleView = Join-Path $root 'recipes\quest-catalogs\example-quest-view.json'
        if (Test-Path $exampleView) { Copy-Item $exampleView -Destination $qc }

        # sanitized config replaces the real sources.json (which names real guilds)
        Copy-Item (Join-Path $samples 'quest-picker\sources.sample.json') -Destination (Join-Path $qc 'sources.json')
        Copy-Item (Join-Path $samples 'quest-picker\make_sample_tracker.py') -Destination $sample
        Copy-Item (Join-Path $samples 'quest-picker\RUN-ME.md') -Destination (Join-Path $staging 'RUN-ME.md')

        Invoke-Python -WorkDir $sample -Arguments @('make_sample_tracker.py')
        Invoke-Python -WorkDir $staging -Arguments @('quest-catalogs/harvest.py', 'sample-guild')
        Invoke-Python -WorkDir $staging -Arguments @('quest-catalogs/render_quest_picker.py', 'quest-picker.html', 'sample/quest-catalog-sample.json')

        # marker the privacy scanner uses to distinguish the synthetic picker from the real one
        [System.IO.File]::AppendAllText((Join-Path $staging 'quest-picker.html'),
            "`n<!-- SAMPLE-CATALOG: synthetic demonstration data only -->`n",
            (New-Object System.Text.UTF8Encoding($false)))
    }

    if ($Tool -eq 'telemetry-starter') {
        Copy-Item (Join-Path $samples 'telemetry-starter\STARTER.md') -Destination $staging
        Copy-Item (Join-Path $samples 'telemetry-starter\poll_telemetry.py') -Destination $staging
        Copy-Item (Join-Path $root 'Lumberjacks\docs\api\telemetry-v0.md') -Destination $staging
    }

    # manifest: relative path + sha256 + bytes for every staged file
    $entries = @()
    Get-ChildItem -Path $staging -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($staging.Length + 1) -replace '\\', '/'
        $entries += [ordered]@{
            path   = $rel
            sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLower()
            bytes  = $_.Length
        }
    }
    $manifest = [ordered]@{
        tool     = $Tool
        built_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        files    = $entries
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $staging 'manifest.json'), $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

    # MANDATORY privacy gate — a finding aborts the build and keeps staging for inspection
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-WorkbenchZipPrivacy.ps1') -Path $staging
    if ($LASTEXITCODE -ne 0) {
        throw "privacy scan FAILED (exit $LASTEXITCODE) - zip NOT built. Staging kept for inspection: $staging"
    }

    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    $zipPath = Join-Path $OutDir "$Tool.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    # Compress-Archive writes backslash entry separators, which strict unzip tools reject;
    # write the archive manually with forward slashes so it extracts cleanly everywhere.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem -Path $staging -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($staging.Length + 1) -replace '\\', '/'
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel) | Out-Null
            }
        } finally { $archive.Dispose() }
    } finally { $zipStream.Dispose() }

    $hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLower()
    $size = (Get-Item $zipPath).Length
    Write-Host ""
    Write-Host "BUILT  $zipPath"
    Write-Host "sha256 $hash"
    Write-Host "bytes  $size"

    Remove-Item -Recurse -Force $staging
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    Write-Host "staging retained: $staging"
    exit 1
}
