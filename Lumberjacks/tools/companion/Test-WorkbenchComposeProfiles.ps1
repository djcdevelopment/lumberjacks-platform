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
[pscustomobject]@{
    schema_version = 1
    verdict = 'passed'
    default_services = $baseServices
    dev_services = $devServices
    production_services = $productionServices
    docker_socket = 'absent'
} | ConvertTo-Json -Depth 8
