# Thesis Compliance Audit — March 27, 2026

Examination of the codebase against the 5 thesis prompts, with scoring per the compliance matrix.

---

## Prompt 1: Simulation Architecture Audit

**Finding: Input-Driven, Fixed-Rate, Server-Authoritative**

The simulation is definitively **input-driven** — not state-synced. Clients never send positions.

**Tick loop** (`Game.Simulation/Tick/TickLoop.cs:28-31`):
```csharp
private const int TickMs = 50; // 20 Hz
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
```
Runs as a `BackgroundService` on its own thread. Fixed 20Hz rate, completely independent of any rendering frame rate. **Not tied to rendering — the "Bankrupt" penalty does not apply.**

**Input queuing** (`Game.Simulation/Tick/InputQueue.cs:14-84`):
- Per-player concurrent queue with tick-based buffering
- Inputs assigned to `currentTick + 1` (processed next tick)
- Buffer depth: 3 ticks
- Last-write-wins by `InputSeq` — if multiple inputs arrive for the same tick, highest sequence number takes priority
- Thread-safe: `ConcurrentDictionary<long, ConcurrentBag<QueuedInput>>`

**Client sends direction + speed, not positions** (`Game.Contracts/Protocol/InputMessage.cs:5-43`):
```csharp
public record PlayerInputMessage
{
    public required byte Direction { get; init; }      // 0-255 → 0°-360°
    public required byte SpeedPercent { get; init; }   // 0-100
    public byte ActionFlags { get; init; }             // bitfield
    public required ushort InputSeq { get; init; }     // echo for reconciliation
}
```
5 bytes on the wire. No absolute positions leave the client.

**Server computes positions** (`Game.Simulation/Tick/SimulationStep.cs:22-130`):
1. Drain inputs from InputQueue for current tick
2. Convert direction byte → heading radians
3. Compute velocity: `vx = sin(heading) * speed`, `vz = cos(heading) * speed`
4. Update position: `newPos = player.Position + velocity`
5. Clamp to region bounds
6. Apply friction to idle players (deceleration per tick)

Physics constants: `MaxSpeedPerTick = 10.0` (200 units/sec at 20Hz), `FrictionPerTick = 2.0`.

**State hashing** (`Game.Simulation/Tick/StateHasher.cs:14-62`): CRC32 hash of all player positions, velocities, and current tick. Included in every `entity_update` for desync detection. **The "Bankrupt" penalty for missing state hashing does not apply.**

**Verdict: Input-driven deterministic simulation with tick-aligned processing, fixed-rate loop, and state hashing. No bandwidth-heavy absolute position sending.**

---

## Prompt 2: Channel Bifurcation Analysis

**Finding: Two distinct delivery lanes with formal classification**

**Message Classification** (`Game.Contracts/Protocol/MessageClassification.cs:7-35`):

| Reliable Lane (TCP/WebSocket) | Datagram Lane (UDP, WS fallback) |
|-------------------------------|----------------------------------|
| `join_region` | `player_input` |
| `leave_region` | `player_move` |
| `place_structure` | `entity_update` |
| `interact` | `entity_removed` |
| `session_started` | |
| `world_snapshot` | |
| `event_emitted` | |
| `error` | |

The distinction maps directly to the thesis:

- **Deterministic Core (Reliable):** State-changing authoritative messages — joining regions, placing structures, session management. These must arrive, in order, and are auditable. Low bandwidth, low frequency.
- **Fidelity Enhancement (Datagram):** Position/velocity updates that are superseded by the next one. Dropping a frame is harmless — the next update overwrites it. High frequency (20Hz), but each message is tiny (33 bytes).

**Transport implementation:**
- **WebSocket** (`Game.Gateway/WebSocket/GameWebSocketMiddleware.cs`): Always available. Carries both lanes (reliable natively, datagram as fallback).
- **UDP** (`Game.Gateway/WebSocket/UdpTransport.cs:20-140`): Optional second channel on port 4005. Session-bound via 8-byte crypto-random token sent in `session_started`. Only carries datagram-lane messages.

