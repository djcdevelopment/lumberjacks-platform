# Repository Boundary: lumberjacks-platform

## Owns

- .NET 9 Gateway, services, contracts, simulation, and Companion under `Lumberjacks/`.
- `Comfy.Transport.Contracts`, P7 infrastructure, and production compose/env templates.
- The live FieldLab harness, scenarios, routes, authority inputs, and roadmap journal.
- Authority-lab, P7, Wave 0, guest-package, and telemetry Workbench tooling.

## Does not own

- Client telemetry mod, HUD, Owner Score, or generic mod deploy lanes: `networksense`.
- Quest Lab, Runtime, Contracts, Studio, or quest packages: `comfy-quest`.
- Dev/Lab MCP implementation: `isolate`.
- Historical evidence/runs/integration/status and architecture index: `baseline`.

## Artifact contracts

| Direction | Artifact | Verification |
| --- | --- | --- |
| publishes | `Comfy.Transport.Contracts` | exact NuGet version; local `0.1.0-local` rehearsal until Phase 3 |
| publishes | Gateway/service images | immutable image digest and release identity |
| consumes | `Comfy.Quest.Contracts`, `Comfy.Quest.Studio` | exact package version; local feed only during extraction |
| consumes | `ComfyNetworkSense.dll` | release tag, manifest schema, bytes, SHA-256, and baked release ID |
| consumes | Quest Lab/Picker assets | release tag plus manifest SHA-256 |

Sibling source trees are never a fallback. A missing package or artifact is a
hard boundary failure, not permission to build another repository’s source.
