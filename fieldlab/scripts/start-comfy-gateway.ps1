# Start the Baseline comfy_gateway MCP server (streamable-http on 127.0.0.1:8721).
# This is the door the Claude session reads Valheim/ComfyNetworkSense telemetry through.
# Must be running BEFORE Claude Code starts for the `.mcp.json` comfy-gateway entry to connect.
#
#   .\fieldlab\scripts\start-comfy-gateway.ps1            # foreground
#   Start-Process powershell -ArgumentList '-File .\fieldlab\scripts\start-comfy-gateway.ps1'  # detached
#
# Health check:  curl http://127.0.0.1:8721/healthz   ->  {"ok":true,...}
# Auth header:   X-Comfy-Key: comfy-dev-local  (see network/mcp/comfy_gateway/etc/callers.json)

$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))  # the baseline repo root
$mcp = Join-Path $repo "network\mcp"
$port = if ($env:COMFY_MCP_PORT) { [int]$env:COMFY_MCP_PORT } else { 8721 }
$py = if ($env:COMFY_GATEWAY_PYTHON) {
    $env:COMFY_GATEWAY_PYTHON
} else {
    $command = Get-Command python -ErrorAction Stop
    $command.Source
}

$env:PYTHONPATH = $mcp
$env:PYTHONUTF8 = "1"
$env:COMFY_MCP_ROOT = $mcp
$env:COMFY_MCP_PROFILE = if ($env:COMFY_MCP_PROFILE) { $env:COMFY_MCP_PROFILE } else { "Dev" }
$env:COMFY_MCP_HOST_PORT = [string]$port
$revision = (& git -C $repo rev-parse HEAD 2>$null | Select-Object -First 1)
$branch = (& git -C $repo branch --show-current 2>$null | Select-Object -First 1)
$dirty = (& git -C $repo status --porcelain 2>$null)
$env:COMFY_MCP_SOURCE_REVISION = if ($revision) { $revision.ToString().Trim() } else { "unknown" }
$env:COMFY_MCP_SOURCE_BRANCH = if ($branch) { $branch.ToString().Trim() } else { "unknown" }
$env:COMFY_MCP_SOURCE_DIRTY = if ($dirty) { "true" } else { "false" }
$env:COMFY_MCP_IMAGE = "native-baseline:$($env:COMFY_MCP_SOURCE_REVISION)"

Write-Host "Starting Baseline comfy-gateway on http://127.0.0.1:$port/mcp ..."
& $py -m comfy_gateway.kernel.gateway `
    --providers comfy_gateway.toolsurface.valheim,comfy_gateway.toolsurface.inference `
    --host 127.0.0.1 --port $port