**Send priority in TickBroadcaster** (`TickBroadcaster.cs:86-88`):
```csharp
if (!TrySendUdpEntityUpdate(session, entityId, data.Player, tick, stateHash))
{
    await SendBinaryEntityUpdate(session, entityId, data.Player, tick, stateHash);
}
```
Try UDP first → fall back to WebSocket binary → fall back to WebSocket JSON. Three-tier degradation.

**Verdict: Formal two-lane architecture. Game-critical packets (structures, session, events) are reliable-ordered. Cosmetic/transient packets (positions, velocity) are datagram with graceful fallback.**

---

## Prompt 3: Interest Management & Spatial Partitioning

**Finding: Grid-based spatial hashing with tiered AoI bands**

**SpatialGrid** (`Game.Simulation/World/SpatialGrid.cs:11-143`):
- Fixed 50-unit cells in XZ plane (Y ignored for AoI)
- 64-bit hash key: `((long)cellX << 32) | (uint)cellZ`
- O(1) insert/update within same cell
- O(k) radius queries (iterate bounding box of cells, distance check)
- `ConcurrentDictionary` storage for thread safety

**InterestManager** (`Game.Simulation/World/InterestManager.cs:17-90`):

| Band | Distance | Update Rate | Effective Hz |
|------|----------|-------------|--------------|
| Near | 0–100 units | Every tick | 20 Hz |
| Mid | 100–300 units | Every 4th tick | 5 Hz |
| Far | 300+ units | Dropped | 0 Hz |
| Self | Any | Every tick | 20 Hz (always) |

**The server does NOT broadcast all updates to all clients.** Each player gets a filtered view based on spatial proximity. The filtering happens per-tick in `TickBroadcaster.cs:71-72`:
```csharp
var visible = _interest.FilterForObserver(
    session.PlayerId, regionChanges, players, tick);
```

**Bandwidth profile at 20Hz with binary serialization (39 bytes per entity_update frame):**

| Scenario | Near Entities | Mid Entities | Downstream |
|----------|---------------|--------------|------------|
| Isolated player | 0 | 0 | 0.78 KB/s (self only) |
| Small group (5 near) | 5 | 0 | 4.7 KB/s |
| Crowded (10 near, 5 mid) | 10 | 5 | 8.8 KB/s |
| Dense (20 near, 10 mid) | 20 | 10 | 17.5 KB/s |

**For the sub-3.6 KB/s core stream target:** An isolated player consumes ~1 KB/s total (binary). A small group of 3-4 players stays under 3.6 KB/s. Larger groups exceed the dialup constraint but benefit from binary compression keeping it 5x lower than JSON.

**Verdict: Grid-based spatial partitioning with 3-tier AoI filtering. Not a naive broadcast. The isolated/small-group scenario meets the 3.6 KB/s target; larger groups rely on binary serialization to stay within modern low-bandwidth connections (3G/4G).**

---

## Prompt 4: Serialization & Bit-Packing Efficiency

**Finding: Custom bit-packing with sub-byte writes, no delta compression**

**BitWriter/BitReader** (`Game.Contracts/Protocol/Binary/BitWriter.cs:1-116`):
- Custom implementation with `_bitPosition` tracking individual bits
- `WriteBits(uint value, int bitCount)` — packs arbitrary bit-width values
- `WriteVarInt`, `WriteBool`, `WriteInt16`, `WriteUInt16`, `WriteUInt32`
- Big-endian bit order (MSB first)
- `stackalloc`-friendly (ref struct) — zero heap allocation on hot paths

**CompactVec3** (`Game.Contracts/Protocol/Binary/CompactVec3.cs:16-66`):
- 3 × 16-bit signed integers = **48 bits (6 bytes)** per position
- X, Z: ±32,767 units at 1-unit precision (covers 65 km²)
- Y: ±3,276.7 units at 0.1-unit precision (scaled by 10x)
- Compare to JSON `{"x":1.5,"y":0,"z":3.2}` = ~30 bytes → **5x reduction per position**

