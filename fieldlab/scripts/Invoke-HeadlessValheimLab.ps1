#Requires -Version 5.1
<#
.SYNOPSIS
Run one disposable, profile-gated Valheim lab client without a keyboard.

.DESCRIPTION
This is the bounded local vertical slice:

  refresh -> stage the built DLL/config into the shared read-only client payload
  start   -> start one Compose client; the container launches Valheim and the
             opt-in LabAutoJoin patch selects an existing character
  status  -> show container state and recent lifecycle lines
  stop    -> stop the client and verify the container is no longer running

After the client joins, use the existing local MCP tools to observe JSONL and
send only allowlisted mod commands. This script deliberately does not provide
arbitrary command execution inside Valheim.

The Compose client profile is the only supported auto-join environment. OMEN/i5
player installs are not modified by this script.
#>
[CmdletBinding()]
param(
    [ValidateSet('01', '02', '03', '04')]
    [string] $Client = '01',

    [ValidateSet('refresh', 'start', 'status', 'stop', 'restart')]
    [string] $Action = 'status',

    [string] $ConfigPath = '',

    [string] $EnvFile = '',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$composeFile = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.compose.yml'
$defaultEnv = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.env'
$exampleEnv = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.env.example'
$sharedRoot = Join-Path $repoRoot 'fieldlab\autonomous\state\client-shared'
$dllSource = Join-Path $repoRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
$clientInitSource = Join-Path $repoRoot 'fieldlab\autonomous\client-init\20-comfy-valheim-autostart.sh'
$clientService = "valheim-client-$Client"
$clientHome = Join-Path $repoRoot "fieldlab\autonomous\state\client$Client\home"

if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) { throw "Compose file not found: $composeFile" }
if (-not $EnvFile) { $EnvFile = if (Test-Path -LiteralPath $defaultEnv -PathType Leaf) { $defaultEnv } else { $exampleEnv } }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "Compose env file not found: $EnvFile" }

function Invoke-Compose([string[]] $Arguments) {
    & docker compose --env-file $EnvFile -f $composeFile --profile clients @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed: $($Arguments -join ' ')" }
}

function Copy-Verified([string] $Source, [string] $Target) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "payload file not found: $Source" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw "payload verification failed: $Target" }
    Write-Host ("staged {0} sha256:{1}" -f $Target, $sourceHash.Substring(0, 12).ToLowerInvariant())
}

function Refresh-Payload {
    if (-not $NoBuild) {
        Push-Location $repoRoot
        try {
            & dotnet build '.\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj' -c Release
            if ($LASTEXITCODE -ne 0) { throw 'ComfyNetworkSense build failed' }
        } finally { Pop-Location }
    }
    Copy-Verified $dllSource (Join-Path $sharedRoot 'plugins\ComfyNetworkSense.dll')
    if ($ConfigPath) {
        $resolvedConfig = (Resolve-Path $ConfigPath).Path
        Copy-Verified $resolvedConfig (Join-Path $sharedRoot 'config\djcdevelopment.valheim.comfynetworksense.cfg')
    } else {
        Write-Host 'no config staged; disposable clients use their existing/default config'
    }
    $routeSource = Join-Path $repoRoot 'network\mod\ComfyNetworkSense\bin\Release\teleport-route.tsv'
    if (Test-Path -LiteralPath $routeSource -PathType Leaf) {
        Copy-Verified $routeSource (Join-Path $sharedRoot 'comfy-network-sense\teleport-route.tsv')
    }
}

function Stage-ClientInit {
    New-Item -ItemType Directory -Force -Path (Join-Path $clientHome 'init.d') | Out-Null
    Copy-Verified $clientInitSource (Join-Path $clientHome 'init.d\20-comfy-valheim-autostart.sh')
}

switch ($Action) {
    'refresh' {
        Stage-ClientInit
        Refresh-Payload
        break
    }
    'start' {
        New-Item -ItemType Directory -Force -Path $sharedRoot | Out-Null
        Stage-ClientInit
        if (-not (Test-Path -LiteralPath (Join-Path $sharedRoot 'plugins\ComfyNetworkSense.dll') -PathType Leaf)) {
            Refresh-Payload
        }
        Invoke-Compose @('up', '-d', $clientService)
        Write-Host "started $clientService; inspect lifecycle with -Action status and MCP telemetry after join"
        break
    }
    'restart' {
        Stage-ClientInit
        Invoke-Compose @('stop', '-t', '60', $clientService)
        Refresh-Payload
        Invoke-Compose @('up', '-d', $clientService)
        Write-Host "restarted $clientService with verified shared payload"
        break
    }
    'stop' {
        Invoke-Compose @('stop', '-t', '60', $clientService)
        $running = (& docker ps --filter "name=$clientService" --filter 'status=running' --format '{{.Names}}')
        if ($running) { throw "client container is still running after graceful stop: $running" }
        Write-Host "stopped $clientService"
        break
    }
    'status' {
        Invoke-Compose @('ps', $clientService)
        Invoke-Compose @('logs', '--tail', '80', $clientService)
        break
    }
}
