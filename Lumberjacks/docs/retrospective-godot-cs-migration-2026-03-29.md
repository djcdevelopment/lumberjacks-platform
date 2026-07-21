# Retrospective: Godot C# Client Migration (2026-03-29)

## What Happened

Attempted to migrate the Godot client from GDScript to C# to align with ADR 0006 (C# as primary game logic language) and enable direct use of `Game.Contracts` for binary protocol, entity types, and coordinate mapping.

### Phase 1: In-place Migration (Failed)

The original Godot project at `clients/godot/` was created by a previous agent using the **non-mono Godot editor**. C# scripts were written alongside GDScript but never compiled because:

1. **No .NET SDK integration** — the project was created with `Godot_v4.6.1-stable_win64.exe` (no C# support), not `Godot_v4.6.1-stable_mono_win64.exe`
2. **Godot requires it to create the C# solution** — hand-crafting `.csproj`/`.sln` files doesn't work; Godot must create them via `Project → Tools → C# → Create C# solution` to register them internally
3. **"Create C# solution" vs "Build Solution"** — the menu shows "Create" when Godot hasn't adopted the project, "Build" when it has. We kept seeing "Create" because the project was never properly initialized for C#
4. **Multiple attempts to work around this** wasted time — creating `.sln` manually, changing `.tscn` script references, updating `project.godot` autoloads. None worked because the fundamental issue was the editor binary.

### Phase 2: Clean Slate (Succeeded)

Created a fresh project at `clients/godot-cs/nature-2.0/` using the mono editor:

1. `Project → Create` in Godot mono editor
2. `Project → Tools → C# → Create C# solution` — generated correct `.csproj` with `Godot.NET.Sdk/4.6.1`
3. Added `Game.Contracts` as `ProjectReference` — required multi-targeting (`net8.0;net9.0`) since Godot uses `net8.0` and the server uses `net9.0`
4. Added `Directory.Build.props` to block root props inheritance
5. Built incrementally: Main.cs → ConnectScreen → SimulationClient → GameState → World

### Result

Full end-to-end pipeline proven:
- Connect screen → WebSocket → session_started → join_region → world_snapshot → entity spawning
- Server's procedurally generated forest appears as 3D entities in Godot
- Binary protocol (`Game.Contracts.Protocol.Binary`) works at runtime
- Thread-safe networking (ConcurrentQueue + `_Process` drain pattern)
- Coordinate mapping (ADR 0018) applied correctly

## What Went Well

- **Clean slate approach** was right — fighting the old project was a dead end
- **Game.Contracts multi-targeting** works cleanly — one codebase, both frameworks
- **Incremental slicing** (connect screen → networking → state → world) caught issues early
- **Thread-safe message queue pattern** is clean and Godot-native
- **Server changes minimal** — only added `region_profile` to world_snapshot in `MessageRouter.cs`

## What Went Wrong

- **~2 hours wasted** trying to make the old GDScript project accept C# scripts
- **Root cause not identified early** — should have verified the Godot editor binary first
- **Previous agent created a hybrid GDScript/C# mess** — two parallel implementations, neither complete
- **`stackalloc` in async methods** — C# 13 feature not available in Godot's C# 12 context; had to use heap arrays instead

## Lessons Learned

1. **Always verify the toolchain first** — before writing any code, confirm the editor/SDK supports the target language
2. **Godot C# projects must be created by Godot** — the editor registers the `.csproj` internally; external creation doesn't work
3. **The `.csproj` name must match `project/assembly_name`** in `project.godot` (spaces and all)
4. **`Directory.Build.props` inheritance** — Godot projects nested in a .NET monorepo need an empty `Directory.Build.props` to block the root one
5. **`net8.0` vs `net9.0`** — Godot 4.6.1 targets `net8.0`; shared libraries must multi-target if referenced from both Godot and `net9.0` server projects
6. **Don't fight the framework** — when something isn't working after 2 attempts, step back and question assumptions

## Architecture Impact

- **ADR 0006 validated**: C# works as primary Godot scripting language with direct `Game.Contracts` reference
- **ADR 0016 (JSON debt) partially resolved**: SimulationClient handles both binary and JSON; full binary migration can happen incrementally
- **ADR 0018 (coordinate mapping)** confirmed working in C# via `CoordinateMapper.ServerToGodot()`
- **New tech debt**: Old `clients/godot/` project with broken GDScript/C# hybrid should be archived or removed

## Current State

### Working (clients/godot-cs/nature-2.0/)
- Connect screen with URL input
- WebSocket connection with binary protocol negotiation
- Session management (session_started)
- Region joining (join_region → world_snapshot)
- Entity spawning from snapshot (placeholder box meshes)
- Coordinate mapping (server → Godot space)
- Scene switching (connect screen → world → back)
- Error handling and disconnect/reconnect overlay

### Not Yet Ported (from original plan)
- Player capsule mesh with camera and WASD movement (PlayerController)
- Remote entity interpolation (ADR 0017)
- Tree entity scenes with growth_history visual variation
- Terrain heightmap generation from RegionProfile
- Structure placement (build mode)
- HUD overlay (player count, tick, position, ping)
- Tree chopping interaction (axe swing, health decrement, directional fall)

## Next Steps

1. **Slice 5**: Player mesh + camera + WASD movement (server-authoritative)
2. **Slice 6**: Tree entity scenes with visual variation + terrain heightmap
3. Archive or remove `clients/godot/` once `nature-2.0` reaches feature parity
4. Consider ADR 0019 documenting the Godot C# project setup requirements
