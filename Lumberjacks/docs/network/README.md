# Lumberjacks Network Infrastructure

The network infrastructure is the core of Lumberjacks. It is a server-authoritative,
input-driven simulation stack designed to remain useful on constrained links and to
degrade without surrendering server ownership of truth. Godot is a client of this
infrastructure, not the place where authoritative simulation lives.

## The system in one pass

```text
player intent
    -> 5-byte binary input
    -> gateway admission and session routing
    -> tick-aligned input queue
    -> 20 Hz authoritative simulation
    -> state hash and input acknowledgement
    -> spatial interest filtering
    -> compact entity update
    -> UDP when bound, binary WebSocket fallback, JSON compatibility fallback
    -> Godot rendering and interpolation
```

The architecture separates four concerns that are easy to blur together:

1. **Simulation authority** — clients send intent; the server computes state.
2. **Representation** — hot-path state is bit-packed into compact binary payloads.
3. **Delivery** — message semantics select reliable or disposable delivery lanes.
4. **Visibility** — each client receives only the update rate its area of interest needs.

## Read by question

| Question | Document |
|---|---|
| What are the components and boundaries? | [Architecture](architecture.md) |
| In what order was this probably built? | [Build reconstruction](build-reconstruction.md) |
| How are messages represented? | [Protocol and compression](protocol-and-compression.md) |
| Who owns physics and state? | [Deterministic simulation](deterministic-simulation.md) |
| How do UDP and WebSocket cooperate? | [Transport and degradation](transport-and-degradation.md) |
| How is per-client traffic bounded? | [Interest management](interest-management.md) |
| What did the live Valheim P7 cutover prove, and how does it flow? | [Valheim + Lumberjacks P7 victory and overview](valheim-lumberjacks-p7-overview.md) |
| How does P7 become a safe volunteer and multiplayer test platform? | [Valheim volunteer cutover platform plan](valheim-volunteer-platform-plan.md) |
| Where can I scan current gates and implementation notes? | [Living Valheim roadmap](../../src/Game.Gateway/Community/roadmap.html) (also served at `/roadmap`) |
| What has actually been measured? | [Validation](validation.md) |
| What does Godot own? | [Godot integration](godot-integration.md) |
| Which artifacts support each claim? | [Evidence index](evidence-index.md) |

## How new mechanics enter the system

Terrain and tree felling follow a second, domain-facing pipeline:

```text
source research -> pure simulation -> interactive lab -> parameter validation
                -> compact projection -> protocol integration -> live client
```

That method is documented in the [labs index](../labs/README.md). A lab result is
not automatically a shipped network feature. The evidence index records whether a
mechanic is researched, simulated, visualized, projected, serialized, transported,
or integrated end to end.

## Historical honesty

Most of the original network implementation landed in commit `22b7292` on
2026-03-26: 75 files and 7,521 additions covering protocol, simulation, transport,
tests, deployment, and planning. Several ADRs were written on March 27–28. The
repository therefore proves what exists and roughly when, but not a trustworthy
fine-grained construction order.

This documentation uses three evidence labels:

- **Recorded** — directly stated in a contemporaneous document or result.
- **Observed** — directly visible in current code or tests.
- **Reconstructed** — an ordering inferred from technical dependencies.

Commit messages are supporting evidence, not the sole source of truth.
