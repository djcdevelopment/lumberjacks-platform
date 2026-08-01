<#
.SYNOPSIS
Reports whether a local Workbench profile has an unmanaged Dev MCP listener.

.DESCRIPTION
This is a read-only boundary check. It validates the Compose service cutline and
reports any host process already listening on the configured Dev MCP port. It
never stops or reconfigures that process because it may belong to HEARTH or an
independent operator environment.
#>
[CmdletBinding()]
param(
    [ValidateSet('Explore','Admin','Dev','Lab','Production')]
    [string]$Profile = 'Explore',
    [ValidateRange(1024,65535)]
    [int]$McpPort = 8721,
    [string]$ComposeFile = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = Join-Path $PSScriptRoot '..\..\Lumberjacks\tools\companion\docker-compose.yml'
}
$ComposeFile = (Resolve-Path -LiteralPath $ComposeFile).Path
$baseServices = @(docker compose -p workbench-boundary-check -f $ComposeFile config --services)
$productionServices = @(docker compose -p workbench-boundary-check -f $ComposeFile --profile production config --services)
if ($baseServices -contains 'dev-mcp' -or $productionServices -contains 'dev-mcp') {
    throw 'Compose boundary includes Dev MCP in the default or Production profile.'
}

$listeners = @(Get-NetTCPConnection -LocalPort $McpPort -State Listen -ErrorAction SilentlyContinue)
$processes = @($listeners | ForEach-Object {
    $process = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $_.OwningProcess) -ErrorAction SilentlyContinue
    [ordered]@{
        port = $McpPort
        pid = $_.OwningProcess
        process = if ($process) { $process.Name } else { $null }
        command_line = if ($process) { $process.CommandLine } else { $null }
    }
})
$external = $processes.Count -gt 0
$verdict = if ($external -and $Profile -in @('Dev','Lab')) { 'mcp_port_collision' }
           elseif ($external) { 'external_mcp_listener_present' }
           else { 'passed' }

[ordered]@{
    schema_version = 1
    profile = $Profile
    mcp_port = $McpPort
    default_services = $baseServices
    production_services = $productionServices
    external_listener = $external
    listeners = $processes
    verdict = $verdict
    action = if ($external) { 'Identify the owner before stopping it; use -McpPort <free-port> for Workbench Dev/Lab.' } else { 'No unmanaged MCP listener observed.' }
} | ConvertTo-Json -Depth 8

if ($verdict -ne 'passed') { exit 2 }
