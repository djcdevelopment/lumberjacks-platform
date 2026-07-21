# ADR 0005: .NET as Authoritative Backend Runtime

## Status

Accepted

## Context

The platform's backend is responsible for authoritative simulation, progression, persistence, transport hosting, and operator-visible explainability.

Key constraints:

- graceful degradation under poor network conditions is a product requirement
- hotspot regions and combat zones will eventually create CPU-heavy authoritative workloads
- long-lived backend services must remain debuggable and operationally legible
- the platform intends to support WebSocket initially and HTTP/3 or QUIC-based transport later
- the project owner has more than a decade of C# experience

The previous TypeScript and Node.js decision optimized for monorepo convenience and early iteration, but it undershoots the actual long-term backend needs of the platform.

## Decision

Use modern .NET and C# as the canonical implementation stack for authoritative backend services.

This includes, by default:

- gateway and transport-facing services
- authoritative simulation and hot-zone compute
- progression and state mutation services
- event ingestion and replay paths
- operator-facing backend APIs when they depend on authoritative state

Other runtimes may still be used for non-authoritative tooling or peripheral utilities, but they must justify themselves against .NET rather than becoming the default by convenience.

## Why

### Runtime fit

The authoritative backend will eventually need to handle some combination of:

- many concurrent connections
- CPU-heavy simulation bursts
- multicore scaling
- sustained long-lived processes
- HTTP/2 and HTTP/3 hosting
- strict observability and replay requirements

Modern .NET is a better default fit for this mix than Node.js.

### Transport fit

ASP.NET Core and Kestrel provide a cleaner server-side path for WebSockets, gRPC, HTTP/2, and HTTP/3. That aligns with ADR 0003.

### Multicore fit

As simulation becomes more interesting, the cost of fighting a single-thread-first runtime model rises. Choosing .NET early avoids spending architecture effort on runtime workarounds instead of product behavior.

### Team fit

Existing C# depth lowers delivery risk more than hypothetical productivity gains from a second primary backend stack.

## Consequences

Positive:
- better fit for CPU-heavy and multicore backend evolution
- cleaner alignment between runtime, transport ambitions, and operator tooling
- less architectural drift caused by convenience choices
- more leverage from existing C# experience
- stronger default foundation for dialup-minimum and graceful-degradation goals

Negative:
- less natural code-sharing with JavaScript-oriented tools or web clients
- some prototypes may feel slower than they would in TypeScript
- any Node-based service experiments now need stronger justification

## Alternatives Considered

- **TypeScript on Node.js**: Fast to scaffold and easy to share types with web tooling, but a weaker default for CPU-heavy authoritative workloads and long-term transport ambitions.
- **Go**: Strong concurrency story and good operational profile, but weaker fit than .NET given existing C# depth and the expected service mix.
- **Rust**: Excellent performance ceiling, but too high a cost in iteration speed and implementation friction for the current stage.

## Follow-On Work

- define which services, if any, are allowed to remain in a secondary runtime
- define profiling and performance budgets for simulation and gateway services
- document concurrency strategy for hotspot zones and region ownership
- align service scaffolding and repo layout with the .NET-first decision
