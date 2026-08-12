# Repository Boundary: lumberjacks-platform

## Owns

- .NET 9 Gateway, services, contracts, simulation, and Companion under `Lumberjacks/`.
- `Comfy.Transport.Contracts`, P7 infrastructure, and production compose/env templates.
- The live FieldLab harness, scenarios, routes, executable experiment inputs,
  decision queue, FieldLab ADRs/automation skills, and roadmap journal.
- Authority-lab, P7, Wave 0, guest-package, and telemetry Workbench tooling.

## Does not own

- Client telemetry mod, HUD, Owner Score, or generic mod deploy lanes: `networksense`.
- Quest Lab, Runtime, Contracts, Studio, or quest packages: `comfy-quest`.
- Dev/Lab MCP implementation: `isolate`.
- Historical evidence and experiment run outputs, integration/status snapshots,
  retrospectives, and the cross-repository architecture index: `baseline`.

## Artifact contracts

| Direction | Artifact | Verification |
| --- | --- | --- |
| publishes | `Comfy.Transport.Contracts` | exact NuGet version; strict tag/commit/payload validation and NuGet.org availability proof |
| publishes | Gateway/service images | immutable image digest and release identity |
| consumes | `Comfy.Quest.Contracts`, `Comfy.Quest.Studio` | exact `[0.1.0]` public profile; explicit exact `[0.1.0-local]` interim profile until publication |
| consumes | `ComfyNetworkSense.dll` | manifest v1 + SHA256SUMS, bytes, managed identity, source/tag, and baked release ID |
| consumes | Quest Lab/Picker assets | comfy-quest manifest v1, exact four-asset verification, release tag, and pinned manifest SHA-256 |

Sibling source trees are never a fallback. A missing package or artifact is a
hard boundary failure, not permission to build another repository’s source.
