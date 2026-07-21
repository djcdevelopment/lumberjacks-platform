# Godot Client — Vertical Slice Plan (C#-First Edition)

**Goal:** Build a Godot 4.4 (.NET) client that references `Game.Contracts` for zero-overhead, binary-ready networking. This isn't just a rendering shell—it's a thin-client architecture that shares the backend's "Simulation Logic" constants to ensure perfect synchronization.

> [!IMPORTANT]
> **Active Technical Debt:** This plan prioritizes velocity over bandwidth by using JSON-first fallbacks, but maintains a "Binary-Ready" C# path via `Game.Contracts`. 
> See [ADR 0016 (Protocol)](file:///d:/work/game/docs/adrs/0016-godot-client-json-protocol-debt.md), 
> [ADR 0017 (Interpolation)](file:///d:/work/game/docs/adrs/0017-godot-client-interpolation-debt.md), and
> [0018 (Coordinate Mapping)](file:///d:/work/game/docs/adrs/0018-godot-client-coordinate-mapping.md).

**Exit Criteria:**
- Player connects via WebSocket (binary-ready mode), receives `session_started`.
- World renders from `world_snapshot` (terrain, structures, other players).
- WASD input sends `player_input` packets using the `PlayerInputBinary` struct logic.
- Other players interpolated using a delta-aware time buffer (handling 5Hz/20Hz zones).
- Click-to-place a structure, see it appear after server confirms.
- Build system uses the same `MaxSpeed` and `Friction` constants as `src/Game.Simulation`.

---

## Architecture: The Adaptor Pattern

Because we are using **Godot 4.4 .NET**, we can use C# for the "Brain" and GDScript (optional) for the "Heart/Visuals."

```
  Godot Engine (Visuals) <—— [ Signals ] ——> [ SimulationClient.cs ] <—— [ Binary ] ——> [ Gateway ]
                                                 (Uses Game.Contracts)
```

**Key Principle:** The client is an "Echo" of the backend. It uses the `Game.Contracts` assembly to ensure that data types and constants (Physics, Protocol IDs) are 100% identical to the server.

---

## Phase 1: Foundation (Zero-Debt Setup)

### 1.1 Create Godot .NET Project
- Initialize Godot 4.4 .NET project at `clients/godot/`.
- **Project Reference:** Add `<ProjectReference Include="..\..\src\Game.Contracts\Game.Contracts.csproj" />` to the Godot `.csproj`.

### 1.2 SimulationClient (C# Autoload)
`scripts/Networking/SimulationClient.cs` — The core "Brain."

**Responsibilities:**
- Connect to `ws://localhost:4000?protocol=binary` using `ClientWebSocket`.
- Handle the `BinaryEnvelope` stream from the Gateway.
- Dispatch signals: `SessionStarted`, `EntityUpdate`, `WorldSnapshot`.
- **Binary Path:** Use `PayloadSerializers.ReadEntityUpdate(payload)` directly.

### 1.3 Coordinate Mapper (C# Utility)
`scripts/Core/CoordinateMapper.cs`
- Maps Server (+Z = North) to Godot (-Z = Forward).
- Centralizes world $\to$ screen transformations.

---

## Phase 2: World Rendering (C# Logic)

### 2.1 GameState (C# Autoload)
`scripts/Core/GameState.cs` — Listens to `SimulationClient` and maintains the state.
- Stores entities using the `EntityUpdateBinary` structs.
- Emits signals when entities enter/leave the VoI (View of Interest).

### 2.2 Delta-Aware Interpolation
`scripts/Entities/RemoteEntity.cs`
- Attached to player/structure scenes.
- Lerps towards `target_position` using `delta * interpolation_speed`.
- **Variable Frequency:** Tracks the time between server updates to handle the "Mid-range" 5Hz updates from ADR 0015 without stutter.

---

## Phase 3: Player Movement (C# Input)

### 3.1 Input Capture
`scripts/Player/PlayerController.cs` (C#)
- Captures WASD input.
- Maps Godot's Forward/Left vectors to the Server's 0-255 compass byte (ADR 0018).
- Sends `player_input` at 20Hz using `PayloadSerializers.WritePlayerInput`.

---

## Phase 4: Build System

### 4.1 Raycast & Placement
- Use Godot's 3D raycasting to find the ground plane point.
- Send the `place_structure` message using the shared `MessageTypeId.PlaceStructure`.

---

## Phase 5: Reconnection & HUD

### 5.1 Reconnection Path
- Persist the `resume_token` from `SessionStarted`.
- Implement exponential backoff for WebSocket reconnection.

---

## Technical Constants (Mirrored from src/Game.Simulation)

| Constant | Value | Purpose |
| :--- | :--- | :--- |
| **Max Speed** | 10.0 | Units per tick (Reference `SimulationStep.MaxSpeedPerTick`) |
| **Friction** | 2.0 | Deceleration per tick (Reference `SimulationStep.FrictionPerTick`) |
| **Tick Rate** | 20Hz | Matches server simulation frequency |

---

## Tech Notes: Why C#?

1. **Serialization Efficiency:** We stop writing bit-packers and byte-readers in GDScript. We use the backend's proven `PayloadSerializers`.
2. **Type Safety:** `EntityUpdateBinary` structs ensure we never mis-index a JSON key (`pos_x` vs `position.x`).
3. **Future-Proofing:** UDP transport (Phase 7) is trivial with .NET's `UdpClient`, while GDScript's UDP is more limited.
beacon` | Debug marker | Yellow sphere |

### Region Bounds
Default `region-spawn`: `(-500, -10, -500)` to `(500, 200, 500)` — 1km² playable area.

---

## Future Slices (not this plan)

These are parked. Don't build them until this slice is proven.

- **Inventory & items:** Pickup world items, inventory UI, store in containers
- **Binary protocol:** Switch from JSON to compact binary for lower bandwidth
- **UDP transport:** Bind UDP socket for datagram-lane messages (entity_update, player_input)
- **Client-side prediction:** Predict local movement immediately, reconcile with server via input_seq
- **Terrain:** Replace flat plane with heightmap terrain, biomes
- **Art pass:** Replace placeholder meshes with actual 3D models
- **Audio:** Footsteps, placement sounds, ambient
- **Combat:** Health, damage, respawn
- **Guild UI:** Guild membership, challenge progress display
