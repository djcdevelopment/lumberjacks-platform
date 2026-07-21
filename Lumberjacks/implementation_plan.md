# Network Infrastructure Refactoring Plan

## The Problem

The [theRefactor.txt](file:///d:/work/game/docs/theRefactor.txt) analysis is correct. Despite a working vertical slice (sessions, regions, structures, guilds, inventory — all functional), **the networking layer is fundamentally unsuitable as community-buildable infrastructure**. Here's the gap analysis mapped to actual code:

### Gap Analysis: What theRefactor.txt Found vs. What the Code Shows

| Problem | Where It Lives | Severity | Status |
|---------|---------------|----------|--------|
| JSON serialization everywhere | [Envelope.cs](file:///d:/work/game/src/Game.Contracts/Protocol/Envelope.cs) — `System.Text.Json` for all wire traffic | 🔴 Critical | ✅ Fixed (Phase 1) — BinaryEnvelope, BitWriter/BitReader, CompactVec3 |
| Vec3 = 3× `double` (192 bits/position) | [Vec3.cs](file:///d:/work/game/src/Game.Contracts/Entities/Vec3.cs) — `record struct Vec3(double X, double Y, double Z)` | 🔴 Critical | ✅ Fixed (Phase 1) — CompactVec3 = 48 bits |
| MessageType = string constants on the wire | [MessageType.cs](file:///d:/work/game/src/Game.Contracts/Protocol/MessageType.cs) — `"player_move"`, `"entity_update"`, etc. | 🟡 Medium | ✅ Fixed (Phase 1) — byte IDs added |
| Absolute positions in move messages | [Messages.cs](file:///d:/work/game/src/Game.Contracts/Protocol/Messages.cs) — PlayerMoveMessage | 🔴 Critical | ✅ Fixed (Phase 2) — InputMessage (direction+speed, 4 bytes) |
| No simulation on tick — tick loop is empty | [TickLoop.cs](file:///d:/work/game/src/Game.Simulation/Tick/TickLoop.cs) | 🔴 Critical | ✅ Fixed (Phase 2) — SimulationStep with physics |
| Gateway→Sim is HTTP per message | [MessageRouter.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/MessageRouter.cs) | 🔴 Critical | ✅ Fixed (Phase 2) — direct in-process handler calls |
| O(N) region broadcast, no spatial filter | TickBroadcaster / MessageRouter | 🟡 Medium | ✅ Fixed (Phase 3) — SpatialGrid + InterestManager AoI |
| Both lanes go through single WebSocket | [ADR 0008](file:///d:/work/game/docs/adrs/0008-delivery-lane-classification.md) L53 — "both lanes flow over WebSocket" | 🟡 Medium (deferred by design) | ⏳ Deferred (Phase 5) |
| No client prediction or reconciliation | No code exists for this yet | 🟡 Deferred | ⏳ Deferred (Phase 4 — needs Godot client) |
| No state hashing | No code exists for this yet | 🟡 Deferred | ✅ Fixed (Phase 2) — StateHasher (CRC32) |

### What's Actually Good (Keep This)

- ✅ Session management with resume tokens ([SessionManager.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/SessionManager.cs))
- ✅ DeliveryLane classification system ([MessageClassification.cs](file:///d:/work/game/src/Game.Contracts/Protocol/MessageClassification.cs))
- ✅ Region-scoped session tracking
- ✅ Server-authoritative movement validation (physics in SimulationStep, bounds clamping, speed clamping)
- ✅ Event system architecture
- ✅ Tick loop at 20Hz with full simulation pipeline (InputQueue → SimulationStep → StateHasher → TickBroadcaster)
- ✅ In-process handler calls (MessageRouter → PlayerHandler/PlaceStructureHandler/InventoryHandler)
- ✅ Per-player AoI filtering (SpatialGrid + InterestManager, near/mid/far bands)
- ✅ Binary wire protocol (BinaryEnvelope, CompactVec3, BitWriter/BitReader)
- ✅ 141 automated tests (90 Contracts + 51 Simulation)
- ✅ ADR discipline and planning docs

---

## Proposed Refactoring Phases

> [!IMPORTANT]
> This is a **major architectural refactoring** that changes the wire protocol, simulation model, and gateway-to-simulation communication. Each phase should be a separate PR/sprint, tested independently before moving to the next.

### Phase 1: Binary Serialization — "The Binary Diet" ✅ COMPLETE

Replace JSON wire protocol with bit-packed binary. This is the foundation everything else builds on.
**Completed 2026-03-26. 48 tests in Game.Contracts.Tests.**

#### [NEW] [BitWriter.cs](file:///d:/work/game/src/Game.Contracts/Protocol/Binary/BitWriter.cs)
- `BitWriter` — stackalloc-friendly bit-packing writer
- Write methods: `WriteBits(value, bitCount)`, `WriteFixedVec3(Vec3)`, `WriteVarInt(int)`, `WriteBool(bool)`

#### [NEW] [BitReader.cs](file:///d:/work/game/src/Game.Contracts/Protocol/Binary/BitReader.cs)
- `BitReader` — read counterpart to BitWriter
- Matching read methods for every write method

#### [NEW] [CompactVec3.cs](file:///d:/work/game/src/Game.Contracts/Entities/CompactVec3.cs)
- 16-bit fixed-point X/Z (±32767 units, 1-unit precision — covers 65km² world)
- 16-bit Y (±3276.7, 0.1-unit precision — 6.5km vertical range)
- **48 bits total** vs current 192 bits (4× compression)
- Conversion methods: `CompactVec3.FromVec3(Vec3)`, `.ToVec3()`

#### [NEW] [BinaryEnvelope.cs](file:///d:/work/game/src/Game.Contracts/Protocol/Binary/BinaryEnvelope.cs)
- Replace JSON [Envelope](file:///d:/work/game/src/Game.Contracts/Protocol/Envelope.cs#5-13) with binary header: `[version:4bits][type:6bits][seq:16bits][payloadLen:16bits][payload...]`
- 6 bytes header vs ~80+ bytes JSON envelope overhead
- [MessageType](file:///d:/work/game/src/Game.Contracts/Protocol/MessageType.cs#3-20) becomes a `byte` enum (0-63) instead of string constants
- Keep [EnvelopeFactory](file:///d:/work/game/src/Game.Contracts/Protocol/Envelope.cs#14-43) for backwards-compat during transition (dual-mode: can Parse both JSON and binary)

#### [MODIFY] [MessageType.cs](file:///d:/work/game/src/Game.Contracts/Protocol/MessageType.cs)
- Add numeric `byte` ID for each message type alongside existing string constants
- String constants remain for logging/debugging

#### [MODIFY] [DeliveryLane.cs](file:///d:/work/game/src/Game.Contracts/Protocol/DeliveryLane.cs)
- Pack as 1-bit field in binary envelope header

#### [MODIFY] [GameWebSocketMiddleware.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/GameWebSocketMiddleware.cs)
- Accept `WebSocketMessageType.Binary` in addition to `.Text`
- Route binary frames through `BinaryEnvelope.Parse()`, text frames through existing JSON path
- Send binary frames when client indicates binary protocol support (via query param or handshake)

---

### Phase 2: Input-Driven Simulation — "The Deterministic Pivot" ✅ COMPLETE

Replace absolute-position messaging with input-only packets. Wire the tick loop to actually simulate.
**Completed 2026-03-26. PlayerHandler extracted, MessageRouter converted to direct calls, SimulationStep with physics, InputQueue, StateHasher, TickBroadcaster. 51 tests in Game.Simulation.Tests.**

#### [NEW] [InputMessage.cs](file:///d:/work/game/src/Game.Contracts/Protocol/InputMessage.cs)
- `InputMessage(byte Direction, byte SpeedPercent, byte ActionFlags, uint TargetTick)`
- Direction: 0-255 mapped to 0°-360°
- SpeedPercent: 0-100
- ActionFlags: bitfield for jump/crouch/interact
- **4 bytes** vs current ~120 bytes JSON move message

#### [NEW] [InputQueue.cs](file:///d:/work/game/src/Game.Simulation/Tick/InputQueue.cs)
- Per-player input buffer keyed by target tick
- Inputs arriving for past ticks get assigned to `CurrentTick + 1`
- Configurable buffer depth (default: 3 ticks / 150ms at 20Hz)

#### [MODIFY] [TickLoop.cs](file:///d:/work/game/src/Game.Simulation/Tick/TickLoop.cs)
- Each tick: drain input queue → apply movement physics → broadcast authoritative state
- Movement physics: direction + speed → velocity → position delta (server computes position, not client)
- Add `StateHash` computation: CRC32 of all player positions + region entity state

#### [MODIFY] [PlayerEndpoints.cs](file:///d:/work/game/src/Game.Simulation/Endpoints/PlayerEndpoints.cs)
- `/players/move` → `/players/input` — accepts raw input, queues for next tick
- Remove absolute position acceptance; server computes all positions

#### [MODIFY] [MessageRouter.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/MessageRouter.cs)
- [HandlePlayerMoveAsync](file:///d:/work/game/src/Game.Gateway/WebSocket/MessageRouter.cs#212-295) → `HandlePlayerInputAsync` — forward input to simulation's input queue
- **Critical change**: Replace HTTP POST per move with direct in-process call or gRPC stream
- The Gateway→Sim HTTP hop for high-frequency movement is a scalability killer

#### [NEW] [TickBroadcaster.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/TickBroadcaster.cs)
- Subscribes to simulation tick output
- Builds per-region entity update frames
- Sends binary `entity_update` frames containing only delta state (changed entities)
- Includes `state_hash` in every authoritative frame for client-side desync detection

---

### Phase 3: Spatial Interest Management — "Spatial Surgery" ✅ COMPLETE

Replace O(N) region broadcast with distance-based filtering.
**Completed 2026-03-26. SpatialGrid, InterestManager (near/mid/far AoI bands), TickBroadcaster rewritten for per-player filtering. 20 new tests. E2E validated with test-input-broadcast.js (15/15 green).**

#### [NEW] [SpatialGrid.cs](file:///d:/work/game/src/Game.Simulation/World/SpatialGrid.cs)
- Grid-based spatial hash: region subdivided into cells (e.g., 50×50 unit cells)
- `Insert(entityId, position)`, [Remove(entityId)](file:///d:/work/game/src/Game.Gateway/WebSocket/SessionManager.cs#84-88), [Update(entityId, newPos)](file:///d:/work/game/src/Game.Contracts/Protocol/Messages.cs#15-16)
- `QueryRadius(position, radius)` → yields nearby entity IDs

#### [NEW] [InterestManager.cs](file:///d:/work/game/src/Game.Simulation/World/InterestManager.cs)
- Per-player Area of Interest (AoI): near band (full rate), medium band (throttled), far band (dropped)
- Near: 0-100 units → every tick update
- Medium: 100-300 units → every 4th tick
- Far: 300+ units → only Reliable-lane events (structure placed, etc.)

#### [MODIFY] [TickBroadcaster.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/TickBroadcaster.cs) (from Phase 2)
- Build per-player update frames using `InterestManager` instead of per-region broadcast
- Reliable-lane messages (world mutations) still broadcast to full region
- Datagram-lane messages (positions) filtered by AoI

#### [MODIFY] [SessionManager.cs](file:///d:/work/game/src/Game.Gateway/WebSocket/SessionManager.cs)
- Add position tracking per session (for interest queries from gateway side)

---

### Phase 4: Client Prediction — "The Fidelity Buffer" ✅ SERVER-SIDE COMPLETE

> [!NOTE]
> Client-side prediction/reconciliation is Godot work. The server-side support is now complete.

**Completed 2026-03-26. Binary payload serializers, outbound binary framing, inbound binary fast-path. 12 new tests.**

#### Server-side support (DONE):
- `state_hash` in every entity_update frame (Phase 2) ✅
- `last_input_seq` echoed in every entity_update frame ✅
- Binary payload serializers: EntityUpdate (~33 bytes vs ~200+ JSON), PlayerInput (5 bytes vs ~120 JSON), EntityRemoved ✅
- TickBroadcaster sends binary frames to binary-mode sessions, JSON to JSON-mode ✅
- Middleware fast-path: binary `player_input` deserialized directly (no JSON round-trip) ✅
- Full binary frame: 6-byte envelope header + binary payload — ~11 bytes for input, ~39 bytes for entity update ✅

#### Client-side (Godot — needs game client):
- Local input prediction: apply inputs immediately, don't wait for server
- Reconciliation: on receiving authoritative state, replay unconfirmed inputs from buffer
- Dead reckoning: extrapolate other players' positions between server updates

---

### Phase 5: Dual-Channel Transport ✅ COMPLETE

**Completed 2026-03-26. UdpTransport BackgroundService, session UDP binding, TickBroadcaster dual-channel send. 5 new tests.**

#### What was built:
- `UdpTransport.cs` — UDP listener BackgroundService on configurable port (default 4005)
- UDP packet format: `[udpToken: 8 bytes][binaryEnvelope: header + payload]` — 19 bytes for player_input
- Session UDP binding: client sends first UDP packet with token, server maps endpoint to session
- `session_started` includes `udp_token` and `udp_port` so clients know how to bind
- TickBroadcaster: tries UDP first for binary sessions with bound endpoints, falls back to WebSocket
- Inbound UDP player_input: deserialized directly via PayloadSerializers fast-path (no JSON)
- WebSocket remains Channel 1 (Reliable lane) — all non-datagram messages stay on WebSocket
- UDP is Channel 2 (Datagram lane) — entity_update sent via UDP when available

---

## Recommended Execution Order

> [!CAUTION]
> **Do NOT attempt all 5 phases at once.** Each phase must be stable before starting the next. The refactor doc is aspirational — real execution needs incremental delivery.

| Order | Phase | Effort | Blocks | Status |
|-------|-------|--------|--------|--------|
| 1st | **Phase 1: Binary Serialization** | 2-3 days | Nothing — can deploy alongside JSON | ✅ Complete |
| 2nd | **Phase 2: Input-Driven Simulation** | 3-5 days | Phase 1 (binary needed for efficiency) | ✅ Complete |
| 3rd | **Phase 3: Spatial Interest Management** | 2-3 days | Phase 2 (need tick-based broadcasting) | ✅ Complete |
| 4th | Phase 4: Client Prediction | Client-side work | Phases 1-3 | ✅ Server-side complete |
| 5th | Phase 5: Dual-Channel Transport | 2-3 days | Phases 1-3 | ✅ Complete |

**All 5 phases completed 2026-03-26.** The network infrastructure refactoring plan is done. Next: Azure deployment, Godot client prototype.

## Verification Plan

### Phase 1 Verification
- **Unit tests**: Add to existing `Game.Contracts.Tests` project:
  - `BitWriter`/`BitReader` roundtrip tests for all data types
  - `CompactVec3` precision tests (verify conversion accuracy)
  - `BinaryEnvelope` serialize/parse roundtrip vs JSON envelope equivalence
  - Binary envelope size assertions (verify < 10 bytes header)
- **Run**: `dotnet test tests/Game.Contracts.Tests` (existing test command)
- **Backwards compatibility**: Existing JSON tests must still pass (dual-mode support)

### Phase 2 Verification
- **Unit tests**: New `Game.Simulation.Tests` project:
  - `InputQueue` ordering and tick assignment tests
  - [TickLoop](file:///d:/work/game/src/Game.Simulation/Tick/TickLoop.cs#5-93) simulation step: input→position computation correctness
  - `StateHash` determinism: same inputs → same hash
- **Integration test**: Adapt existing [scripts/test-multiplayer.js](file:///d:/work/game/scripts/test-multiplayer.js) to send input messages instead of position messages
- **Run**: `dotnet test` (all projects)

### Phase 3 Verification
- **Unit tests**: 
  - `SpatialGrid` insert/remove/query tests
  - `InterestManager` band filtering tests
- **Load test**: Script that spawns N players spread across a region, measures broadcast packet count per player (should be << N)

### Manual Verification
- After Phase 1: Connect with existing test scripts, verify both JSON and binary paths work
- After Phase 2: Observer logs to confirm tick-based broadcasting replaces per-move HTTP calls
- After Phase 3: With 50+ simulated players, verify each player receives only updates for nearby entities
