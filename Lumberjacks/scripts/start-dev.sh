#!/usr/bin/env bash
set -e

echo "=== Community Survival Platform - Local Dev ==="
echo ""
echo "Services will start on:"
echo "  Gateway:      http://localhost:4000  (+ WebSocket + in-process Simulation)"
echo "  Event Log:    http://localhost:4002"
echo "  Progression:  http://localhost:4003"
echo "  Operator API: http://localhost:4004"
echo "  Admin Web:    http://localhost:5173"
echo ""

# Start .NET services + admin-web
npx concurrently \
  --names "gateway,events,progress,op-api,admin" \
  --prefix-colors "blue,yellow,magenta,cyan,white" \
  "dotnet run --project src/Game.Gateway" \
  "dotnet run --project src/Game.EventLog" \
  "dotnet run --project src/Game.Progression" \
  "dotnet run --project src/Game.OperatorApi" \
  "npm run dev -w @game/admin-web"
