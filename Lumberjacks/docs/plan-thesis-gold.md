# Plan: Thesis Gold (0.9+)

**Current Score:** 0.85 ("Era-Authentic" high end)
**Target Score:** 0.9+ ("Thesis Gold")
**Two gaps:** Delta compression and client-side prediction

This plan covers both. They are independent — either can be built first. Delta compression is server-only (no Godot changes). Client prediction requires Godot work and benefits from delta compression but doesn't require it.

---

## Gap 1: Delta Compression

### Problem

Every `entity_update` sends all 7 fields (entity_id, position, velocity, heading, last_input_seq, tick, state_hash) even if only heading changed. A stationary player surrounded by 10 other stationary players receives 10 × 33 bytes × 20Hz = 6.6 KB/s of redundant data.

### Goal

Only send fields that changed since the client's last acknowledged state. A stationary player near other stationary players should receive near-zero bytes until something moves.

### Design

**Concept: Per-client acknowledged state + field-level change flags**

Each client tracks what it last confirmed receiving. The server compares current state against that baseline and sends only the diff.

#### Server-Side Changes

**Step 1: Extend GameSession with ack tracking**

File: `Game.Gateway/WebSocket/SessionManager.cs`

```csharp
public record GameSession(...)
{
    // ... existing fields ...

    /// Last tick the client acknowledged (via ack message or implicit)
    public long LastAckedTick { get; set; } = 0;

    /// Per-entity: snapshot of state at LastAckedTick
    /// Key = entity_id, Value = last-sent compact state
    public ConcurrentDictionary<string, EntitySnapshot> LastSentState { get; }
        = new();
}

public readonly record struct EntitySnapshot(
    CompactVec3 Position,
    CompactVec3 Velocity,
    ushort Heading,
    ushort LastInputSeq);
```

**Step 2: Add change flags to binary payload**

New serializer: `PayloadSerializers.WriteEntityUpdateDelta()`

```
Change flags byte (8 bits):
  bit 0: position changed
  bit 1: velocity changed
  bit 2: heading changed
  bit 3: last_input_seq changed
  bits 4-7: reserved

Wire format:
  [entity_id: VarInt string]
  [change_flags: 1 byte]
  [position: 6 bytes]       — only if bit 0 set
  [velocity: 6 bytes]       — only if bit 1 set
  [heading: 2 bytes]        — only if bit 2 set
  [last_input_seq: 2 bytes] — only if bit 3 set
  [tick: 4 bytes]           — always
  [state_hash: 4 bytes]     — always
```

**Best case (nothing changed):** entity_id (2-9) + flags (1) + tick (4) + hash (4) = **11-18 bytes** (vs 33 full)
**Typical case (only position):** + 6 bytes = **17-24 bytes** (27% savings)
**Worst case (everything changed):** + 1 byte flags overhead = **34 bytes** (negligible cost)

**Step 3: Update TickBroadcaster to use delta encoding**

In `TickBroadcaster.BroadcastTickAsync()`:

```csharp
foreach (var entityId in visible)
{
    var current = new EntitySnapshot(
        CompactVec3.FromVec3(player.Position),
        CompactVec3.FromVec3(player.Velocity),
        PackHeading(player.Heading),
        (ushort)player.LastInputSeq);

    byte changeFlags = 0xFF; // full update by default
    if (session.LastSentState.TryGetValue(entityId, out var prev))
    {
        changeFlags = ComputeChangeFlags(prev, current);
        if (changeFlags == 0)
            continue; // nothing changed — skip entirely
    }

    // Send delta update
    SendDeltaEntityUpdate(session, entityId, current, changeFlags, tick, stateHash);

    // Update last-sent state
    session.LastSentState[entityId] = current;
}
```

**Step 4: Handle new entities and full syncs**

- First time a client sees an entity: `changeFlags = 0xFF` (full state)
- After reconnection / world_snapshot: clear `LastSentState` for that session
- Periodically (every 100 ticks / 5 seconds): force full update to recover from any drift

**Step 5: Optional client ack (stretch goal)**

Add a lightweight `ack` message from client → server:
```json
{"type": "ack", "payload": {"tick": 12345}}
```

