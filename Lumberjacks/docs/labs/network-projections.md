# Lab Network Projections

## Tree felling

The lab's rich model stores a polar cross-section across angular sectors and vertical
slices, along with cut geometry, material properties, forces, and fall state.

`TreeFellingSim.GetCompactState()` reduces the client-visible result to six floats:

- notch angle;
- notch depth;
- back-cut depth;
- hinge-width fraction;
- fall tilt;
- fall bearing.

The in-memory struct is described as 24 bytes. Current status:

| Gate | Status |
|---|---|
| Research | Complete for the cited forestry and swing sources |
| Pure simulation | Implemented |
| Interactive lab | Implemented |
| Scenario validation | Implemented through presets and lab inspection |
| Compact projection | Demonstrated |
| Shared contract serializer | Not implemented |
| Gateway binary transport | Not implemented for this projection |
| Nature 2.0 live consumption | Not implemented |

The existing natural-resource broadcaster sends JSON resource fields and explicitly
leaves its binary path for later.

## World generation

`TerrainSim` separates generation and erosion math from Godot rendering.
`ParameterSweep` ran randomized configurations and produced presets used by
`WorldGenLab`. The live client can render a `RegionProfile` altitude grid, but the
lab algorithm has not been fully promoted into the authoritative server generation
pipeline.

| Gate | Status |
|---|---|
| Pure simulation | Implemented |
| Interactive lab | Implemented |
| Parameter exploration | 500-run sweep recorded |
| Client terrain rendering | Implemented from region profile data |
| Authoritative server generation | Incomplete |
| Compact/delta terrain transport | No separate hot-path format documented |

## Next promotion records

When either system advances, update the [network evidence index](../network/evidence-index.md)
with the exact shared contract, serializer tests, gateway path, client consumer, and
measured frame size. Do not update status from a plan alone.
