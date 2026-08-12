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

.NOTES
Not yet split by -Tool branch: the quest-lab and quest-picker branches (and
recipes/quest-catalogs, which quest-picker reads) are the comfy-quest lanes and are slated to
migrate into that repo at extraction, leaving telemetry-starter here. -RepoBlobBase exists so
the quest-lab README rewrite below can be retargeted ahead of that split without changing
today's behavior (default is this repo).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('quest-picker', 'telemetry-starter', 'quest-lab')]
    [string]$Tool,
    [string]$OutDir,
    [string]$RepoBlobBase = 'https://github.com/djcdevelopment/baseline/blob/main/'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$samples = Join-Path $PSScriptRoot 'samples'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist' }

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("wbzip-$Tool-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $staging | Out-Null
Write-Host "staging: $staging"
$packageVersion = $null
$packageReleaseId = $null

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

    if ($Tool -eq 'quest-lab') {
        Push-Location (Join-Path $root 'network\mod\ComfyQuestLab')
        try {
            & dotnet build -c Release
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with $LASTEXITCODE" }
        } finally { Pop-Location }
        
        $dll = Join-Path $root 'network\mod\ComfyQuestLab\bin\Release\ComfyQuestLab.dll'
        if (-not (Test-Path $dll)) { throw "Quest Lab DLL not found at $dll" }
        Copy-Item $dll -Destination $staging
        
        $readme = Join-Path $root 'network\mod\ComfyQuestLab\README.md'
        # The source README links across the repository. Rewrite every such link to either
        # its bundled companion or the canonical public source so the extracted package is
        # self-navigating instead of presenting dead ../../../ paths.
        $packageReadme = Get-Content $readme -Raw
        $readmeLinks = [ordered]@{
            '../../../tools/questlab-sheets/Start-QuestLabSheets.ps1' = 'questlab-sheets/Start-QuestLabSheets.ps1'
            '../../../tools/questlab-sheets/README.md' = 'questlab-sheets/README.md'
            '../../../tools/component-packets/EVENT-ATLAS.md' = "${RepoBlobBase}tools/component-packets/EVENT-ATLAS.md"
            '../../../tools/component-packets/samples/gallery-profiles.json' = "${RepoBlobBase}tools/component-packets/samples/gallery-profiles.json"
            '../../../tools/i5/Invoke-I5QuestLabBatch.ps1' = "${RepoBlobBase}tools/i5/Invoke-I5QuestLabBatch.ps1"
            '../../../tools/component-packets/quest-event-authoring.json' = "${RepoBlobBase}tools/component-packets/quest-event-authoring.json"
            '../../../tools/quest-packs/README.md' = "${RepoBlobBase}tools/quest-packs/README.md"
            '../../../docs/legal/LICENSING.md' = "${RepoBlobBase}docs/legal/LICENSING.md"
            'Patches/HarvestPatches.cs' = "${RepoBlobBase}network/mod/ComfyQuestLab/Patches/HarvestPatches.cs"
        }
        foreach ($link in $readmeLinks.GetEnumerator()) {
            $packageReadme = $packageReadme.Replace([string]$link.Key, [string]$link.Value)
        }
        if ($packageReadme.Contains('../../../')) { throw 'Quest Lab package README retains a repository-relative link' }
        [System.IO.File]::WriteAllText(
            (Join-Path $staging 'README.md'),
            $packageReadme,
            (New-Object System.Text.UTF8Encoding($false)))

        # Package the same reviewed config that developers and tests see. Generating a second
        # copy here previously dropped BepInEx section headers and silently drifted as settings
        # were added.
        $config = Join-Path $root 'network\mod\ComfyQuestLab\djcdevelopment.valheim.comfyquestlab.cfg'
        Copy-Item $config -Destination $staging

        # The event parser/CSV path is dependency-free; Google remains an optional install.
        # Copy an explicit file allowlist so local __pycache__, credentials, tokens, or receipts
        # can never hitch a ride in the creator package.
        $sheetsSource = Join-Path $root 'tools\questlab-sheets'
        $sheetsStage = Join-Path $staging 'questlab-sheets'
        New-Item -ItemType Directory -Path $sheetsStage | Out-Null
        @('questlab_sheets.py', 'Start-QuestLabSheets.ps1', 'requirements-google.txt', 'README.md') | ForEach-Object {
            Copy-Item (Join-Path $sheetsSource $_) -Destination $sheetsStage
        }

        # Include the richer dependency-free parser as a separate, explicit allowlist. It
        # adds filters plus JSON/CSV/XLSX/ZIP outputs without sharing credentials or state
        # with the fixed-loopback Sheets companion.
        $eventsSource = Join-Path $root 'tools\questlab-events'
        $eventsStage = Join-Path $staging 'questlab-events'
        New-Item -ItemType Directory -Path $eventsStage | Out-Null
        @('questlab_events.py', 'README.md') | ForEach-Object {
            Copy-Item (Join-Path $eventsSource $_) -Destination $eventsStage
        }

        @(
            'questlab-sheets\Start-QuestLabSheets.ps1',
            'questlab-sheets\README.md',
            'questlab-events\README.md'
        ) | ForEach-Object {
            if (-not (Test-Path (Join-Path $staging $_))) {
                throw "Quest Lab package README target is missing: $_"
            }
        }
        [regex]::Matches(
            $packageReadme,
            '\]\((?!https?://|#)([^)#]+)(?:#[^)]*)?\)'
        ) | ForEach-Object {
            $relativeTarget = $_.Groups[1].Value -replace '/', '\'
            if (-not (Test-Path (Join-Path $staging $relativeTarget))) {
                throw "Quest Lab package README has a broken local link: $relativeTarget"
            }
        }

        $pluginManifest = Get-Content (Join-Path $root 'network\mod\ComfyQuestLab\manifest.json') -Raw | ConvertFrom-Json
        $packageVersion = [string]$pluginManifest.version_number
        $pluginSource = Get-Content (Join-Path $root 'network\mod\ComfyQuestLab\ComfyQuestLab.cs') -Raw
        $releaseMatch = [regex]::Match($pluginSource, 'public const string ReleaseId = "([^"]+)";')
        if (-not $releaseMatch.Success) { throw 'Quest Lab ReleaseId was not found in ComfyQuestLab.cs' }
        $packageReleaseId = $releaseMatch.Groups[1].Value
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
    }
    if ($packageVersion) { $manifest['version'] = $packageVersion }
    if ($packageReleaseId) { $manifest['release_id'] = $packageReleaseId }
    $manifest['files'] = $entries
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
