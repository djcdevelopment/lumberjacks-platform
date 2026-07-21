# World Generation Parameter Sweep

## Purpose
Systematically explore erosion parameter space to find optimal presets
for different biome types. Outputs a dataset mapping parameters → terrain metrics.

## Approach

### Console tool (no Godot, pure C#)
Runs the same erosion algorithm from WorldGenLab as a batch process.
For each run: generate noise → erode with params → measure terrain → log results.

### Parameters to vary (per run)
| Parameter | Range | Step |
|-----------|-------|------|
| Erosion rate | 0.01 - 0.5 | random uniform |
| Deposition rate | 0.1 - 0.8 | random uniform |
| Evaporation | 0.001 - 0.05 | random uniform |
| Inertia | 0.01 - 0.4 | random uniform |
| Sediment capacity | 1.0 - 8.0 | random uniform |
| Droplet lifetime | 15 - 80 | random uniform |
| Erosion iterations | 10K, 50K, 100K, 200K | fixed set |
| Wind angle | 0 - 360 | random uniform |
| Wind strength | 0.5 - 2.0 | random uniform |

### Terrain quality metrics (computed per run)
| Metric | What it measures |
|--------|-----------------|
| height_retention | mean(final) / mean(original) — how much mountain remains |
| elevation_variance | std(heightmap) — terrain roughness |
| max_ridge_height | max(heightmap) - sea_level — tallest peak survival |
| river_density | count(flow > threshold) / total_land_cells |
| river_max_length | longest continuous flow path |
| coastline_complexity | perimeter of land/water boundary / sqrt(land_area) |
| valley_depth | mean depth of cells below median height |
| slope_variance | std of slope across all cells — terrain interest |

### Output
- CSV file: one row per run, columns = params + metrics
- Can be analyzed in Excel, Python, or fed back into the algorithm
- Goal: identify parameter clusters that produce each biome archetype

### Biome archetypes to find
| Archetype | Expected signature |
|-----------|-------------------|
| Alpine | High height_retention, high slope_variance, low river_density |
| Rainforest valley | Low height_retention, high river_density, high river_length |
| Desert mesa | High height_retention, low river_density, high slope_variance |
| Rolling hills | Medium all metrics, low slope_variance |
| Coastal fjords | Medium height_retention, high coastline_complexity |
| Flat wetlands | Very low slope_variance, high river_density |

### Multi-epoch runs
Some runs will chain 2-3 erosion epochs with different parameters:
1. Epoch 1: aggressive erosion (mountain building aftermath)
2. Epoch 2: gentle long-term weathering
3. Epoch 3: recent climate (wind/rain specific to biome)

This matches the user's discovery that sequencing erosion passes
(aggressive → gentle) produces more interesting terrain than a single pass.

## Implementation
- Reuse erosion algorithm from WorldGenLab (extract to shared class)
- Console app or Godot background task
- 500-1000 runs should be sufficient for pattern detection
- Estimate: ~2-5 seconds per run at 100K iterations on 128x128 grid
- Total sweep: ~15-80 minutes
