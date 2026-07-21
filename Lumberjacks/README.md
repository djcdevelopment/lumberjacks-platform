# Lumberjacks

Lumberjacks is a server-authoritative multiplayer infrastructure project with a thin
Godot client and a lab-driven method for turning research into network-ready game
systems.

The network core is the primary asset: deterministic input processing, compact binary
protocols, spatial interest management, and progressive UDP/WebSocket delivery built
for 100-player communities and constrained links. Godot sits above that core as the
rendering, interaction, and experimentation layer.

[Start with the network infrastructure](docs/network/README.md) ·
[Read the reconstructed build history](docs/network/build-reconstruction.md) ·
[Explore research and labs](docs/labs/README.md) ·
[Inspect the evidence](docs/network/evidence-index.md)

## The architecture in one view

```text
research and constraints
        |
        +---------------------- network core -----------------------+
        |                                                           |
        v                                                           v
delivery semantics -> compact contracts -> deterministic simulation -> interest tiers
                                                                   |
                          UDP when available <- gateway -> WebSocket fallback
                                                                   |
                                                                   v
                                                        Godot thin client
                                                                   |
                                    research -> pure model -> interactive lab
                                                                   |
                                                  compact network projection
```

Clients submit intent, never authoritative positions. The server processes input at
20 Hz, computes world state, filters updates per observer, and publishes compact state.
Binary sessions use UDP when a client has bound it, binary WebSocket otherwise, and a
JSON WebSocket compatibility path remains available.

## Why it was built this way

The design works backward from several constraints:

- server ownership of simulation and persistent world truth;
- 100+ player community scale;
- a playable core on a 28.8k-class bandwidth budget;
- reliable delivery for mutations and disposable delivery for transient state;
- graceful operation when UDP is blocked;
- game systems that can be researched and validated before they enter the live loop.

The result is not one networking trick. It is a stack:

| Layer | Implementation |
|---|---|
| Authority | Input queue and fixed-rate server simulation |
| Representation | Six-byte binary envelope, five-byte player input, compact vectors |
| Visibility | Spatial grid with near/mid/far update tiers |
| Delivery | UDP datagrams with binary WebSocket and JSON fallbacks |
| Verification | Contract tests, simulation tests, smoke scripts, and load tests |
| Client | C# Godot client sharing `Game.Contracts` |

See [Network architecture](docs/network/architecture.md) for the boundaries and
[Protocol and compression](docs/network/protocol-and-compression.md) for byte-level
details.

## Reconstructed build story

The original history is coarse: most of the network refactor landed in a single
75-file commit on March 26, 2026, and several ADRs followed on March 27–28. The
repository contains the code, tests, plans, audits, and results, but not a trustworthy
fine-grained commit narrative.

The documentation therefore reconstructs the logical sequence from dependencies:

1. Define server authority and reliable/datagram delivery semantics.
2. Replace client-submitted positions with tick-aligned input.
3. Compress the high-frequency input and entity-update path.
4. Filter state by spatial relevance and update cadence.
5. Add UDP delivery with WebSocket fallback.
6. Validate the system with unit, smoke, multiplayer, and load tests.
7. Build Nature 2.0 over the shared contracts.
8. Use research and labs to design new compact domain projections.

The full account, including what is recorded versus inferred, is in the
[build reconstruction](docs/network/build-reconstruction.md).

## Godot above the network core

The active client is `clients/godot-cs/nature-2.0/`, a Godot 4.6 C# project. It
references the shared contracts, sends binary player input, accepts binary or JSON
entity updates, maps server coordinates into Godot space, and renders authoritative
state.

Today, Nature 2.0 uses binary WebSocket rather than binding the optional UDP channel.
That is the designed fallback path. Full local prediction/reconciliation and binary
natural-resource delivery remain incomplete.

See [Godot integration](docs/network/godot-integration.md) for the precise ownership
boundary and current integration status.

## Research, labs, and network projection

New mechanics move through explicit stages:

```text
source research -> pure C# simulation -> interactive Godot lab -> validation
                -> compact projection -> shared serializer -> gateway -> live client
```

### Tree felling

The tree-felling work uses forestry manuals, material properties, and swing/cutting
research. `TreeFellingSim` models a rich polar trunk cross-section without depending
on Godot. `TreeFellingLab` exposes the model through cut presets, stress views, force
data, and failure scenarios.

The lab demonstrates a six-float, 24-byte `CompactTreeState`. That projection is not
yet a shared binary serializer or live gateway payload; the documentation keeps those
stages distinct.

### World generation

`TerrainSim` isolates hydraulic erosion and biome math. `ParameterSweep` explored 500
configurations, and `WorldGenLab` exposes the resulting presets and visualization
modes. The client renders server-supplied region profiles, while full promotion of the
lab generator into authoritative server generation remains incomplete.

See [Research and labs](docs/labs/README.md) and
[Lab network projections](docs/labs/network-projections.md).

## Validation snapshot

The recorded March 27 dual-channel load run used 50 bots for 30 seconds:

| Environment | Result |
|---|---|
| Local | 152,118 UDP entity updates, zero recorded errors |
| Azure Container Apps | UDP ingress blocked; binary WebSocket fallback, zero recorded errors |

These are dated results, not a continuously reproduced benchmark. The
[validation guide](docs/network/validation.md) defines how future measurements should
separate payload, frame, and transport sizes.

## Current infrastructure extensions

The July work extends the gateway toward Valheim integration through priority
manifests, datagram activation, ZDO redirect and injection paths, and handshake
admission. These features build on the same contracts, lane classification, gateway,
and test structure. Their current evidence is indexed in
[Network evidence](docs/network/evidence-index.md).

## Quick start

### Prerequisites

- .NET 9 SDK
- Node.js 18+
- Docker and Docker Compose
- Godot 4.6.1 Mono for Nature 2.0 and the labs

### Backend and admin UI

```bash
npm install
npm run dev:infra
npm run build:dotnet
npm run test:dotnet
npm run dev
```

PostgreSQL uses port 5433 locally. The Gateway uses WebSocket/TCP port 4000 and UDP
port 4005.

### Smoke and load tests

```bash
node scripts/test-vertical-slice.js
node scripts/test-multiplayer.js
npm run test:load:50
```

### Nature 2.0

Open `clients/godot-cs/nature-2.0/` in Godot 4.6.1 Mono. The primary world, atmosphere
lab, world-generation lab, and tree-felling lab are separate scenes so mechanics can
be examined without requiring the full live stack.

## Repository map

```text
src/
  Game.Contracts/       Shared protocol, entities, and binary serialization
  Game.Simulation/      Input queue, tick loop, physics, spatial interest
  Game.Gateway/         Sessions, WebSocket/UDP transport, broadcast, Valheim paths
  Game.EventLog/        Append-only event ingestion
  Game.Progression/     Player and guild progression
  Game.OperatorApi/     Administrative API
  Game.Persistence/     PostgreSQL access

clients/
  godot-cs/nature-2.0/  Active C# client and research labs
  godot/                Earlier client, now also used by the replay viewer
  admin-web/            Operator console

tests/                  Contract, simulation, and gateway tests
scripts/                Smoke, multiplayer, resume, and load clients
docs/network/           Network architecture and reconstructed history
docs/labs/              Research-to-network method and projection status
docs/adrs/              Architecture decision records
tools/ideas/            Forestry and physics research sources
```

The curated documentation index is at [docs/README.md](docs/README.md).

## License

Licensed under the [Lumberjacks Community Source License v1.0](LICENSE.md): free for
non-commercial community servers up to 100 active members. Commercial use requires a
separate agreement. Copyright © 2026 DJC Development.
