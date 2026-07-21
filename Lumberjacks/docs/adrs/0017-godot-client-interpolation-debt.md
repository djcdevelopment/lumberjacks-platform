# ADR 0017: Godot Client Interpolation Strategy

**Status:** Accepted (Technical Debt)
**Date:** 2026-03-28
**Depends on:** ADR 0015 (Spatial Interest Management)

## Context

ADR 0015 introduces variable tick rates for entities based on proximity:
- **Near**: 20Hz (every 50ms)
- **Mid**: 5Hz (every 200ms)

A naive linear interpolation (lerping towards the latest target) assumes a constant update frequency. If the client assumes 20Hz but receives a 5Hz update, the entity will appear to "snap" to the end of its path and wait for the next update, causing visible stutter.

## Decision

For the initial Godot client, we will implement **Basic Linear Interpolation with Velocity Estimation** rather than a full multi-packet jitter buffer.

Specifically:
- Each `RemoteEntity` will track the `delta` time since its last `entity_update`.
- It will lerp towards the target position using a speed modified by the *actual arrival interval* of the last packet.

## Consequences

Positive:
- **Simplicity**: Avoids the complexity of managing a 100-250ms "delay buffer" for all remote entities.
- **Responsiveness**: Local player sees remote movements as soon as they arrive (minus the lerp time).

Negative:
- **Stutter**: Distant entities (5Hz) will still exhibit some "micro-snapping" or "rubber-banding" during fast direction changes because the client is effectively extrapolating without a safety buffer.

## Mitigation (Future Effort Savings)

- We will implement the lerp in a dedicated `_process(delta)` loop using Godot's built-in `lerp` and `lerp_angle`, but keep the "Target Data" separate from the "Rendered Data" to allow swapping in a more robust jitter buffer later.
