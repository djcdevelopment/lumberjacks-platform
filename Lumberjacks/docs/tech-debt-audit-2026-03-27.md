# Tech Debt Audit: What We Built to Get Off the Ground

**Date:** 2026-03-27
**Purpose:** Identify scaffolding shortcuts that could limit us moving forward. Prioritized by when they'll bite.

---

## Summary Scorecard

| Category | Severity | When It Breaks | Fix Effort |
|----------|----------|----------------|------------|
| No authentication on any endpoint | **Blocker** | Before any shared deployment | Large |
| Hardcoded DB credentials as fallbacks | **Blocker** | Before any shared deployment | Small |
| Single-instance Gateway assumption | **Blocker** | ~100 concurrent users | Large |
| No rate limiting (WS + UDP + REST) | Limiting | ~10 users (someone will test limits) | Medium |
| In-memory world state (no persistence on restart) | Limiting | Any Gateway restart | Medium |
| No database migration strategy (init.sql only) | Limiting | First schema change after launch | Medium |
| No protocol versioning | Limiting | First breaking protocol change | Small |
| Hardcoded physics constants (server + client) | Limiting | First tuning pass | Medium |
| Fire-and-forget event emission | Limiting | Audit/replay at scale | Medium |
| Event log unbounded growth | Limiting | ~1K events/minute sustained | Medium |
| No load/stress testing baseline | Tech debt | ~100 users (capacity unknown) | Medium |
| No CI/CD pipeline | Tech debt | ~10 deploys/week | Medium |
| Dockerfile builds all services in one stage | Tech debt | ~10 deploys/day | Small |
| Legacy scaffold directories (services/, packages/) | Acceptable | Never (visual clutter only) | Small |

---

## P0: Fix Before Any Shared Deployment

### No Authentication or Authorization

**Where:** Every endpoint in every service.

- `GameWebSocketMiddleware` accepts any WebSocket connection with no auth check
- All REST endpoints (regions, players, structures, events, challenges, guilds) are wide open
- OperatorApi admin endpoints have no auth — anyone can delete regions, modify challenges
- UDP transport accepts any packet with a valid session token (but tokens are easy to obtain)

**Risk:** Anyone who finds the server URL can impersonate players, delete data, or DoS the system.

**Fix path:** JWT or API-key middleware. OperatorApi needs separate admin auth. WebSocket needs auth on upgrade. Estimate: 1-2 weeks.

### Hardcoded Database Credentials as Fallbacks

**Where:**
- `Game.Gateway/Program.cs` line 34
- `Game.OperatorApi/Program.cs` line 10
- `Game.EventLog/Program.cs` line 10

**Code pattern:**
```csharp
config["ConnectionStrings:GameDb"] ?? "Host=localhost;Port=5433;Database=game;Username=game;Password=game"
```

**Risk:** If `ConnectionStrings:GameDb` env var is missing, services silently connect with dev credentials. In a misconfigured production deploy, this could connect to the wrong database or expose credentials in logs.

**Fix:** Remove the fallback. Fail fast if the connection string is missing. Small fix.

---

## P1: Fix Before 100 Users

### Single-Instance Gateway Assumption

**Where:**
- `UdpTransport` binds to fixed port 4005 — can't run two Gateway instances
- `SessionManager` stores all sessions in `ConcurrentDictionary` — no sharing between instances
- `WorldState` is a single in-memory copy — no replication or partitioning
- `InputQueue` is per-process — inputs can't route to a different instance

**Impact:** Gateway is a single point of failure. If it crashes, all sessions and world state are lost. Can't horizontally scale.

**Fix path:** Abstract session store (Redis), distributed world state, UDP endpoint mapping. This is the biggest architectural investment. Estimate: 2-4 weeks depending on approach.

### No Rate Limiting

**Where:**
- WebSocket: `GameWebSocketMiddleware` processes every message immediately, no throttle
- UDP: `UdpTransport.ProcessPacket()` has no per-token rate limit
- REST: All endpoints can be called unlimited times

**Risk:** A single malicious or buggy client can saturate the server with inputs. The `InputQueue` uses `ConcurrentBag` with no max size, so unbounded input spam causes memory growth.

**Fix:** Per-session input rate cap (e.g., 100/sec for player_input, 10/sec for place_structure). Drop excess with a warning. Medium effort.

### No Database Migration Strategy

**Where:** `infra/docker/init.sql` is the only schema definition. No EF Core migrations configured.

**Impact:** Any schema change (new column, new table, index) requires manual SQL and a coordinated stop-the-world deploy. Can't do rolling updates.

**Fix:** Set up EF Core migrations or a migration tool like Flyway/dbmate. Medium effort.

### CORS Allows Any Method

**Where:** `Game.ServiceDefaults/ServiceDefaultsExtensions.cs` lines 20-38.

**Code:** `policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()`

**Risk:** `AllowAnyMethod()` permits DELETE/PATCH from any configured origin. No CSRF protection.

**Fix:** Restrict to GET/POST/PATCH for specific endpoints. Small fix.

---

## P2: Fix Before 1,000 Users

### In-Memory World State Has No Restart Persistence

**Where:** `WorldState.cs` — ConcurrentDictionary for players, regions, structures, items.

**Current behavior:** On Gateway start, `RegionLoader` and `StructureLoader` load from Postgres. But player positions, inventories, and active sessions are lost on restart.

**Impact:** A Gateway restart kicks all players and loses their position. Structures survive (persisted). Player positions don't.

