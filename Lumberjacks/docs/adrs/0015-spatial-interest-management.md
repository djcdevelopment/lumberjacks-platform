# ADR 0015: Spatial Interest Management & Grid Hashing

**Status:** Accepted
**Date:** 2026-03-28
**Depends on:** ADR 0014 (Input-Driven Deterministic Simulation)

## Context

A core architectural thesis is to maintain a playable sub-3.6 KB/s simulation stream (ADR 0003). While ADR 0012 (Binary Payload Serialization) significantly reduced the size of individual message updates, a "naive" broadcasting system that sends every world update to every connected client still scales exponentially with the number of players.

In regions with 100+ entities, broadcasting all updates even in binary format would exceed the bandwidth target and overload client-side deserialization.

## Decision

Implement a **Spatial Interest Management** system to filter authoritative updates based on player proximity.

Specifically:
- **SpatialGrid**: Partition the world into a fixed 2D grid (XZ-plane). When entities move, they are re-hashed into their current grid cell.
- **Interest Managed Tick Frequency**:
    *   **Near (0-100u)**: Every tick (20Hz). High fidelity for immediate interactions.
    *   **Mid (100-300u)**: Every 4th tick (5Hz). Secondary updates for distant context.
    *   **Far (300+u)**: Effectively dropped. Background context only updated on demand.
- **TickBroadcaster**: Before sending an `entity_update`, the broadcaster queries the `SpatialGrid` for nearby entities within the player's current "Interest Circle" and filters the payload accordingly.

## Consequences

Positive:
- **Broadcasting Efficiency**: Bandwidth consumption is decoupled from the total server-side entity count. A single client's inbound stream is capped by the number of *nearby* entities.
- **Performance**: High-speed radius queries via the `SpatialGrid` avoid $O(N^2)$ distance checks between all players.
- **Predictability**: Maintains the sub-3.6 KB/s bandwidth target even in high-density regions.

Negative:
- **Visual Artifacts**: Entities at the "Mid" range update at a lower frequency (5Hz), requiring more aggressive interpolation on the client side.
- **Grid Maintenance**: Entities must reliably re-hash their grid cell position on every movement to avoid "ghost" entities or missing updates.