**BinaryEnvelope** (`Game.Contracts/Protocol/Binary/BinaryEnvelope.cs:1-93`):
- 43 bits packed in 6 bytes: version(4) + type(6) + lane(1) + seq(16) + payloadLen(16)
- Compare to JSON envelope overhead: ~80-120 bytes → **13-20x reduction**

**Payload sizes** (from `PayloadSerializerTests.cs`):

| Message | Binary | JSON | Reduction |
|---------|--------|------|-----------|
| PlayerInput | 5 bytes | ~120 bytes | **96%** |
| EntityUpdate | ~33 bytes | ~200+ bytes | **84%** |
| Full EntityUpdate frame (envelope + payload) | ~39 bytes | ~280+ bytes | **86%** |

**Delta compression: NOT implemented.** Each `entity_update` sends full absolute position + velocity. The `StateHasher` computes a CRC32 of the entire world state (all positions, velocities, tick number) — this is for desync detection, not delta encoding. There is no XOR/diff against a "golden state."

**Impact of missing delta compression:** If only heading changed, the system still sends the full 33-byte payload. With delta encoding, unchanged positions could be skipped or sent as 1-2 bit flags. However, the current 33-byte payload is already small enough that delta compression would save ~10-15 bytes at the cost of significant complexity (ack tracking, golden state management, retransmission logic).

**Verdict: Strong custom bit-packing with sub-byte precision. 84-96% reduction over JSON. No delta compression — full state per update, but the full state is already very compact (33 bytes). The trade-off is pragmatic: delta compression would add complexity for marginal gains at this payload size.**

---

## Prompt 5: Resilience & Progressive Enhancement Logic

**Finding: Three-tier transport degradation, no client prediction, fully playable on WebSocket JSON alone**

**Degradation chain** (from `TickBroadcaster.cs` and `GameWebSocketMiddleware.cs`):

```
Tier 1: UDP binary (lowest latency, no head-of-line blocking)
  ↓ fails?
Tier 2: WebSocket binary (reliable, compact, TCP ordered)
  ↓ not supported?
Tier 3: WebSocket JSON (reliable, verbose, universal)
```

Each tier is a complete implementation — not a stub. The game works identically on any tier; only bandwidth and latency characteristics differ.

**If the datagram channel latency spikes or is unavailable:**
- UDP is best-effort with no retransmission. If packets are lost, the next entity_update (50ms later) supersedes it. No client-visible artifact beyond slightly jerkier movement.
- If UDP is completely unavailable (NAT, firewall), the system falls back to WebSocket binary transparently. No client action required — the server detects `session.UdpEndpoint == null` and skips UDP.
- If binary mode is disabled, full JSON works identically (just more bytes).

**Client-side prediction: NOT implemented (by design).**

The Godot client (`remote_entity.gd`) uses interpolation only:
```gdscript
position = position.lerp(target_position, delta * interpolation_speed)
```

The client sends input, waits for the server's authoritative position, and interpolates toward it. There is no local physics simulation, no dead reckoning, and no reconciliation loop. The `input_seq` echo in `entity_update` exists for future client prediction but is not consumed by the Godot client today.

**Why this is acceptable for the vertical slice:** The game is a building/survival game (like Valheim), not an FPS. 50-100ms input latency is imperceptible for structure placement, inventory management, and walking. Client prediction becomes necessary for combat (future slice — explicitly parked in `godot-client-plan.md`).

**Minimum viable bandwidth (WebSocket JSON only, single player):**
- Upstream: ~2.4 KB/s (PlayerInput at 20Hz)
- Downstream: ~4 KB/s (self EntityUpdate at 20Hz)
- **Total: ~6.4 KB/s** — playable on any modern connection

