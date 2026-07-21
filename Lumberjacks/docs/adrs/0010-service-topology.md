# ADR 0010: Monorepo Service Topology

**Status:** Accepted
**Date:** 2026-03-26
**Depends on:** ADR 0005 (.NET Runtime)

## Context

The platform backend needs to be decomposed into services. Too few services and we get a monolith that's hard to scale or reason about. Too many and we get operational overhead that a solo founder can't sustain. The decomposition must reflect actual trust and scaling boundaries, not hypothetical future needs.

## Decision

Four deployed services in a single monorepo, with three shared libraries:

### Services

| Service | Port | Responsibility | State |
|---------|------|---------------|-------|
| **Game.Gateway** | 4000 (WS/HTTP), 4005 (UDP) | Unified host: WebSocket, UDP transport, in-process simulation (tick loop, physics, broadcasting), session management | In-memory (sessions, regions, players) |
| **Game.EventLog** | 4002 | Append-only event ingestion and query | PostgreSQL (events table) |
| **Game.Progression** | 4003 | Consume events, update player/guild progress | PostgreSQL (player_progress, guild_progress) |
| **Game.OperatorApi** | 4004 | Admin dashboard backend, proxies to other services, health aggregation | Proxies + PostgreSQL read |

> **Note:** `Game.Simulation` exists as a library project referenced by Gateway (runs in-process). It can also run standalone on port 4001 for isolated HTTP-only testing, but is **not deployed** as a separate service.

### Shared Libraries

| Library | Purpose |
|---------|---------|
| **Game.Contracts** | Entities, events, protocol types, message classification — zero dependencies |
| **Game.Persistence** | EF Core DbContext, entity mappings — depends on Contracts |
| **Game.ServiceDefaults** | Health checks, CORS, JSON config, HttpClient — depends on Contracts |

### Dependency Graph

```
Game.Contracts (no deps)
    ↑
Game.Persistence (→ Contracts, Npgsql.EF)
    ↑
Game.ServiceDefaults (→ Contracts)
    ↑
All services (→ Contracts, Persistence*, ServiceDefaults)
```
*Gateway and Simulation don't reference Persistence (no DB access).

## Rationale

**Four deployed services, not one or fifteen.** Each service maps to a distinct scaling and failure domain:
- Gateway scales with connection count and world complexity (simulation runs in-process for minimal latency)
- EventLog scales with write throughput
- Progression scales with event consumption rate
- OperatorApi is low-traffic admin tooling

**Monorepo, not polyrepo.** A single `Game.sln` enables atomic refactors across service boundaries, shared type definitions via `Game.Contracts`, and a single `dotnet build` to verify everything compiles. The solo-founder context makes polyrepo coordination overhead unjustifiable.

**Shared Contracts library.** Type definitions (entities, events, protocol messages) are shared at compile time rather than duplicated or generated. Changes to wire format are caught by the compiler across all services simultaneously.

**Service-to-service communication is HTTP.** OperatorApi proxies to Gateway, EventLog, and Progression over HTTP. No message bus, no service mesh, no RPC framework. HTTP is debuggable with curl, observable in logs, and sufficient for the current scale.

## Consequences

- All services deploy independently but are developed in the same repo
- `Game.Contracts` changes require rebuilding all downstream projects (by design — catches breaking changes)
- Adding a new service means: create project, add to `Game.sln`, reference shared libraries, pick a port
- The `npm run dev` script starts all 4 .NET services + admin-web via concurrently
- Future: services may move to separate processes, containers, or hosts — the HTTP boundaries make this straightforward