Without explicit acks, the server can use an implicit model: assume the client received everything sent over WebSocket (TCP guarantees delivery). UDP sends can track a high-water mark. This is simpler and sufficient for the vertical slice.

#### Bandwidth Impact

| Scenario | Current | With Delta | Savings |
|----------|---------|------------|---------|
| 1 player, idle | 0.78 KB/s | ~0.02 KB/s (hash-only keepalive) | 97% |
| 5 near, 2 moving | 3.9 KB/s | ~1.2 KB/s | 69% |
| 10 near, all moving | 7.8 KB/s | ~5.4 KB/s | 31% |
| 10 near, all idle | 7.8 KB/s | ~0 KB/s (skipped) | ~100% |

The biggest wins are in static/low-activity scenarios — which is most of a survival game's runtime (building, exploring, standing around).

#### Files to Modify

| File | Change |
|------|--------|
| `Game.Gateway/WebSocket/SessionManager.cs` | Add `LastSentState`, `EntitySnapshot` |
| `Game.Contracts/Protocol/Binary/PayloadSerializers.cs` | Add `WriteEntityUpdateDelta()`, `ReadEntityUpdateDelta()` |
| `Game.Gateway/WebSocket/TickBroadcaster.cs` | Compare against last-sent, compute change flags, skip unchanged |
| `Game.Contracts/Protocol/Binary/MessageTypeId.cs` | Add `EntityUpdateDelta` type (or reuse EntityUpdate with flag) |
| `tests/Game.Contracts.Tests/PayloadSerializerTests.cs` | Delta serializer round-trip tests |
| `tests/Game.Simulation.Tests/` | Integration test for delta broadcast behavior |

#### Test Plan

1. Unit: `WriteEntityUpdateDelta` round-trip with all combinations of change flags
2. Unit: `ComputeChangeFlags` correctly detects which fields changed
3. Integration: Two players — one moves, one doesn't. Verify idle player generates zero entity_update bytes after initial sync.
4. E2E: Run `test-multiplayer.js` — all existing tests must still pass (backwards compatibility)
5. Benchmark: Measure bytes/sec per client with 10 players, 50% idle

---

## Gap 2: Client-Side Prediction

### Problem

The Godot client currently waits for the server to confirm every movement. At 20Hz tick rate, the minimum input-to-visual delay is 50ms (one tick) + network RTT. On Azure, that's ~50ms + ~100ms = ~150ms of input lag. Acceptable for building, noticeable for walking, unacceptable for future combat.

### Goal

The local player's character responds instantly to input. The server remains authoritative — if the server disagrees with the predicted position, the client snaps to the server's answer. This is standard client-side prediction with server reconciliation.

### Design

**Concept: Predict locally, reconcile on server ack, replay unconfirmed inputs**

The client maintains two positions for the local player:
1. **Predicted position** — where the client thinks the player is (rendered)
2. **Authoritative position** — where the server says the player is (received in entity_update)

When they diverge, the client replays unconfirmed inputs on top of the authoritative position to re-predict.

#### Godot Client Changes

**Step 1: Input buffer — track unconfirmed inputs**

New file: `scripts/input_buffer.gd` (or extend `player_controller.gd`)

```gdscript
## Circular buffer of recent inputs, keyed by input_seq
var _buffer: Array[Dictionary] = []  # [{seq, direction, speed, timestamp}, ...]
const MAX_BUFFER_SIZE = 128  # ~6 seconds at 20Hz

func record_input(seq: int, direction: int, speed: int) -> void:
    _buffer.append({"seq": seq, "direction": direction, "speed": speed})
    if _buffer.size() > MAX_BUFFER_SIZE:
        _buffer.pop_front()

func get_unconfirmed_since(acked_seq: int) -> Array[Dictionary]:
    # Return all inputs with seq > acked_seq
    var result: Array[Dictionary] = []
    for input in _buffer:
        if input["seq"] > acked_seq:
            result.append(input)
    return result

func clear_confirmed(acked_seq: int) -> void:
    # Remove all inputs with seq <= acked_seq
    while _buffer.size() > 0 and _buffer[0]["seq"] <= acked_seq:
        _buffer.pop_front()
```

