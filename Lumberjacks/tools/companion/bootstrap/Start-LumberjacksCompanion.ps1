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
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $PSScriptRoot
$valheimPath = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Valheim'
$valheimExe = Join-Path $valheimPath 'valheim.exe'
$compose = Join-Path $bundleRoot 'tools\companion\docker-compose.yml'
$overrideTemplate = Join-Path $bundleRoot 'tools\companion\docker-compose.valheim.yml.example'
$override = Join-Path $bundleRoot 'tools\companion\docker-compose.valheim.yml'
$dockerCandidates = @(
    'C:\Program Files\Docker\Docker\resources\bin\docker.exe',
    (Get-Command docker.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not (Test-Path -LiteralPath $valheimExe)) {
    throw "Valheim was not found at $valheimPath. Install Valheim through Steam before starting Companion."
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
