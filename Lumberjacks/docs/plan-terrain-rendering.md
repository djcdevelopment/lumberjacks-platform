# Terrain Rendering Plan

## Problem
Heightmap mesh uses flat-shaded vertex colors with duplicated vertices per triangle.
Result: visible facets, hard edges, "desert canyon" look. No sense of continuous ground.

## Solution: Two-Layer System

### Layer 1: Smooth Geometry (immediate)
- **Indexed mesh** with shared vertices — SurfaceTool generates smooth normals across triangles
- Same heightmap data from server (50x50 grid), same altitude scaling
- Smooth normals alone eliminate 90% of the faceted look
- Zero server cost — purely client-side mesh generation change

### Layer 2: Terrain Shader (immediate)
- `ShaderMaterial` on terrain mesh, blends visuals based on geometry:
  - **Slope**: flat = grass green, steep = rock/dirt brown
  - **Altitude**: low = dark forest floor, mid = meadow, high = exposed rock
  - **Noise**: procedural noise breaks up repetition, adds natural variation
- Purely client-side — shader reads vertex position/normal, no textures needed initially
- Later: actual texture maps (grass, dirt, rock) for detail
- Tunable in lab via parameters

### Layer 3: Ground Detail (future)
- Grass blades via MultiMesh or GPUParticles3D near camera
- Fallen leaves, small rocks, mushrooms as scatter objects
- Only in near AoI band — fades with distance
- Maps to ADR 0015 interest tiers: near=detail, mid=textured surface, far=fog

### Layer 4: Terrain Modifications (future, needs server)
- Sparse delta list: `(x, z, delta_y, type)` per modification
- Sent in world_snapshot, updated incrementally
- Tree fell scars, worn paths, erosion near water
- Client applies deltas to base heightmap before mesh generation
- Bandwidth: bytes per mod, not re-sending grid

## Implementation Order
1. Smooth normals in lab terrain (indexed mesh with SurfaceTool)
2. Slope/altitude shader with noise
3. Add tuning controls: noise scale, slope threshold, color blending
4. Port to main World TerrainGenerator.cs
5. Higher grid resolution option (100x100)

## ADR Alignment
- Server-authoritative heightmap (ADR 0001) — client only renders
- Lightweight transmission (ADR 0012) — 50x50 grid = ~20KB, delta-compressible
- AoI-compatible (ADR 0015) — detail layers scale with distance tiers
- Thesis Gold (28.8k constraint) — terrain data is one-time snapshot, not per-tick
