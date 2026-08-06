# Lumberjacks Documentation

> **Archive notice (added 2026-08-06): this is an era-1 entrance, not the current one.**
> Most of this directory was written in the pre-Valheim greenfield era for a team that
> never formed, and the reading list below leads into that material — greenfield ADRs
> 0001–0020, the Godot client, 90-day plans. Individual files are not all bannered, so
> treat anything here as historical unless it says otherwise.
>
> **For current truth:** the repo root [`README.md`](../../README.md), the era map at
> [`docs/internal/START-HERE.md`](../../docs/internal/START-HERE.md), and the live
> roadmap journal under [`docs/roadmap/`](roadmap/README.md). The netcode program's own
> canon is [`fieldlab/NETCODE-MAP.md`](../../fieldlab/NETCODE-MAP.md) and the ADR track
> at [`fieldlab/docs/adr/`](../../fieldlab/docs/adr/README.md).

## Start here (era-1 reading order — see the notice above)

1. [Network infrastructure](network/README.md) — system overview and reading guide.
2. [Network architecture](network/architecture.md) — component and authority boundaries.
3. [Build reconstruction](network/build-reconstruction.md) — evidence-based development sequence.
4. [Evidence index](network/evidence-index.md) — intent, code, tests, consumers, and status.
5. [Research and labs](labs/README.md) — how domain research becomes network-ready state.

## Network core

- [Protocol and compression](network/protocol-and-compression.md)
- [Deterministic simulation](network/deterministic-simulation.md)
- [Transport and degradation](network/transport-and-degradation.md)
- [Spatial interest management](network/interest-management.md)
- [Validation](network/validation.md)
- [Godot integration](network/godot-integration.md)

## Research and lab method

- [Research-to-lab method](labs/research-to-lab-method.md)
- [Lab network projections](labs/network-projections.md)
- [World-generation lab](plan-worldgen-lab.md)
- [World-generation parameter sweep](plan-worldgen-parameter-sweep.md)
- [Terrain rendering](plan-terrain-rendering.md)
- [Tree-felling lab](plan-tree-felling-lab.md)
- [Tree-felling physics](article-tree-felling-physics.md)
- [Swing physics](article-cross-swing-physics.md)
- [Tree-felling tech debt](tech-debt-tree-felling-2026-03-31.md)

## Architecture decisions

The ADRs record decisions, not a perfect chronological build log.

| Range | Subject |
|---|---|
| [0001–0002](adrs/) | Thin client and platform authority |
| [0003](adrs/0003-websocket-transport.md) | Multi-lane transport strategy |
| [0004–0007](adrs/) | Persistence, .NET, Godot, canonical events |
| [0008](adrs/0008-delivery-lane-classification.md) | Delivery-lane classification |
| [0009–0011](adrs/) | Query layer, topology, graceful degradation |
| [0012](adrs/0012-binary-payload-serialization.md) | Binary payload serialization |
| [0013](adrs/0013-dual-channel-udp-transport.md) | UDP and WebSocket transport |
| [0014](adrs/0014-input-driven-deterministic-simulation.md) | Input-driven simulation |
| [0015](adrs/0015-spatial-interest-management.md) | Spatial interest management |
| [0016–0018](adrs/) | Godot protocol, interpolation, coordinates |
| [0019](adrs/0019-tree-felling-physics-lab-validated.md) | Lab-validated tree physics |

## Validation and results

- [Simulation audit](simulation-audit-2026-03-26.md)
- [Simulation retrospective](simulation-retrospective-2026-03-26.md)
- [Thesis compliance audit](thesis-compliance-audit-2026-03-27.md)
- [Dual-channel load results](load-test-dual-channel-results.md)
- [Tech-debt audit](tech-debt-audit-2026-03-27.md)
- [Naming-drift audit](naming-drift-audit-2026-03-27.md)
- [Test guide](Tests.md)

## Client and replay records

- [Godot client plan](godot-client-plan.md)
- [Nature 2.0 phase plan](plan-nature2-phase2.md)
- [Godot C# migration retrospective](retrospective-godot-cs-migration-2026-03-29.md)
- [Replay overview](replay-overview.md)
- [Replay slices 0–1 retrospective](retro/2026-05-06-godot-replay-slices-0-1.md)

## Product and operations

- [Product brief](product-brief.md)
- [Architecture principles](architecture-principles.md)
- [Repository layout](repo-layout.md)
- [Getting started](getting-started.md)
- [GitHub integration strategy](github-integration-strategy.md)
- [Google Cloud migration strategy](google-cloud-migration-strategy.md)
- [Google Cloud Stage 1 runbook](google-cloud-stage1-runbook.md)
- [Azure deployment runbook](azure-deployment-runbook.md)
- [Deployment strategy](deployment-strategy.md)
- [Current focus](current-focus.md)

## Historical planning

The following documents preserve how the project was framed at different moments.
They are useful evidence but should not override current code or the network evidence
index.

- [12-step success plan](12-step-success-plan.md)
- [90-day roadmap](90-day-roadmap.md)
- [MVP scope](mvp-scope.md)
- [Vertical-slice intent](vertical-slice.md)
- [Planning system](planning-system.md)
- [Idea inbox](idea-inbox.md)
- [Path to Thesis Gold](plan-thesis-gold.md)
- [March 27 retrospective](retrospective-2026-03-27.md)

Templates remain under [templates/](templates/).
