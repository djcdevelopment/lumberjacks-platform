<#
.SYNOPSIS
Start the local Docker-backed Lumberjacks Companion with the Valheim directory mounted.

.DESCRIPTION
This is the canonical local developer/operator launcher for OMEN-class Windows machines.
It starts the `lumberjacks-companion` compose project with both the base compose file and
the Valheim mount override, then verifies the loopback Companion can see `/valheim`.

The script also retires the known pre-canonical read-only compose project named `companion`
when it was created from this same companion compose file. That old project binds
127.0.0.1:8080 without mounting Valheim, which makes the dashboard look alive while the
updater cannot write files.

.PARAMETER ValheimPath
Windows Valheim install path. Defaults to the standard Steam install location.

.PARAMETER ReadOnly
Start without the Valheim mount. This is useful only for dashboard viewing; mod updates
will not work.

.PARAMETER NoLegacyCleanup
Do not stop the known legacy `companion` compose project before starting.

.EXAMPLE
.\Start-LocalCompanion.ps1
#>
[CmdletBinding()]
param(
    [string]$ValheimPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch]$ReadOnly,
    [switch]$NoLegacyCleanup
)

$ErrorActionPreference = 'Stop'

$ToolRoot = $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ToolRoot '..\..')
$ComposeFile = Resolve-Path (Join-Path $ToolRoot 'docker-compose.yml')
$ValheimComposeFile = Resolve-Path (Join-Path $ToolRoot 'docker-compose.valheim.yml')
$LatestBootstrapFile = Join-Path $ToolRoot 'latest-bootstrap.json'
$ProjectName = 'lumberjacks-companion'
$LegacyProjectName = 'companion'

function Set-WorkbenchSourceMetadata {
    $revision = (& git -C $RepoRoot.Path rev-parse HEAD 2>$null | Select-Object -First 1)
    $branch = (& git -C $RepoRoot.Path branch --show-current 2>$null | Select-Object -First 1)
    $dirty = (& git -C $RepoRoot.Path status --porcelain --untracked-files=no 2>$null)
    $env:LUMBERJACKS_COMPANION_SOURCE_REVISION = if ($revision) { $revision.ToString().Trim() } else { 'unknown' }
    $env:LUMBERJACKS_COMPANION_SOURCE_BRANCH = if ($branch) { $branch.ToString().Trim() } else { 'unknown' }
    $env:LUMBERJACKS_COMPANION_SOURCE_DIRTY = if ($dirty) { 'true' } else { 'false' }
    $env:LUMBERJACKS_COMPANION_IMAGE = "$ProjectName:local"
}

function Test-DockerServer {
    param([int]$TimeoutSeconds = 8)

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Ok = $false; Detail = 'docker CLI not found' }
    }

    $job = Start-Job -ScriptBlock { docker version --format '{{.Server.Version}}' 2>&1 }
    if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Ok = $false; Detail = "docker server did not answer within $TimeoutSeconds seconds" }
    }

    $output = Receive-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    if ($output) {
        $server = ($output | Select-Object -Last 1).ToString().Trim()
        if ($server -and $server -notmatch 'error|failed|Cannot connect|pipe') {
            return [pscustomobject]@{ Ok = $true; Detail = "Docker server $server" }
        }
        return [pscustomobject]@{ Ok = $false; Detail = (($output | Out-String) -replace '\s+', ' ').Trim() }
    }

    return [pscustomobject]@{ Ok = $false; Detail = 'docker server returned no version' }
}

function Stop-KnownLegacyCompanion {
    $containers = docker ps -a `
        --filter "label=com.docker.compose.project=$LegacyProjectName" `
        --filter "label=com.docker.compose.service=companion" `
        --format '{{.Names}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $containers) { return }

    $composePath = [System.IO.Path]::GetFullPath($ComposeFile.Path)
    $matched = $false
    foreach ($name in @($containers)) {
        if (-not $name) { continue }
        $inspect = docker inspect $name 2>$null | ConvertFrom-Json
        if ($LASTEXITCODE -eq 0 -and $inspect) {
            $normalized = $inspect[0].Config.Labels.'com.docker.compose.project.config_files'
            if ($normalized -ieq $composePath) {
                $matched = $true
                break
            }
        }
    }

    if (-not $matched) {
        Write-Host "legacy project '$LegacyProjectName' exists, but it was not created from $composePath; leaving it alone"
        return
    }

    Write-Host "stopping legacy read-only Companion compose project '$LegacyProjectName'"
    docker compose -p $LegacyProjectName -f $ComposeFile.Path down
    if ($LASTEXITCODE -ne 0) { throw "failed to stop legacy compose project '$LegacyProjectName'" }
}

function Assert-PortAvailableOrOwnedByProject {
    $owners = docker ps --format '{{.Names}}|{{.Ports}}' |
        Where-Object { $_ -match '127\.0\.0\.1:8080->8080/tcp' }
    foreach ($line in @($owners)) {
        $name = ($line -split '\|', 2)[0]
        $inspect = docker inspect $name 2>$null | ConvertFrom-Json
        $project = $inspect[0].Config.Labels.'com.docker.compose.project'
        if ($project -ne $ProjectName) {
            throw "127.0.0.1:8080 is already used by container '$name' from compose project '$project'. Stop it or rerun without that conflict."
        }
    }
}

$dockerState = Test-DockerServer
if (-not $dockerState.Ok) {
    throw "Docker Desktop Linux engine is not ready: $($dockerState.Detail). Start Docker Desktop, wait until it is running, then retry."
}
Write-Host $dockerState.Detail

if (-not $ReadOnly -and -not (Test-Path -LiteralPath $ValheimPath)) {
    throw "Valheim path not found: $ValheimPath. Pass -ValheimPath or use -ReadOnly for dashboard-only mode."
}

if (-not $NoLegacyCleanup) {
    Stop-KnownLegacyCompanion
}
Assert-PortAvailableOrOwnedByProject

Set-Location $RepoRoot
Set-WorkbenchSourceMetadata
$composeArgs = @('compose', '-p', $ProjectName, '-f', $ComposeFile.Path)
if (Test-Path -LiteralPath $LatestBootstrapFile) {
    try {
        $latestBootstrap = Get-Content -LiteralPath $LatestBootstrapFile -Raw | ConvertFrom-Json
        if ($latestBootstrap.release) {
            $env:LUMBERJACKS_COMPANION_BOOTSTRAP_RELEASE = $latestBootstrap.release
        }
    } catch {
        Write-Host "could not read latest bootstrap pointer: $($_.Exception.Message)"
    }
}
if ($ReadOnly) {
    Write-Host 'starting read-only Companion: /valheim will not be mounted and mod updates will not work'
} else {
    $env:LUMBERJACKS_VALHEIM_HOST_PATH = $ValheimPath
    $composeArgs += @('-f', $ValheimComposeFile.Path)
}
$composeArgs += @('up', '-d', '--build')

docker @composeArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        Invoke-RestMethod http://127.0.0.1:8080/health | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $ready) {
    docker logs --tail 100 "$ProjectName-companion-1"
    throw 'Companion did not answer on http://127.0.0.1:8080.'
}

$status = Invoke-RestMethod http://127.0.0.1:8080/api/v0/companion/status
if (-not $ReadOnly -and (-not $status.valheim.found -or -not $status.valheim.config_found)) {
    $status | ConvertTo-Json -Depth 8
    throw 'Companion started, but Valheim/config readiness is not green. Do not use this state for client-pull updates.'
}

$status | ConvertTo-Json -Depth 8
