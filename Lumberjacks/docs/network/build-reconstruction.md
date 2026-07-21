# Network Build Reconstruction

This is a dependency-based reconstruction, not a claim that the Git commits preserve
the exact order of work.

## What the history can prove

| Date | Recorded transition |
|---|---|
| 2026-03-24 | Repository bootstrap and the initial multi-lane/.NET ADR direction |
| 2026-03-26 | TypeScript scaffolds replaced by a working .NET vertical slice |
| 2026-03-26 | Network refactor landed as one 75-file, 7,521-line addition |
| 2026-03-27 | Binary serialization, UDP, simulation audit, deployment, and Godot planning documented |
| 2026-03-28 | Dual-channel load results and missing simulation/spatial ADRs added |
| 2026-03-29 | Nature 2.0 C# Godot client proved against the shared contracts |
| 2026-03-30–31 | Terrain and tree-felling labs developed; compact tree projection demonstrated |
| 2026-05-06–07 | Replay viewer built on the older Godot project as a separate consumer path |
| 2026-07-08–10 | Valheim priority, redirect, injection, and handshake work extended the gateway |

## Reconstructed construction order

### 1. Establish authority and delivery semantics

**Recorded:** ADRs 0001, 0003, 0005, and 0008 define a thin client, a .NET
authoritative backend, and reliable/datagram message classes.

**Why it must precede the rest:** compression and transport choices cannot be judged
until the system knows which state is authoritative and which updates may be lost.

### 2. Replace position submission with intent

**Observed:** `PlayerInputMessage`, `InputQueue`, `SimulationStep`, and `TickLoop`
form an input-driven 20 Hz server loop. Clients send a five-byte input body; the
server computes position and velocity.

**Dependency:** compact inputs only matter if the server can turn them into state.

### 3. Make the hot path compact

**Observed:** `BitWriter`, `BitReader`, `CompactVec3`, `BinaryEnvelope`, and
`PayloadSerializers` provide custom framing and payload layouts. JSON stays in the
system for compatibility.

**Dependency:** the simulation identifies the high-frequency values that must fit
the bandwidth target.

### 4. Bound who sees each update

**Observed:** `SpatialGrid` and `InterestManager` introduce near, mid, and far
visibility tiers. `TickBroadcaster` filters changed entities per observer.

**Dependency:** reducing bytes per entity is insufficient if every client still
receives every entity.

### 5. Separate logical lanes from physical paths

**Observed:** binary sessions can use UDP for entity updates and fall back to binary
WebSocket. JSON WebSocket remains supported. Session establishment and UDP binding
are coordinated through an eight-byte token.

**Dependency:** transport fallback needs stable message semantics and framing.

### 6. Add reconciliation evidence and operational proof

**Observed:** updates include tick, last processed input sequence, and a state hash.
Contract and simulation tests cover the components. Node scripts exercise vertical,
multiplayer, resume, and dual-channel paths. The load report records local UDP
success and Azure WebSocket fallback.

### 7. Put Godot above the contracts

**Observed:** Nature 2.0 references `Game.Contracts`, sends binary input over
WebSocket, reads binary or JSON entity updates, maps coordinates, and emits rendering
signals. It does not currently establish the UDP binding used by the load-test client.

### 8. Feed new domain systems through labs

**Observed:** `TerrainSim` and `TreeFellingSim` isolate domain math; Godot labs expose
it interactively; parameter sweeps tune terrain; `CompactTreeState` projects rich
felling state into 24 bytes.

**Unfinished boundary:** compact tree state has not yet crossed the full contracts →
gateway → client pipeline. This is a demonstrated projection, not completed network
integration.

## Why the original documentation feels incomplete

The refactor commit combined research notes, an implementation plan, production
code, unit tests, smoke scripts, deployment configuration, and retrospectives. Later
ADRs describe architectural slices after the integrated system already existed.
There is plenty of evidence, but it was never assembled into a subsystem-by-subsystem
construction record. The [evidence index](evidence-index.md) is that missing join.