**Step 2: Local prediction — apply input immediately**

In `player_controller.gd`, after sending input to server:

```gdscript
# Apply input locally for instant feedback
var predicted_velocity = _direction_to_velocity(direction_byte, speed_percent)
var predicted_pos = position + predicted_velocity * (1.0 / 20.0)
predicted_pos = _clamp_to_bounds(predicted_pos)  # match server bounds
position = predicted_pos  # move immediately
```

The prediction must mirror the server's `SimulationStep` physics:
- Same direction → heading conversion
- Same speed scaling (`MaxSpeedPerTick = 10.0` at 20Hz = 0.5 units/frame at 60fps)
- Same friction (`FrictionPerTick = 2.0`)
- Same bounds clamping

**Step 3: Reconciliation — correct on server disagreement**

In `player_entity.gd`, when receiving `entity_update` for the local player:

```gdscript
func update_from_server(entity: Dictionary) -> void:
    if not is_local_player:
        # Remote players: interpolate as before
        super.update_from_server(entity)
        return

    # Local player: reconcile prediction
    var server_pos = GameState.get_entity_position(entity)
    var server_seq = entity.get("_data", {}).get("last_input_seq", 0)

    # Clear confirmed inputs
    _input_buffer.clear_confirmed(server_seq)

    # Check if prediction matches server
    var error = position.distance_to(server_pos)
    if error < 0.5:
        # Close enough — keep predicted position (feels smooth)
        return

    # Diverged — snap to server position and replay unconfirmed inputs
    position = server_pos
    var unconfirmed = _input_buffer.get_unconfirmed_since(server_seq)
    for input in unconfirmed:
        var vel = _direction_to_velocity(input["direction"], input["speed"])
        position += vel * (1.0 / 20.0)
    position = _clamp_to_bounds(position)
```

**Step 4: Dead reckoning for remote players**

For other players, extrapolate position using last known velocity between server updates:

```gdscript
# In remote_entity.gd, for non-local players
var last_velocity: Vector3 = Vector3.ZERO

func update_from_server(entity: Dictionary) -> void:
    target_position = GameState.get_entity_position(entity)
    var vel_dict = entity.get("velocity", entity.get("_data", {}).get("velocity", null))
    if vel_dict is Dictionary:
        last_velocity = Vector3(
            float(vel_dict.get("x", 0)),
            float(vel_dict.get("y", 0)),
            float(vel_dict.get("z", 0)))

func _process(delta: float) -> void:
    if not should_interpolate:
        return

    # Extrapolate toward target + velocity prediction
    var predicted_target = target_position + last_velocity * delta
    position = position.lerp(predicted_target, delta * interpolation_speed)
    rotation.y = lerp_angle(rotation.y, target_heading_rad, delta * interpolation_speed)
```

This smooths out the 50ms gaps between server ticks. Remote players appear to move continuously rather than in 20Hz steps.

#### Physics Parity: Server ↔ Client

The prediction only works if the client's physics match the server's. Extract the constants:

| Constant | Server (SimulationStep.cs) | Client (GDScript) |
|----------|---------------------------|-------------------|
| MaxSpeedPerTick | 10.0 | 10.0 |
| FrictionPerTick | 2.0 | 2.0 |
| TickRate | 20 Hz (50ms) | 20 Hz (50ms) |
| Direction mapping | 0-255 → 0°-360° → sin/cos | Same formula |
| Bounds clamping | Per-region min/max | Hardcoded or from world_snapshot |

**Critical:** If these drift, the client will constantly rubber-band. Consider sending physics constants in `world_snapshot` or `session_started` so they're always in sync.

#### Reconciliation Tolerance

```
error < 0.5 units → keep prediction (smooth)
error >= 0.5 units → snap to server + replay (correct)
error >= 5.0 units → teleport snap, no replay (major desync)
```

The 0.5-unit threshold accounts for floating-point differences between C# (server) and GDScript (client). Tune this based on testing — too tight causes constant rubber-banding, too loose allows visible desync.

#### Files to Create/Modify

