# Repository Layout

The repository is organized around a .NET network core, clients above that core, and
research labs that feed new network projections.

```text
/
├── src/
│   ├── Game.Contracts/       Shared entities, protocol meaning, binary framing
│   ├── Game.Simulation/      Input queue, fixed tick, world state, spatial interest
│   ├── Game.Gateway/         WebSocket/UDP sessions, routing, broadcast, Valheim paths
│   ├── Game.EventLog/        Append-only event ingestion
│   ├── Game.Progression/     Challenge and progression evaluation
│   ├── Game.OperatorApi/     Administrative and service-proxy API
│   ├── Game.Persistence/     PostgreSQL data access
│   └── Game.ServiceDefaults/ Shared service configuration
│
├── clients/
│   ├── godot-cs/nature-2.0/ Active C# thin client and interactive labs
│   ├── godot/                Earlier client plus the replay-viewer consumer
│   ├── admin-web/            React operator console
│   └── game-client/          Legacy TypeScript client scaffold
│
├── tests/
│   ├── Game.Contracts.Tests/ Protocol and binary-format tests
│   ├── Game.Simulation.Tests/ Tick, physics, hash, grid, and interest tests
│   └── Game.Gateway.Tests/   Gateway/Valheim integration tests
│
├── scripts/                  Smoke, multiplayer, resume, and load clients
├── infra/                    Docker Compose and database bootstrap
├── docs/
│   ├── network/              Network reference and reconstructed build history
│   ├── labs/                 Research-to-network method and projection status
│   ├── adrs/                 Architecture decision records
│   ├── retro/                Retrospectives
│   └── templates/            Planning and ADR templates
│
├── tools/
│   ├── ideas/                Forestry and physics source material
│   └── parameter_sweep.csv   Recorded terrain sweep output
│
├── services/                 Legacy TypeScript service scaffolds
├── packages/                 Legacy TypeScript package scaffolds
├── plugins/                  Reserved plugin surface
└── oldimages/                Unreferenced historical image assets
```

## Dependency direction

```text
Godot Nature 2.0 ───────┐
Gateway ────────────────┼──> Game.Contracts
Simulation ─────────────┘

Gateway -> Game.Simulation -> Game.Persistence
EventLog / Progression / OperatorApi -> Contracts + Persistence
```

The active Godot project directly references `Game.Contracts`, which multi-targets
.NET 9 for the server and .NET 8 for Godot. Authoritative simulation remains in
`Game.Simulation`; Godot owns presentation and input capture.

## Active versus historical areas

`src/`, `tests/`, `scripts/`, `clients/godot-cs/nature-2.0/`, and the operator UI are
active implementation areas. The older Godot project cannot be classified as wholly
dead because the replay viewer uses it. The TypeScript `services/`, `packages/`, and
`clients/game-client/` directories are pre-.NET scaffolds and should not be mistaken
for the authoritative backend.

The curated entry point is [docs/README.md](README.md).
