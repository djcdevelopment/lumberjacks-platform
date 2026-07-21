# Research and Labs

Labs are the bridge between source research and network-ready mechanics. They allow a
rich domain model to be inspected and tuned before it is forced into a server tick or
wire budget.

## Method

```text
research sources
    -> explicit assumptions and units
    -> pure C# simulation
    -> Godot visualization and controls
    -> parameter sweeps / scenario presets
    -> compact client-visible projection
    -> shared contracts and serializers
    -> gateway transport
    -> live Godot integration
```

Read [Research to lab method](research-to-lab-method.md) for the promotion gates and
[Network projections](network-projections.md) for the current integration status.

## Current labs

| Lab | Research / model | Pure simulation | Interactive surface | Network status |
|---|---|---|---|---|
| Atmosphere | Visual tuning | Primarily Godot scene logic | Fog, light, terrain, trees | Visual-only; no compact projection claimed |
| World generation | Hydraulic erosion and biome parameters | `TerrainSim` | `WorldGenLab`, five presets | Region profile exists; full server promotion remains incomplete |
| Tree felling | Forestry manuals and swing/cutting physics | `TreeFellingSim` | `TreeFellingLab`, scenario presets | 24-byte projection demonstrated; shared wire integration incomplete |

## Existing detailed records

- [World-generation plan](../plan-worldgen-lab.md)
- [World-generation parameter sweep](../plan-worldgen-parameter-sweep.md)
- [Terrain rendering plan](../plan-terrain-rendering.md)
- [Tree-felling plan](../plan-tree-felling-lab.md)
- [Tree-felling ADR](../adrs/0019-tree-felling-physics-lab-validated.md)
- [Tree-felling physics article](../article-tree-felling-physics.md)
- [Swing physics article](../article-cross-swing-physics.md)
- [Tree-felling tech debt](../tech-debt-tree-felling-2026-03-31.md)

Research source files are retained under `tools/ideas/`. A source file supports a
model; it does not by itself validate the implementation.
