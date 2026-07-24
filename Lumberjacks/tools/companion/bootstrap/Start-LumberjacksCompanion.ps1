<#
.SYNOPSIS
Starts the Lumberjacks Companion from an extracted release bundle on Windows.

.DESCRIPTION
This is the one-click alpha bootstrap. It never reads or copies a Valheim access key:
the running Companion reads the existing local ComfyNetworkSense config only when an
operator chooses a mod update. The script starts Docker Desktop, verifies the default
Valheim installation, writes a local compose override, and opens the loopback dashboard.
#>
[CmdletBinding()]
param(
    [string]$ValheimPath,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $bundleRoot 'tools\companion\docker-compose.yml'
$overrideTemplate = Join-Path $bundleRoot 'tools\companion\docker-compose.valheim.yml.example'
$override = Join-Path $bundleRoot 'tools\companion\docker-compose.valheim.yml'
$bootstrapReleaseFile = Join-Path $bundleRoot 'tools\companion\bootstrap-release.json'
$dockerCandidates = @(
    'C:\Program Files\Docker\Docker\resources\bin\docker.exe',
    (Get-Command docker.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

function Find-ValheimPath {
    param([string]$RequestedPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($RequestedPath) { $candidates.Add($RequestedPath) }
    if ($env:LUMBERJACKS_VALHEIM_PATH) { $candidates.Add($env:LUMBERJACKS_VALHEIM_PATH) }

    $steamRoot = Join-Path ${env:ProgramFiles(x86)} 'Steam'
    if ($steamRoot) {
        $candidates.Add((Join-Path $steamRoot 'steamapps\common\Valheim'))
        $libraryFile = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraryFile) {
            # Steam records each installed library as a VDF "path" value. The launcher does
            # not parse its entire format: a discovered path is accepted only if valheim.exe
            # exists at the expected location.
            foreach ($match in [regex]::Matches((Get-Content -LiteralPath $libraryFile -Raw), '"path"\s+"(?<path>(?:\\.|[^"\\])*)"')) {
                $library = $match.Groups['path'].Value -replace '\\\\', '\\'
                if ($library) { $candidates.Add((Join-Path $library 'steamapps\common\Valheim')) }
            }
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $full = [IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath (Join-Path $full 'valheim.exe') -PathType Leaf) { return $full }
    }
    return $null
}

$valheimPath = Find-ValheimPath -RequestedPath $ValheimPath
if (-not $valheimPath) {
    throw 'Valheim was not found in the default or configured Steam libraries. Install it through Steam, or run the launcher with -ValheimPath C:\path\to\Valheim.'
}
if (-not (Test-Path -LiteralPath $compose) -or -not (Test-Path -LiteralPath $overrideTemplate)) {
    throw 'This Companion bundle is incomplete. Download and extract a fresh release bundle.'
}
if (-not $dockerCandidates) {
    throw 'Docker Desktop is required for this alpha Companion bundle. Install Docker Desktop, then run this launcher again.'
}

$docker = $dockerCandidates
& $docker desktop start 2>$null
$ready = $false
for ($attempt = 1; $attempt -le 24; $attempt++) {
    $version = & $docker version --format '{{.Server.Version}}' 2>$null
    if ($LASTEXITCODE -eq 0 -and $version) {
        $ready = $true
        break
    }
    Start-Sleep -Seconds 5
}
if (-not $ready) {
    throw 'Docker Desktop did not become ready. Start Docker Desktop, wait for it to finish loading, then run this launcher again.'
}

if (-not (Test-Path -LiteralPath $override)) {
    Copy-Item -LiteralPath $overrideTemplate -Destination $override
}
$env:LUMBERJACKS_VALHEIM_HOST_PATH = $valheimPath
$env:LUMBERJACKS_COMPANION_SOURCE_BRANCH = 'release-bundle'
$env:LUMBERJACKS_COMPANION_SOURCE_DIRTY = 'false'
if (Test-Path -LiteralPath $bootstrapReleaseFile) {
    try {
        $bootstrapRelease = Get-Content -LiteralPath $bootstrapReleaseFile -Raw | ConvertFrom-Json
        if ($bootstrapRelease.release) {
            $env:LUMBERJACKS_COMPANION_BOOTSTRAP_RELEASE = $bootstrapRelease.release
            $env:LUMBERJACKS_COMPANION_SOURCE_REVISION = 'bundle:' + $bootstrapRelease.release
            $env:LUMBERJACKS_COMPANION_IMAGE = 'lumberjacks-companion:' + $bootstrapRelease.release
        }
    }
    catch {
        Write-Warning "Could not read Companion bootstrap release metadata: $($_.Exception.Message)"
    }
}
Push-Location (Join-Path $bundleRoot 'tools\companion')
try {
    & $docker compose -p lumberjacks-companion -f docker-compose.yml -f docker-compose.valheim.yml up --build -d
    if ($LASTEXITCODE -ne 0) { throw 'Companion container build or start failed.' }
}
finally {
    Pop-Location
}

$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
    try {
        $health = Invoke-RestMethod 'http://127.0.0.1:8080/health' -TimeoutSec 3
        if ($health.ok) { break }
    }
    catch { }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)
if (-not $health.ok) { throw 'Companion did not answer on http://127.0.0.1:8080.' }

Write-Host 'Lumberjacks Companion is ready at http://127.0.0.1:8080' -ForegroundColor Green
if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:8080' }
