# Nature 2.0 Phase 2: From Primitives to Presence

## Context

Phase 1 (2026-03-29) proved the full pipeline: connect → server-authoritative world → procedural terrain with forest. Trees sit on terrain, player walks with camera orbit, WASD movement round-trips through the server. Azure and local both work.

Current state: geometric primitives (capsules, spheres, cylinders, boxes). Functional but no sense of place. Phase 2 turns primitives into an environment worth studying.

## Philosophy

Per the project vision: movement is intentional and slow. Staying in one place increases depth of understanding. The AoI tiers (near 20Hz / mid 5Hz / far dropped) aren't just bandwidth optimization — they ARE the game feel. Non-critical detail loads progressively, and that loading window is part of the gameplay.

This means: environmental detail and inspection mechanics are gameplay features, not polish.

## Slices

### Slice 7: Character Model
**Goal**: Replace capsule with a low-poly humanoid. Axe visible in hand.

- Source a CC0/MIT low-poly character model (options: Kenney, Quaternius, Mixamo)
- Import as .glb/.gltf into Godot
- Replace CapsuleMesh in Player.tscn with imported model
- Basic idle pose (no animation yet — static mesh is fine for first pass)
- Axe stays as procedural mesh (handle + blade) positioned relative to hand bone if rigged, or as child node if static
- Camera pivot stays at character height

**Verify**: Connect, see humanoid instead of capsule, camera still orbits correctly.

**Follow-up** (not this slice): walk animation, axe swing animation, idle breathing.

### Slice 8: Environmental Ambiance
**Goal**: Sky, fog, ambient sound. The world feels inhabited.

- **Sky**: ProceduralSkyMaterial or HDRI skybox — sun position matches directional light
- **Fog**: Distance fog to fade far terrain/trees into atmosphere (reinforces AoI — far things are literally hazier)
- **Ambient sound**: Wind loop (persistent), bird calls (randomized, proximity-based)
- **Time of day** (stretch): slowly rotating sun, color temperature shift. Server could eventually broadcast time-of-day tick.

**Verify**: Connect, hear wind, see sky gradient, distant trees fade into fog.

### Slice 9: Tree Inspection ("Study" Mechanic)
**Goal**: Walk up to a tree, press E, see its story. This is the first gameplay mechanic beyond movement.

- **Proximity detection**: Client checks distance to nearest tree (from GameState entity positions)
- **UI panel**: When within interaction range (~3 units), show "[E] Study"
- **On interact**: Display panel with tree data from server:
  - Age (from growth_history.age_years): "~150 years old"
  - Wind exposure (from growth_history.twist): "Shaped by strong westerly winds"
  - Fire history (from growth_history.fire_scars): "Survived a fire — bark is darkened"
  - Health: "Healthy" / "Damaged" / "Felled"
  - Lean: Visual indicator of which way it's leaning from previous chops
- **Server request** (optional): Request full growth_history for studied tree (not sent in snapshot to save bandwidth)
- This mechanic validates the "staying increases depth" vision — you learn more by being near things

**Verify**: Walk to tree, press E, see age/wind/fire data. Walk away, panel closes.

### Slice 10: Weather System (Visual Layer)
**Goal**: Wind direction visible in tree sway and particle effects. Rain as a visual/audio event.

- **Wind visualization**: Trees sway slightly in trade wind direction (server sends trade_wind_x/z in region_profile)
  - Canopy meshes get a subtle oscillating rotation based on wind vector
  - Intensity varies by altitude (higher = windier)
- **Rain particles**: Godot GPUParticles3D, triggered by a weather state
  - For now: toggle with a debug key (F2)
  - Later: server broadcasts weather state per region
- **Rain sound**: Layered over wind ambient

**Verify**: Trees sway in wind direction, F2 toggles rain particles + sound.

### Slice 11: Procedural Generation Refinement
**Goal**: Iterate on terrain and forest generation while the user can walk through and inspect.

- **Terrain resolution**: Increase grid from 50x50 to 100x100 or 200x200 for smoother hills
- **Biome variation**: Use humidity grid to vary ground color (dry = yellow-brown, wet = dark green)
- **Tree density tuning**: Adjust spawn probability curves in NaturalResourceLoader
- **Tree species**: Multiple types (oak, pine, birch) with different mesh proportions and canopy shapes
- **Meadows**: Humidity pockets without trees — open grassland areas
- **Edge treatment**: Coastline where altitude drops below sea level (water plane)

This is iterative and benefits from the user walking through with the inspect mechanic — each tree tells you about the generation parameters.

## Implementation Priority

```
Slice 7 (character model)     → quick visual uplift, 1 session
Slice 8 (ambiance)            → sky + fog + sound, 1 session
Slice 9 (tree inspection)     → first real mechanic, 1 session
Slice 10 (weather)            → visual depth, 1-2 sessions
Slice 11 (procgen refinement) → ongoing iteration
```

Slices 7-9 can be done in any order. I recommend 7 first (biggest visual impact for effort), then 9 (gameplay), then 8 (atmosphere).

## Architecture Notes

- All entity data comes from server — client never generates gameplay state
- Tree inspection reads from GameState's cached entity metadata (already parsed from snapshot)
- Growth_history detail could be lazy-loaded: request full history only when player studies a specific tree
- Weather state would be a new field in world_snapshot or a periodic broadcast
- Character model is purely client-side — server doesn't care what mesh renders, only position/heading matters
- Fog distance can map to AoI bands: near = clear, mid = slight haze, far = fog wall

## Dependencies

- **Character model**: Need to source a CC0/MIT .glb file. Options:
  - [Kenney Character Assets](https://kenney.nl/assets) (CC0, low-poly, multiple styles)
  - [Quaternius](https://quaternius.com/) (CC0, stylized medieval/fantasy)
  - [Mixamo](https://www.mixamo.com/) (free with Adobe account, rigged + animated)
- **Ambient sound**: Need CC0 wind/bird .ogg files
  - [Freesound.org](https://freesound.org/) or [Kenney](https://kenney.nl/assets/category:Audio)
- **HDRI sky**: Godot's ProceduralSkyMaterial is sufficient; no external asset needed
