# ADR 0016: Godot Client Protocol Choice (JSON vs Binary)

**Status:** Accepted (Technical Debt)
**Date:** 2026-03-28

## Context

The backend has implemented a high-performance binary protocol (ADR 0012) using `BitWriter`/`BitReader` and specialized serializers that reduce bandwidth by up to 96%. However, GDScript lacks native bit-packing utilities comparable to .NET's `Span<byte>` and `BitConverter`, making a performant binary parser a significant upfront implementation cost for the "Vertical Slice."

## Decision

Initial Godot 4.x client development will prioritize **JSON over WebSocket** for all message types to accelerate visual validation of the simulation loop.

Specifically:
- We will use the Gateway's JSON fallback mode.
- We will accept the bandwidth "debt" (approx. 200 bytes per entity update vs. 19 bytes binary).

## Consequences

Positive:
- **Velocity**: Near-zero implementation time for message parsing using `JSON.parse_string()`.
- **Debuggability**: Message payloads are human-readable in the Godot console.

Negative:
- **Bandwidth**: Will exceed the 3.6 KB/s target if more than 5-10 entities are in the AoI.
- **Portability**: Transitioning to binary later will require replacing the core of the `NetworkManager`.

## Mitigation (Future Effort Savings)

To minimize future refactoring, we will:
1.  Isolate all JSON parsing within the `NetworkManager`.
2.  Expose signals (e.g., `entity_updated(dict)`) that are agnostic of the source format (JSON vs. Binary).
3.  Avoid passing raw JSON dictionaries directly into game logic; instead, map them to typed data objects as soon as possible.
