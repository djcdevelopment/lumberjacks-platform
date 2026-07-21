# ADR 0012: Binary Payload Serialization

**Status:** Accepted
**Date:** 2026-03-27

## Context

The system previously used JSON payloads for all messages over the WebSocket transport. As the simulation became deterministic and input-driven, the frequency of `EntityUpdate` and `PlayerInput` messages increased significantly (e.g., 20Hz ticks). Realizing the platform's vision of supporting 100+ players per region and adhering to the 28.8k "dialup" bandwidth constraint (as referenced in ADR 0011) required drastic reductions in payload size. JSON's overhead, text representation of numbers, and lack of bit-level packing made it mathematically impossible to satisfy the bandwidth targets for high-frequency spatial streams.

## Decision

Adopt custom binary payload serialization for hot-path spatial and input messages, replacing JSON payloads for these specific types.

Specifically:
- Implement a 6-byte `BinaryEnvelope` framing format.
- Use `CompactVec3` (48-bit fixed-point) and bit-packing techniques for coordinates and input states.
- Expose `PayloadSerializers` for direct translation between structural records and raw byte sequences.
- Retain JSON for low-frequency "Reliable" lane messages (like chat or event emissions) where human readability and schema flexibility outweigh byte counts, until profiling dictates otherwise.

## Consequences

Positive:
- **Massive Bandwidth Savings:** `PlayerInput` size was reduced from ~120 bytes to precisely 5 bytes (a 96% reduction). `EntityUpdate` size dropped from >200 bytes to ~33 bytes (an 84% reduction).
- **Reduced Allocation:** Binary deserialization fast-paths enable direct mapping without generating heavy garbage-collected strings or nested JSON DOM objects.
- **Thesis Compliance:** Brings the primary simulation stream well below the 3.6 KB/s threshold per client, successfully earning the "Thesis Gold" score constraint.

Negative:
- **Client-Side Complexity:** The Godot client and any future client applications must implement matching bit-level deserializers instead of relying on standard JSON parsers.
- **Lost Readability:** Network debugging tools (like browser dev tools) can no longer trivially inspect the contents of hot-path messages without a specialized Wireshark dissect plugin or custom debug proxy.
