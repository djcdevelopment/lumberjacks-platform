# Start the Lumberjacks Gateway (ASP.NET Core, HTTP :4000 + UDP :4005) for the P4/I3 redirect.
# The 0.0.0.0 bind is REQUIRED: appsettings.json defaults to localhost-only, and the redirect
# POSTs arrive from the am4 server container over the tailnet (OMEN tailnet IP 100.124.12.37).
#
#   .\fieldlab\scripts\start-lumberjacks-gateway.ps1            # foreground
#   Start-Process powershell -ArgumentList '-File .\fieldlab\scripts\start-lumberjacks-gateway.ps1'  # detached
#
# Health check:   curl http://127.0.0.1:4000/health          -> {"status":"ok","service":"gateway",...}
# Receipt gate:   curl http://127.0.0.1:4000/valheim/zdo-redirect/status
# From am4:       curl http://100.124.12.37:4000/health      (verified 2026-07-10, incl. from
#                 inside the valheim-server container)
#
# Postgres is optional at boot (loaders log a warning and continue); the redirect receipt
# counters are in-memory singletons and need no DB.

$ErrorActionPreference = "Stop"
$dotnet = "C:\work\dotnet9\dotnet.exe"   # PATH dotnet is 8.x; the repo needs the 9.x SDK
$repo   = "C:\work\Lumberjacks"

$env:DOTNET_ROOT = "C:\work\dotnet9"

Write-Host "Starting Lumberjacks gateway on http://0.0.0.0:4000 (UDP :4005) ..."
Set-Location $repo
& $dotnet run --project src/Game.Gateway -- --urls http://0.0.0.0:4000
