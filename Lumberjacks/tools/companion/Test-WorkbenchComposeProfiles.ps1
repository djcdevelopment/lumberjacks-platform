<#
.SYNOPSIS
Prove the local Compose profile boundary for Workbench v1.
#>
[CmdletBinding()]
param(
    [string]$ComposeFile = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ComposeFile)) { $ComposeFile = Join-Path $PSScriptRoot 'docker-compose.yml' }
$compose = (Resolve-Path -LiteralPath $ComposeFile).Path
$baseServices = @(docker compose -p workbench-profile-test -f $compose config --services)
if ($LASTEXITCODE -ne 0) { throw 'base Compose config failed' }
$devServices = @(docker compose -p workbench-profile-test -f $compose --profile dev config --services)
if ($LASTEXITCODE -ne 0) { throw 'Dev Compose config failed' }
$productionServices = @(docker compose -p workbench-profile-test -f $compose --profile production config --services)
if ($LASTEXITCODE -ne 0) { throw 'Production Compose config failed' }
$baseJson = docker compose -p workbench-profile-test -f $compose config --format json | Out-String
$devJson = docker compose -p workbench-profile-test -f $compose --profile dev config --format json | Out-String
$productionJson = docker compose -p workbench-profile-test -f $compose --profile production config --format json | Out-String
$baseConfig = $baseJson | ConvertFrom-Json
$devConfig = $devJson | ConvertFrom-Json
$companionPort = @($baseConfig.services.companion.ports) | Select-Object -First 1
$devMcpPort = @($devConfig.services.'dev-mcp'.ports) | Select-Object -First 1
if ($companionPort.host_ip -ne '127.0.0.1' -or [int]$companionPort.target -ne 8080) { throw 'Companion is not loopback-bound' }
if ($devMcpPort.host_ip -ne '127.0.0.1' -or [int]$devMcpPort.target -ne 8720) { throw 'Dev profile has no loopback MCP publication' }
if ($productionJson -match 'dev-mcp|workbench-tool-runner|COMFY_WORKBENCH_MCP_PORT') { throw 'Production profile retains Dev MCP/tool-runner configuration' }
if ($baseServices -contains 'dev-mcp' -or $baseServices -contains 'workbench-tool-runner') { throw 'default profile starts a Dev-only service' }
if (-not ($devServices -contains 'dev-mcp') -or -not ($devServices -contains 'workbench-tool-runner')) { throw 'Dev profile is missing its bounded services' }
if ($productionServices -contains 'dev-mcp' -or $productionServices -contains 'workbench-tool-runner') { throw 'Production profile includes a Dev-only service' }
$json = docker compose -p workbench-profile-test -f $compose --profile dev config --format json | Out-String
if ($json -match 'docker\.sock|/var/run/docker') { throw 'Compose profile exposes the Docker socket' }
$launcherSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Start-LocalCompanion.ps1') -Raw
if ($launcherSource -notmatch "Profile -eq 'Lab'" -or $launcherSource -notmatch 'http://host\.docker\.internal:4000') {
    throw 'Lab launcher does not default to the local Docker Gateway'
}
$localCompose = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\infra\docker\docker-compose.yml')).Path
$localJson = docker compose -p workbench-local-gateway-test -f $localCompose config --format json | Out-String
if ($LASTEXITCODE -ne 0) { throw 'local Gateway Compose config failed' }
$localConfig = $localJson | ConvertFrom-Json
if ($localConfig.services.gateway.environment.LUMBERJACKS_MODPACK_MANIFEST -ne '/data/modpack/current.json') {
    throw 'local Gateway does not consume the persistent Lab modpack pointer'
}
$publisher = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\modpack\Publish-LocalModpack.ps1')).Path
$dryRun = & $publisher `
    -ReleaseId 'm999-workbenchfixture-20260801-r1' `
    -ModRelease 'm999-workbenchfixture-20260801-r1' `
    -PackagePath $compose `
    -DryRun
if ($dryRun.verdict -ne 'dry_run' -or $dryRun.valheim_files_changed -ne $false -or
    $dryRun.manifest.package_sha256 -ne (Get-FileHash -LiteralPath $compose -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'local modpack publisher dry-run contract failed'
}
[pscustomobject]@{
    schema_version = 1
    verdict = 'passed'
    default_services = $baseServices
    dev_services = $devServices
    production_services = $productionServices
    docker_socket = 'absent'
    lab_gateway = 'http://host.docker.internal:4000'
    local_modpack_pointer = '/data/modpack/current.json'
    local_publisher_dry_run = 'passed'
} | ConvertTo-Json -Depth 8
