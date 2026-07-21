# World Generation Lab

## Purpose
Interactive sandbox for exploring procedural terrain generation with erosion,
rivers, biomes, and weather. No server dependency — pure local simulation.
Results inform the server-side RegionProfile generation pipeline.

## Architecture Alignment
- Server generates world data (ADR 0001 — server-authoritative)
- Client renders from received data (thin client)
- This lab prototypes the *algorithm* that will run on the server
- Once tuned, the algorithm moves to `NaturalResourceLoader`/`RegionProfileLoader`

## Implementation

### Core: Heightmap + Erosion (512x512 grid)

**Noise generation:**
- Multi-octave simplex noise for base continental shape
- Large scale: continents/oceans. Small scale: local hills.
- Sea level threshold → coastlines emerge naturally

**Hydraulic erosion (Sebastian Lague approach):**
- Drop virtual raindrop at random position
- Droplet flows downhill, picks up sediment based on speed × slope
- Deposits sediment when slowing (flat terrain, pooling)
- Evaporates gradually
- Parameters: erosion rate, deposition rate, evaporation, inertia, capacity
- Run N iterations (tunable via slider, 0 to 500K)

**Thermal erosion:**
- If slope between adjacent cells exceeds talus angle, material slumps downhill
- Softens ridges, creates scree slopes
- Parameter: talus angle (tunable)

### Rivers: Flow Accumulation

1. Fill depressions (remove local minima)
2. Calculate flow direction per cell (steepest descent to neighbor)
3. Accumulate upstream cell count
4. Threshold → river cells
5. Render as blue lines/mesh on terrain

### Biomes: Climate from Terrain

- **Moisture**: rainfall simulation from wind direction + orographic effect
  - Wind carries moisture from ocean
  - Moisture drops when hitting elevation (rain shadow)
- **Temperature**: base from altitude (higher = colder)
- **Biome selection**: temp × moisture matrix
  - Hot+wet = tropical forest
  - Hot+dry = desert
  - Cold+wet = boreal forest
  - Cold+dry = tundra
  - Very cold = snow/ice

### Visualization Modes (toggle in tuning panel)

1. **Height** — grayscale altitude
2. **Shaded** — terrain shader (grass/rock/snow by slope+altitude)
3. **Moisture** — blue gradient showing rainfall distribution
4. **Biome** — colored by biome type
5. **Flow** — river network overlay
6. **Erosion delta** — red/blue showing where material was removed/deposited

### Tuning Panel Sections

**Generation:**
- Noise octaves (1-8)
- Noise frequency (continent scale)
- Sea level (0-1)
- Volcanic hotspots (0-5, click to place)

**Erosion:**
- Iterations (0 to 500K, with "run 10K more" button)
- Erosion rate
- Deposition rate
- Evaporation rate
- Droplet inertia
- Sediment capacity

**Climate:**
- Wind direction (angle slider)
- Wind strength
- Base temperature (latitude proxy)
- Moisture evaporation from ocean

**Display:**
- Visualization mode (dropdown or toggle)
- Vertical exaggeration (how tall mountains appear)
- Water level opacity
- River threshold

**Time:**
- Season slider (0-1, spring→summer→fall→winter)
- Day/night slider

### Controls
- Same movement as atmosphere lab (WASD camera-relative, orbit, zoom)
- Can view terrain from above (top-down map view) or walk on it
- "Regenerate" button → new random seed
- "Erode More" button → add N erosion iterations to current terrain

### Biome Presets (Data-Driven)

Five presets discovered from a 500-run parameter sweep (`scripts/Lab/ParameterSweep.cs`). Each preset applies optimized erosion/deposition/evaporation/inertia/capacity/lifetime values and runs erosion for the corresponding iteration count.

| Preset | Erosion Rate | Deposition | Evaporation | Inertia | Capacity | Lifetime | Iterations |
|--------|-------------|-----------|-------------|---------|----------|----------|-----------|
| **Alpine** | 0.04 | 0.79 | 0.0014 | 0.04 | 2.4 | 37 | 150K |
| **Rainforest** | 0.18 | 0.10 | 0.040 | 0.32 | 4.7 | 41 | 500K |
| **Desert** | 0.04 | 0.69 | 0.033 | 0.05 | 1.1 | 50 | 150K |
| **Rolling Hills** | 0.27 | 0.68 | 0.011 | 0.31 | 7.8 | 75 | 300K |
| **Wetlands** | 0.22 | 0.23 | 0.040 | 0.14 | 2.7 | 57 | 500K |

See [parameter sweep plan](plan-worldgen-parameter-sweep.md) for methodology.

## How to Test

1. Open `clients/godot-cs/nature-2.0/` in Godot 4.6.1 Mono
2. Load `scenes/WorldGenLab.tscn`, run the scene (F6)
3. **Navigate:** WASD to move, RMB drag to orbit camera, scroll to zoom
4. **Generate terrain:** Press R to regenerate with a new random seed
5. **Run erosion:** Press E to run 10K erosion iterations (repeat for more detail)
6. **Try biome presets:** Open the tuning panel (Tab), go to "Biome Presets" section, click Alpine / Rainforest / Desert / Rolling Hills / Wetlands. Each regenerates terrain and runs erosion automatically.
7. **Switch visualization:** In the Display section, change Mode (0-4):
   - 0 = Shaded terrain (grass/rock/snow by slope + altitude)
   - 1 = Height (grayscale altitude)
   - 2 = Moisture (blue gradient showing rainfall distribution)
   - 3 = Biome (colored by biome type)
   - 4 = Erosion delta (red/blue showing where material was removed/deposited)
8. **Tune parameters:** Adjust erosion rate, deposition, wind angle/strength, sea level, etc. in the tuning panel. Most changes rebuild the mesh in real-time.
9. **Verify:** Rivers should follow valleys, moisture accumulates on windward slopes, biomes match climate expectations (hot+wet = forest, hot+dry = desert, cold = tundra/snow).

## Build Order

1. ~~**Heightmap + 3D mesh** — noise terrain on 128x128 grid, tunable octaves/frequency~~ ✅ Done (512x512)
2. ~~**Hydraulic erosion** — droplet simulation with tunable params~~ ✅ Done (up to 500K iterations)
3. ~~**Visualization modes** — height/shaded/moisture toggle~~ ✅ Done (5 modes)
4. ~~**River generation** — flow accumulation + threshold + blue overlay~~ ✅ Done
5. ~~**Biome coloring** — moisture × temperature → biome colors~~ ✅ Done
6. **Season slider** — shifts vegetation colors, adds snow at altitude (not yet implemented)

## Performance Budget
- 512x512 grid = 262K cells
- Mesh generation: builds in real-time for slider changes
- Erosion: 150K-500K iterations depending on preset (~seconds, runs synchronously with progress logging)
- River accumulation: steepest-descent flow direction + accumulation
- All runs on client CPU, no GPU compute needed at this scale
