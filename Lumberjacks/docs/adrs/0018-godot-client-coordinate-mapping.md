# ADR 0018: Godot Client Coordinate System & Input Mapping

**Status:** Accepted
**Date:** 2026-03-28

## Context

Godot 4 3D coordinates use:
- **+Y**: Up
- **-Z**: Forward (Camera looks this way by default)
- **+X**: Right

The Backend simulation uses:
- **Plane (X, Z)**: Movement plane
- **+Z**: North (0° in compass mapping)
- **+X**: East (90° in compass mapping)

Without a strict convention, WASD movement signals (North/South/East/West) will likely result in "mirrored" or "rotated" movement on the screen.

## Decision

We will adopt the following mapping for the Godot Client Vertical Slice:
- **Server North (+Z)** → **Godot -Z** (Visual Forward)
- **Server East (+X)** → **Godot +X** (Visual Right)

Input directions will be converted to the server's byte-based compass (0-255) as follows:
- **W**: Sends 0 (North)
- **D**: Sends 64 (East)
- **S**: Sends 128 (South)
- **A**: Sends 192 (West)

## Consequences

Positive:
- **Consistency**: All developers (and AI) will use the same orientation, avoiding "the player walks backwards" bugs.
- **Interoperability**: Matches the expectations of the `scripts/test-multiplayer.js` logic.

Negative:
- **Axis Swap**: We must remember that server `Z` maps to Godot `-Z`.

## Mitigation (Future Effort Savings)

Create a `ServerInterop.gd` static helper class that contains methods like `godot_to_server_pos(v3)` and `server_to_godot_pos(dict)` to encapsulate this mapping.
