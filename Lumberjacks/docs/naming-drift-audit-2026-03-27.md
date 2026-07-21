# Naming Drift Audit: Simulation Consolidation

**Date:** 2026-03-27
**Context:** Game.Simulation was consolidated to run in-process inside Game.Gateway (eliminating the HTTP-per-move hop). Several files still referenced it as a standalone service on port 4001. This audit documents every finding and its resolution.

---

## Background

The original architecture (ADR 0010) defined 5 services including a standalone `Game.Simulation` on port 4001. During the network refactor, Simulation was consolidated into Gateway to eliminate inter-service latency on the hot path (player input → simulation step → broadcast). The project now deploys **4 services**: Gateway (4000/4005), EventLog (4002), Progression (4003), OperatorApi (4004).

`Game.Simulation` still exists as a .NET project and can run standalone for HTTP-only testing, but it is **not deployed** as a separate service.

---

## Fixed (commit 9068486)

| File | Issue | Fix |
|------|-------|-----|
| `infra/docker/docker-compose.dev.yml` | Defined standalone `simulation` service (lines 20-32) and `ServiceUrls__Simulation` env vars in gateway and operatorapi | Removed simulation service block and both `ServiceUrls__Simulation` entries |
| `src/Game.OperatorApi/Endpoints/StatusEndpoints.cs` | Duplicate "simulation" health check hitting the same Gateway URL as "gateway" | Removed the redundant entry; 3 health checks remain (gateway, event-log, progression) |
| `docs/adrs/0010-service-topology.md` | Listed 5 services with Simulation on port 4001 in the table | Updated to "4 deployed services", added note about Simulation as in-process library, updated rationale and service count |
| `docs/repo-layout.md` | Listed Simulation as "Port 4001 — standalone simulation" and Godot client as "Not yet built" | Fixed Simulation description to "Library (in-process in Gateway)", updated Godot to "Vertical slice complete" |
| `docs/current-focus.md` | "Simulation (port 4001) can also run standalone..." implied deployment | Reworded to clarify "not deployed as a separate service" |
| `Dockerfile` | Simulation build target existed with no context | Added comment: "Simulation runs in-process inside Gateway in production. This standalone target is kept for isolated HTTP-only testing." |
| `README.md` | Listed .NET 8, referenced 5 services, outdated "What's Planned" section, missing Godot/Azure achievements | Updated to .NET 9, 4 deployed services, added Azure/Godot to accomplishments, updated planned work to Thesis Gold |

---

## Acceptable (Intentionally Kept)

These reference port 4001 or standalone Simulation but are correct in context:

| File | Reference | Why It's OK |
|------|-----------|-------------|
| `src/Game.Simulation/appsettings.json` | `"Urls": "http://localhost:4001"` | The standalone project's own config for HTTP-only testing mode |
| `.env.example` | `SIMULATION_PORT=4001`, `SIMULATION_URL=http://localhost:4001` | Example config for local dev; not used in deployed architecture |
| `Dockerfile` (simulation target) | Build target on port 4001 | Kept for isolated testing; now has explanatory comment |
| `.claude/settings.local.json` | Permission entries for `localhost:4001` health/regions | Claude Code dev harness permissions for local testing |
| `src/Game.Contracts/Protocol/NullTickBroadcaster.cs` | "No-op ITickBroadcaster for standalone Simulation" | Accurately describes the testing use case |

---

## Ambiguous (Legacy Artifacts)

These are from the pre-.NET era and reference a planned architecture that was never built:

| File | Reference | Context |
|------|-----------|---------|
| `scripts/check-workspace.ps1` | `'services\\simulation'` in scaffold path list | Entire script checks for a legacy scaffold layout (`services/gateway`, `packages/schemas`, `plugins/`, etc.) that was superseded by the `src/Game.*` .NET structure. None of these paths exist. The script is inert — it would fail on every path, not just simulation. |
| `Handoff.md` (line 77) | `4. services/simulation` in "Recommended First Build Order" | Historical planning doc from before the .NET migration. The `services/` layout was replaced by `src/Game.*`. This is a snapshot of the original plan, not active guidance. |

**Recommendation:** These files are historical. If they cause confusion for future contributors, they can be deleted or annotated with a "superseded" header. They don't affect builds, deployments, or runtime behavior.

---

## Verified Correct (No Action Needed)

These files already correctly reflect the 4-service architecture:

- `infra/docker/docker-compose.yml` — Lists gateway, eventlog, progression, operatorapi (no simulation)
- `docs/deployment-strategy.md` — "There is no separate Simulation service to deploy"
- `docs/azure-deployment-runbook.md` — "4 services — simulation runs in-process in the Gateway"
- `scripts/start-dev.sh` — Lists 4 services, notes Gateway runs simulation
- `scripts/start-all.ps1` — Lists 4 services with in-process simulation comment
- `src/Game.Gateway/Program.cs` — "Simulation services (in-process — eliminates HTTP-per-move hop)"
- `src/Game.OperatorApi/Endpoints/ProxyEndpoints.cs` — "Simulation endpoints proxy to Gateway (which runs the simulation in-process)"
- `package.json` `dev` script — Starts 4 .NET services (Gateway, EventLog, Progression, OperatorApi)

---

## Why This Matters Going Forward

**Risk:** The Simulation project still builds, still has its own `Program.cs`, and can still run on port 4001. This is useful for testing but creates a naming trap: new code or docs might accidentally treat it as a deployed service again.

**Rules of thumb:**
1. **Never add `ServiceUrls__Simulation`** to docker-compose or Azure config — Gateway IS the simulation
2. **Never deploy the simulation Docker target** to Azure Container Apps — it's a testing convenience
3. **Health checks should hit Gateway**, not a separate simulation endpoint
4. **When documenting service count**, say "4 deployed services" (Gateway, EventLog, Progression, OperatorApi)
5. **Port 4001** is a dev/test-only port — it should never appear in production configs or deployment docs
6. **`Game.Simulation` is a library** that Gateway references — the standalone HTTP mode is for isolation testing only

**If Simulation ever needs to be split out again** (e.g., for horizontal scaling of the tick loop), that would be a new ADR superseding the consolidation, not a revert.