**Minimum bandwidth (binary, single player):**
- Upstream: ~0.22 KB/s
- Downstream: ~0.78 KB/s
- **Total: ~1 KB/s** — playable on 28.8k dialup

**Verdict: Full progressive enhancement. The game is playable on JSON-over-WebSocket alone (6.4 KB/s). Binary reduces this to 1 KB/s. UDP is a bonus for latency, not a requirement. No client prediction yet, but the server echoes input_seq for future implementation. The Godot client is deliberately a presentation-only shell.**

---

## Scoring

### "Bankrupt" Penalty Check

| Condition | Status | Evidence |
|-----------|--------|---------|
| Tick rate tied to rendering frame rate | **NO** | `PeriodicTimer(50ms)` in `BackgroundService`, no render coupling |
| No state hashing for desync detection | **NO** | `StateHasher` computes CRC32 per tick, included in every `entity_update` |

**Neither penalty condition is met. Score is uncapped.**

### Scoring Matrix Application

| Criterion | Score Range | Assessment |
|-----------|-------------|------------|
| Input-driven simulation | 0.9-1.0 | Client sends 5-byte input deltas, server computes all physics. Deterministic tick loop at fixed 20Hz. |
| Channel bifurcation | 0.9-1.0 | Formal two-lane classification (Reliable/Datagram). UDP + WebSocket with three-tier fallback. |
| Interest management | 0.8-0.9 | Grid-based spatial hash with 3-tier AoI bands. Sub-3.6 KB/s for isolated/small groups. Larger groups exceed but benefit from 5x binary compression. |
| Serialization efficiency | 0.7-0.8 | Custom bit-packing with 84-96% reduction. No delta compression — full state per update, but payload is already compact (33 bytes). |
| Progressive enhancement | 0.8-0.9 | Three-tier transport degradation. Playable on JSON-only at 6.4 KB/s, 1 KB/s with binary. No client prediction yet (by design for building/survival — parked for combat). |

### Composite Score: **0.85 — "Era-Authentic" (high end)**

The system achieves strong compliance with the thesis. The primary gap preventing a 0.9+ "Thesis Gold" score:

1. **No delta compression.** Full 33-byte state per entity per update. With delta encoding, unchanged entities could be sent as 1-2 byte "no change" flags, cutting bandwidth further in static scenes. This is a pragmatic trade-off — the 33-byte payload is already small, and delta compression would add significant protocol complexity (ack tracking, golden state management).

2. **No client-side prediction.** The Godot client is interpolation-only. For the building/survival vertical slice this is acceptable (50-100ms latency is fine for placing structures). For combat, prediction will be required. The server-side infrastructure exists (input_seq echo in entity_update) but the client doesn't consume it yet.

3. **AoI bandwidth in crowds.** With 20 near players, downstream hits ~17.5 KB/s binary — well above the 3.6 KB/s dialup target. The AoI filtering prevents O(N) broadcast but doesn't achieve the theoretical minimum for dense scenarios. Practical mitigation: survival games rarely have 20+ players in a 100-unit radius.

### Score Trajectory

| Date | Score | Key Change |
|------|-------|------------|
| 2026-03-26 (pre-refactor) | 0.20 | JSON-only, no binary, no AoI, no input-driven simulation |
| 2026-03-26 (post-refactor) | 0.85 | All 5 network phases complete |
| 2026-03-27 (current) | 0.85 | Godot client added (interpolation-only, no prediction) |

### Path to 0.9+ ("Thesis Gold")

| Improvement | Estimated Impact | Complexity |
|-------------|-----------------|------------|
| Delta compression for entity updates | +0.05 | High (ack tracking, golden state) |
| Client-side prediction in Godot | +0.05 | Medium (local physics, reconciliation loop) |
| Adaptive AoI (shrink near radius under load) | +0.02 | Low |
| View-frustum culling (skip entities behind camera) | +0.02 | Low |

These are all parked for future slices. The current 0.85 score validates the architecture and proves the thesis is achievable.
