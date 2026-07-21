# ADR 0014: Input-Driven Deterministic Simulation

**Status:** Accepted
**Date:** 2026-03-28
**Depends on:** ADR 0005 (.NET as Authoritative Backend Runtime)

## Context

The platform's initial movement implementation relied on "Server-Tells-All" absolute position synchronization. Clients would send their desired position, and the server would validate and broadcast the new absolute `Vec3`. 

While functionally correct for small numbers of players, this approach suffers from:
1.  **Bandwidth Bloat:** Sending absolute 64-bit coordinates frequently is expensive.
2.  **High Latency Sensitivity:** Any dropped packet results in immediate visual "warping" as the server's next authoritative position "snaps" the player.
3.  **Lack of Prediction Base:** Without a deterministic simulation, client-side prediction is nearly impossible to implement correctly without significant "rubber-banding."

## Decision

Shift the primary simulation loop to a **deterministic, input-driven model**.

Specifically:
- **Clients** no longer send positions. They send periodic **Input Vectors** (8-bit direction, 8-bit speed) along with a monotonically increasing `input_seq`.
- **The Server** maintains an `InputQueue` per player.
- **The Tick Loop** (20Hz) executes a `SimulationStep` that:
    1.  Drains inputs from the queue.
    2.  Applies deterministic physics (friction, acceleration, bounds clamping).
    3.  Updates the player's authoritative position.
- **State Hashes**: Every `entity_update` includes the simulation tick and the `last_input_seq` processed. This allows the client to reconcile its own locally predicted state against the server's truth.

## Consequences

Positive:
- **Bandwidth Reduction**: `PlayerInput` packets are reduced from ~120 bytes (JSON) to **5 bytes** (binary).
- **Determinism**: The same inputs on a clean state lead to the same position on both server and client, enabling smooth client prediction.
- **Robustness**: Dropped input packets can be recovered by the server processing subsequent, larger sequence "delta" buffers if needed.

Negative:
- **Complexity**: Server-side physics must be 100% deterministic (no floating-point drift or external time-dependencies).
- **Sync Overhead**: Requires rigorous clock synchronization and sequence management between client and server.