| File | Change |
|------|--------|
| `clients/godot/scripts/input_buffer.gd` | **New** — circular buffer of unconfirmed inputs |
| `clients/godot/scripts/player_controller.gd` | Record inputs to buffer, apply locally |
| `clients/godot/scripts/player_entity.gd` | Reconciliation logic for local player |
| `clients/godot/scripts/remote_entity.gd` | Dead reckoning (velocity extrapolation) |
| `clients/godot/scripts/physics_constants.gd` | **New** — shared constants matching server |

#### Test Plan

1. **Local feel:** Connect to local server. Hold W — character should move immediately with no perceptible delay.
2. **Reconciliation:** Add artificial latency (200ms). Walk forward, then quickly reverse. Character should briefly overshoot then snap back without jarring teleport.
3. **Bounds clamping:** Walk into region boundary. Client prediction should stop at the same point the server does — no rubber-banding at edges.
4. **Remote players:** Connect two clients. Walk one around while watching from the other. Movement should appear smooth, not choppy at 20Hz.
5. **Resume:** Disconnect and reconnect. Prediction buffer should clear, client should accept server position cleanly.
6. **Azure latency:** Test against Azure deployment. Movement feel should be comparable to local (prediction absorbs the RTT).

---

## Implementation Order

### Recommended: Delta Compression First

1. **Delta compression is server-only** — no Godot changes, no client risk. Ship it, measure bandwidth, confirm it works with existing clients.
2. **Client prediction depends on tight physics parity** — getting the reconciliation threshold right requires iteration. Delta compression is more deterministic.
3. **Delta compression improves the score independently** — even without prediction, reducing idle bandwidth from 7.8 KB/s to near-zero for stationary groups is a significant thesis improvement.

### Phase A: Delta Compression (Server-Side)

| Step | Description | Effort |
|------|-------------|--------|
| A1 | Add `EntitySnapshot` and `LastSentState` to `GameSession` | Small |
| A2 | Implement `ComputeChangeFlags()` comparing current vs last-sent | Small |
| A3 | Implement `WriteEntityUpdateDelta()` with field-level flags | Medium |
| A4 | Modify `TickBroadcaster` to use delta path, skip unchanged | Medium |
| A5 | Add periodic full-sync (every 5s) as safety net | Small |
| A6 | Unit tests for delta serializer and change flag computation | Medium |
| A7 | Integration test: verify idle players generate zero bytes | Small |
| A8 | Run all existing E2E tests to confirm backwards compatibility | Small |

### Phase B: Client-Side Prediction (Godot)

| Step | Description | Effort |
|------|-------------|--------|
| B1 | Create `input_buffer.gd` with circular buffer | Small |
| B2 | Create `physics_constants.gd` matching server values | Small |
| B3 | Add local prediction to `player_controller.gd` | Medium |
| B4 | Add reconciliation to `player_entity.gd` | Medium |
| B5 | Add dead reckoning to `remote_entity.gd` | Small |
| B6 | Tune reconciliation threshold (0.5 unit default) | Iteration |
| B7 | Test against local and Azure backends | Testing |

### Phase C: Refinements (Stretch)

| Step | Description | Effort |
|------|-------------|--------|
| C1 | Adaptive AoI — shrink near radius under high player density | Medium |
| C2 | View-frustum culling — skip entities behind the camera | Medium |
| C3 | Send physics constants in `world_snapshot` payload | Small |
| C4 | Client `ack` message for explicit delta baseline (UDP only) | Medium |

---

## Scoring Impact

| Improvement | Current → Target | Justification |
|-------------|-----------------|---------------|
| Delta compression (Phase A) | 0.85 → 0.90 | Idle bandwidth drops to near-zero. Dense scenes get 30-70% reduction. Meets 3.6 KB/s target for small groups unconditionally. |
| Client prediction (Phase B) | 0.90 → 0.93 | Game is playable on core channel alone with instant response. Prediction absorbs RTT. Dead reckoning smooths inter-tick gaps. |
| Adaptive AoI + frustum (Phase C) | 0.93 → 0.95 | Dense scenarios stay under bandwidth target. Theoretically optimal filtering. |

A score of 0.95 would be deep in "Thesis Gold" territory. The remaining 0.05 would require true lockstep determinism with full state replay — overkill for a survival game.
