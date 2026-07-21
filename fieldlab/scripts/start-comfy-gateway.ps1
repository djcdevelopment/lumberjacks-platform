# Start the comfy_gateway MCP server (streamable-http on 127.0.0.1:8720).
# This is the door the Claude session reads Valheim/ComfyNetworkSense telemetry through.
# Must be running BEFORE Claude Code starts for the `.mcp.json` comfy-gateway entry to connect.
#
#   .\fieldlab\scripts\start-comfy-gateway.ps1            # foreground
#   Start-Process powershell -ArgumentList '-File .\fieldlab\scripts\start-comfy-gateway.ps1'  # detached
#
# Health check:  curl http://127.0.0.1:8720/healthz   ->  {"ok":true,...}
# Auth header:   X-Comfy-Key: comfy-dev-local  (see network/mcp/comfy_gateway/etc/callers.json)

$ErrorActionPreference = "Stop"
$py  = "C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe"  # PATH python is a broken Store stub
$mcp = "C:\work\comfy\network\mcp"

$env:PYTHONPATH = $mcp
$env:PYTHONUTF8 = "1"

Write-Host "Starting comfy-gateway on http://127.0.0.1:8720/mcp ..."
# matrix restored 2026-07-09 (the original restore silently dropped it; all its env vars have defaults)
& $py -m comfy_gateway.kernel.gateway `
    --providers comfy_gateway.toolsurface.valheim,comfy_gateway.toolsurface.inference,comfy_gateway.toolsurface.matrix `
    --host 127.0.0.1 --port 8720