**Fix:** Periodic player state snapshots to Redis or Postgres. Or accept the restart penalty and focus on fast reconnection (current resume flow handles this partially).

### Hardcoded Physics Constants

**Where:**
- Server: `SimulationStep.cs` — `MaxSpeedPerTick = 10.0`, `FrictionPerTick = 2.0`
- Server: `PlayerHandler.cs` — `MaxMoveDistance = 50.0`
- Server: `TickLoop.cs` — `TickMs = 50` (20Hz)
- Client: `player_controller.gd` — `_send_interval = 1.0 / 20.0`
- Plan: `plan-thesis-gold.md` calls out this parity requirement for client prediction

**Impact:** Changing any physics constant requires coordinated server + client deploy. No way to A/B test different values per region.

**Fix:** Send physics constants in `session_started` or `world_snapshot` payload. Server is the source of truth; client reads from there. Medium effort.

### No Protocol Versioning

**Where:** `BinaryEnvelope.cs` — no version field in the binary header. `MessageTypeId.cs` — 6-bit type space (15/63 used).

**Impact:** Any breaking change to the binary wire format instantly breaks all connected clients. No way to support old + new clients simultaneously during rollout.

**Fix:** Add a version byte to the binary envelope header. Small effort for the plumbing, medium effort to handle version negotiation.

### Fire-and-Forget Event Emission

**Where:**
- `PlayerHandler.EmitPlayerEventAsync` — called with `_ = EmitPlayerEventAsync()` (discards Task)
- `PlaceStructureHandler.EmitEventAsync` — same fire-and-forget pattern

**Impact:** Events silently fail if EventLog is unreachable. No retry, no dead letter queue, no indication of lost events. Audit trail has gaps.

**Fix:** At minimum, log failures. Better: local event buffer with retry. Best: outbox pattern with Postgres. Medium effort.

### Event Log Unbounded Growth

**Where:** `events` table — no partitioning, no archival, no retention policy.

**Projection:** At 100 active players generating ~20 events/minute = 2K events/min = 2.8M/day. Table scans degrade.

**Fix:** Time-based partitioning (Postgres native partitioning on `occurred_at`), or archival to cold storage. Medium effort.

### Resume Token in URL

**Where:** `GameWebSocketMiddleware` — resume via `ws://host?resume=TOKEN`

**Risk:** URL parameters appear in server logs, browser history, proxy logs. Token leakage enables session hijacking.

**Fix:** Send resume token as first message after WebSocket connect, not in the URL. Small effort.

---

## P3: Tech Debt (Fix When Convenient)

### No Integration Tests for WebSocket Protocol

**Current coverage:** The [C# unit test suite](Tests.md) for serialization and simulation is fully passing. Zero tests for the WebSocket handshake, session resume, message routing, or binary fast-path end-to-end.

**Risk:** Protocol regressions caught only by manual testing or E2E JS scripts.

### No Load Testing Baseline

**Impact:** Don't know the max concurrent connections per Gateway instance, max tick rate under load, or memory growth profile. Can't capacity plan.

### Dockerfile Builds All Services in One Stage

**Impact:** A change to `Game.Gateway/Program.cs` invalidates the Docker cache for `Game.EventLog` too. Slower CI builds than necessary.

**Fix:** Separate Dockerfiles per service, or use BuildKit cache mounts.

### Stale Player Cleanup Timing

**Where:** `TickLoop.cs` — stale threshold hardcoded at 5 minutes, cleanup every 10 seconds.

**Impact:** Disconnected players stay visible in-world for up to 5 minutes. Inflates region population counts.

### No Health Check Probes

**Where:** Services have `/health` endpoints but no Kubernetes readiness/liveness probe configuration. Azure Container Apps may not detect a hung service.

---

## Acceptable (No Action Needed Now)

### Legacy Scaffold Directories

`services/`, `packages/`, `plugins/` contain only `.gitkeep` files from the pre-.NET era. They don't affect builds or runtime. Can be cleaned up when convenient or repurposed if those services are ever built.

### Standalone Simulation Docker Target

The `simulation` target in the Dockerfile is documented as testing-only (comment added in naming drift fix). It doesn't deploy anywhere and doesn't consume resources.

### Message Type ID Space

6-bit space with 15/63 types used. At current growth rate, years of runway. Expanding to 8 bits is trivial if needed.

---

## Positive Findings (Things That WON'T Limit Us)

These were built well from the start:

- **Binary serialization** — compact, well-tested (106 tests), 84-96% bandwidth reduction
- **Spatial grid indexing** — efficient AoI queries, won't need replacement
- **Input-driven simulation** — correct architecture, deterministic tick loop
- **State hashing** — CRC32 desync detection works, catches drift
- **Session resume** — 2-minute window with token, handles reconnects gracefully
- **UDP transport with token binding** — crypto-random tokens, secure binding
- **Region-scoped broadcasting** — message fan-out already bounded by AoI
- **Graceful degradation** — services handle unreachable dependencies without crashing
- **Godot client architecture** — thin, server-authoritative, correct signal-based update flow
- **Event-driven progression** — decoupled from simulation, clean trigger/evaluate pattern

---

## Recommended Next Steps (In Order)

| Sprint | Focus | Outcome |
|--------|-------|---------|
| **Security pass** | Auth, credentials, rate limiting, CORS | Safe to share the server URL |
| **Deployment pass** | Health probes, DB migrations, Docker optimization | Safe to do rolling updates |
| **Observability pass** | Event reliability, error logging, basic metrics | Know when things break |
| **Scale groundwork** | Session store abstraction, protocol versioning | Path to multi-instance |
