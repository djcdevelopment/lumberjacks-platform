# Retrospective: March 25-27, 2026

**Period:** ~32 hours across 2 calendar days (March 26 13:13 UTC → March 27 17:18 UTC)

## What We Started With

A TypeScript monorepo scaffold. ADRs 0001–0011 documented the architecture vision — thin client, server-authoritative, event-driven progression, community-operated — but no code ran. The `services/` directory had empty placeholder files. The vertical slice was an aspiration in planning docs.

## What We Shipped

A fully deployed, multiplayer-tested game backend running on Azure with a comprehensive [C# unit test suite](Tests.md).

### By the Numbers

| Metric | Value |
|--------|-------|
| Commits | 13 |
| Files changed | 180 |
| Lines added | 12,467 |
| Lines removed | 2,588 |
| .NET projects created | 8 |
| Domain classes | 100+ |
| Unit/integration tests | [C# Suite](Tests.md) (all passing) |
| E2E test scripts | 6 |
| Docker images | 5 |
| Azure services deployed | 4 |
| ADRs written | 2 new (0012, 0013) |
| Docs produced | 11 documents, ~2,000 lines |

### What Got Built (Chronological)

**March 26 — Foundation & Networking**

1. Replaced the entire TypeScript scaffold with a .NET 9 solution: Gateway, Simulation, EventLog, Progression, OperatorApi, Contracts, Persistence, ServiceDefaults.
2. Implemented a complete vertical slice: WebSocket connection → session management → region join → world snapshot → structure placement → event emission → challenge progression → guild points.
3. Completed all 5 phases of the network infrastructure refactoring plan:
   - Binary serialization (BitWriter/BitReader, CompactVec3, 84–96% bandwidth reduction)
   - Input-driven simulation (InputQueue, SimulationStep with deterministic physics, StateHasher)
   - Spatial interest management (SpatialGrid, InterestManager with near/mid/far AoI bands)
   - Client prediction support (binary payloads, input_seq echo for reconciliation)
   - Dual-channel transport (UDP port 4005, WebSocket fallback, session-bound tokens)
4. Wrote the [C# unit test suite](Tests.md) covering protocol serialization, simulation physics, spatial queries, input queuing, and state hashing.
5. Built 6 E2E test scripts: multiplayer (N concurrent WebSocket players), movement, challenges, resume, input broadcast, vertical slice.

**March 27 — Documentation, Deployment, Planning**

1. Wrote ADRs 0012 (binary serialization) and 0013 (dual-channel UDP transport).
2. Wrote simulation audit and retrospective documents.
3. Consolidated standalone Simulation service into Gateway (one fewer container, simpler architecture).
4. Fixed port conflicts (Postgres 5432 → 5433 to coexist with Langfuse).
5. Deployed to Azure Container Apps: 4 services (Gateway external, OperatorApi external, EventLog internal, Progression internal) + PostgreSQL Flexible Server.
6. Fixed Docker cache/tag issues, added proxy endpoints to OperatorApi for remote testing.
7. Proved multiplayer smoke tests pass against live Azure deployment.
8. Made admin-web configurable for remote backends.
9. Wrote comprehensive Azure deployment runbook with PowerShell commands.
10. Wrote 6-phase Godot client plan.

## What Went Well

**Architecture-first approach paid off.** Having ADRs 0001–0011 already written meant implementation decisions were pre-made. The thin-client philosophy, server-authoritative model, and dual-lane transport strategy were settled before a line of code was written. This eliminated debate during implementation and kept scope tight.

**The vertical slice proves the thesis.** A player can connect, walk around, place structures, trigger guild challenges, and see progression evaluated — all through server-authoritative .NET services. The client is truly a thin shell. This is the core bet of the project, and it works.

**Binary protocol achieves the bandwidth goal.** EntityUpdate went from ~200 bytes (JSON) to ~33 bytes (binary). PlayerInput went from ~120 bytes to 5 bytes. The core simulation stream runs at <3.6 KB/s per client, well within the 28.8k "dialup" constraint from ADR 0003. This was a design goal from day one, and it's met.

**Testing kept pace with development.** The [C# test suite](Tests.md) isn't exhaustive, but it covers the critical paths: serialization round-trips, physics determinism, spatial queries, input ordering, and state hashing. The 6 E2E scripts validate the full stack end-to-end. This confidence enabled aggressive refactoring without fear.

**Azure deployment was smooth (enough).** Container Apps + Flexible Server PostgreSQL is a good fit. Scale-to-zero keeps costs at ~$25/month for testing. The deployment runbook captures every gotcha encountered (Docker cache, tag reuse, provider registration, location restrictions).

## What Was Hard

**Docker layer caching is deceptive.** The multi-stage Dockerfile caches aggressively. After code changes, `docker build` would happily serve stale layers with old binaries. We learned to always use `--no-cache` for deploys and unique image tags (not just `latest`) to force Azure to pull fresh images. This cost ~30 minutes of debugging.

**Port conflicts compound.** Moving Postgres from 5432 to 5433 required changes in 13 files (connection strings in every appsettings.json, docker-compose files, fallback strings in every Program.cs). A central configuration source would help, though for now it's manageable.

**appsettings.json collision in Docker publish.** When Gateway references Simulation as a project, both have appsettings.json. The publish step fails (NETSDK1152). The fix — `CopyToPublishDirectory=Never` on Simulation's config — is non-obvious and took trial-and-error to find.

**Azure CLI path issues on Windows.** The az CLI was installed but not on PATH, and its `cmd` launcher can't handle spaces in `Program Files`. We created a Python wrapper script. This is a Windows-specific paper cut.

**Internal services aren't directly testable from outside Azure.** EventLog and Progression use internal ingress, so test scripts that hit them directly fail when targeting Azure. We added proxy endpoints to OperatorApi to solve this, but it was an unexpected gap.

## What We'd Do Differently

**Tag Docker images from the start.** Using `latest` for initial deployment was fine, but it became a problem immediately on the first update. Timestamp-based tags should be the default.

**Centralize connection string configuration.** Having the Postgres connection string hardcoded in 8+ locations is fragile. A shared environment file or Aspire-style configuration would reduce the blast radius of changes like port moves.

**Test against Azure earlier.** We built the full stack locally, then hit several deployment-specific issues (Docker cache, internal ingress routing, CORS). Deploying a single service to Azure early would have surfaced these sooner.

## Key Decisions Made

| Decision | Rationale | Confidence |
|----------|-----------|------------|
| .NET over TypeScript | Better for server-authoritative simulation (strong typing, perf, binary serialization) | High |
| Simulation in-process in Gateway | Eliminates network hop for tick loop, simpler deployment | High |
| JSON-first protocol with binary upgrade path | Debuggable during development, binary already proven for hot paths | High |
| PostgreSQL on 5433 | Pragmatic — avoids conflict with Langfuse on 5432, no deeper reason | Medium |
| Azure Container Apps over VM | Scale-to-zero, managed TLS, ~$25/mo vs always-on VM cost | High |
| Godot client next (not more Azure work) | Backend is proven, need to validate the client loop end-to-end | High |

## State of the Project

**Backend:** Production-deployed and passing all smoke tests. The architecture is proven. No known bugs.

**Client:** No game client exists yet. The Node.js test scripts prove the protocol works. The Godot client plan is written and ready to execute.

**Risk register:**
- Godot WebSocket + JSON performance at 20Hz updates (likely fine, but unproven)
- Movement feel with server-only authority and no client prediction (acceptable for building/survival, might need prediction for combat later)
- Single-replica Gateway means no horizontal scaling yet (fine for friend testing)

## What's Next

Execute the Godot client vertical slice plan (`docs/godot-client-plan.md`). Six phases:

1. WebSocket connection and session management
2. World rendering from server snapshot
3. WASD movement with server-authoritative physics
4. Structure placement with build mode
5. Reconnection and HUD
6. Azure testing and .exe export

The goal: a playable .exe a friend can download, point at the Azure backend, and walk around placing campfires together.
