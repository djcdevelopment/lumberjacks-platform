# Testing Strategy & Coverage

This document outlines the testing methodology, suite structure, and current coverage for the Community Survival Platform.

## 🧪 Testing Theory

We follow a tiered testing approach to ensure both technical correctness and high-performance networking behavior:

1.  **Unit Tests (C# / .NET)**: Validating the deterministic core, binary serialization, and physics simulation. These run in isolation and are extremely fast.
2.  **Integration Tests (C# / .NET)**: Testing internal service interactions (e.g., Gateway ↔ Progression ↔ EventLog) without involving real network sockets.
3.  **E2E Smoke Tests (Node.js)**: Automated headless bot scripts (`scripts/test-*.js`) that connect to a live or local server, simulating real-world gameplay sequences (movement, placement, inventory).
4.  **Load Tests (Node.js)**: Stress testing high-concurrency scenarios (50-100+ bots) to validate the dual-channel transport and offloading logic.

---

## 📊 Suite Breakdown

### 1. Game.Contracts.Tests (~106 tests)
*Located in: `tests/Game.Contracts.Tests`*

This suite validates the shared data models, binary protocol, and serialization logic. This is the foundation of the deterministic simulation.

| Component | Coverage Area |
| :-- | :-- |
| **Binary Serialization** | Bit-level packing/unpacking, varint encoding, and envelope framing. |
| **MessageTypeMapping** | 1:1 mapping between string constants and 6-bit binary IDs. |
| **Vector Compression** | `CompactVec3` 48-bit precision vs accuracy validation. |
| **Payload Serializers** | Bin-packing efficiency for `EntityUpdate`, `PlayerInput`, and `EntityRemoved`. |
| **UDP Packet Format** | Token-prefixed datagram structural integrity. |

### 2. Game.Simulation.Tests (~51 tests)
*Located in: `tests/Game.Simulation.Tests`*

This suite validates the server-authoritative physics and interest management logic.

| Component | Coverage Area |
| :-- | :-- |
| **Physics Simulation** | Friction, bounds clamping, move-validation, and tick-alignment. |
| **Spatial Partitioning** | `SpatialGrid` hashing and radius-based entity queries. |
| **Interest Management** | AoI filtering (Near/Mid/Far bands) for per-player broadcasts. |
| **Input Queueing** | Deterministic processing of sequence-buffered player inputs. |
| **State Hashing** | Simulation state snapshots and hash consistency across ticks. |

---

## 🚀 Running the Tests

### Automated Unit Tests
Run the following from the root directory to execute the full C# suite:

```bash
dotnet test Game.sln --filter Category!=Performance
```

**Expected Sample Output:**
```text
  Game.Contracts.Tests test succeeded (2.4s)
  Game.Simulation.Tests test succeeded (2.3s)

Test summary: total: 157, failed: 0, succeeded: 157, skipped: 0, duration: 4.0s
```

### E2E Smoke Testing
Smoke tests require the local stack to be running (`npm run dev`).

```bash
# Basic vertical slice validation
node scripts/test-vertical-slice.js

# Specific behavior tests
node scripts/test-movement.js
node scripts/test-multiplayer.js
node scripts/test-challenges.js
```

### High-Load Validation
For transport-specific stress testing (WebSocket vs UDP), refer to:
👉 **[Load Test Results & Methodology](load-test-dual-channel-results.md)**

---

## 📉 Coverage Heatmap (Estimated)

*   **Protocol & Serialization**: 95% (Critical Path)
*   **Physics Core**: 90% (Critical Path)
*   **Spatial SIM**: 85%
*   **Inventory / Persistence**: 70%
*   **Admin UI / Web**: 40% (Tested via manual smoke runs)
