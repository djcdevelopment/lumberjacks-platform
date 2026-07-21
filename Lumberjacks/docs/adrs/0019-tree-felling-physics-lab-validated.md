# ADR 0019: Tree Felling Physics — Lab-Validated, Network-Projected

**Status:** Accepted
**Date:** 2026-03-31
**Depends on:** ADR 0012 (Binary Payload Serialization), ADR 0015 (Spatial Interest Management)

## Context

Tree felling is a core gameplay mechanic and the primary stress test for the platform's spatial interest management and binary serialization systems. The physics must be:
1. Realistic enough to reward skill (notch placement, hinge control, reading lean)
2. Cheap enough to send over the network (sub-3.6 KB/s per ADR 0012)
3. Deterministic for server-authoritative simulation (ADR 0014)

A Tree Felling Physics Lab was built to prototype and validate the physics before committing to a network protocol. The lab uses a detailed polar cross-section model (36 sectors × N height slices) that is too expensive for network transmission but ideal for understanding the physics.

## Reference Material

The physics model is grounded in real forestry field manuals and academic research, not invented game mechanics:

| Source | Location | What It Contributed |
|--------|----------|-------------------|
| **USDA Forest Service — "An Ax to Grind"** | [`tools/ideas/`](../../tools/ideas/) | Notch types (conventional 45°, open-face 70°+), back cut placement (2" above notch floor), hinge dimensions, felling procedures, limbing/bucking |
| **Wisconsin DNR Timber Felling Manual** | [`tools/ideas/`](../../tools/ideas/) | Bore cutting technique, barber chair risk criteria, hinge sizing (width ~10% DBH, length ~80% DBH), open-face technique, felling against natural lean |
| **Rod Cross — "Physics of swinging a striking implement" (2009)** | [`tools/ideas/`](../../tools/ideas/) | Driven circular arc model (not pendulum). Gravity negligible mid-swing. V_head = ω × R. Centripetal force 25× stronger than gravity. |
| **Pluta & Hryniewicz — Cutting dynamics papers** | [`tools/ideas/cutting/`](../../tools/ideas/cutting/) | Three forces in wood cutting: gravity, inertia, material reaction. Informed penetration model. |

Every physics parameter — Janka hardness by species, hinge strength via section modulus, penetration depth from kinetic energy — traces back to these sources.

## Decision

### Physics Model

Adopt a **polar cross-section trunk model** for physics simulation, with a **compact 6-float projection** for network transmission.

**Simulation (server-side, authoritative):**
- Trunk modeled as vertical slices with 36 angular sectors of remaining wood
- Cuts remove material from sectors; hinge = remaining bridge between notch and back cut
- Fall dynamics: tipping moment vs hinge resistance, with barber chair detection
- Axe swing physics per Rod Cross (2009): driven circular arc, V_head = ω × R, gravity negligible

**Network (entity_update datagram):**
- `CompactTreeState`: 6 floats = 24 bytes per tree state update
  - notch_angle, notch_depth, backcut_depth, hinge_width, fall_tilt, fall_bearing
- Fits within the 33-byte entity_update envelope (ADR 0012)
- Client reconstructs visual notch geometry from these 6 parameters

### Gameplay Loop Integration

The felling loop maps directly to AoI tiers (ADR 0015):
- **Far (300+u):** Tree silhouette only. No felling data.
- **Mid (100-300u, 5Hz):** Species, age, lean direction visible. Player begins "cruising."
- **Near (0-100u, 20Hz):** Full detail: growth history, fire scars, crown distribution, knot locations. Player can Inspect, plan the fell, and execute cuts.

Staying near a tree to inspect it before felling is intentional game design — it exercises the progressive loading system and rewards patience with better information.

## Consequences

Positive:
- **Network efficient:** 24 bytes per tree update vs ~2-3KB for full polar model (100× reduction)
- **Deterministic:** All physics run server-side from compact inputs (strike angle + height + effort)
- **Skill-expressive:** Notch type, depth, back cut position, and hinge width all affect fall direction and barber chair risk
- **Lab-proven:** Physics validated interactively before network integration

Negative:
- **Visual approximation:** Client reconstructs notch geometry from 6 floats, not the full 36-sector model. Cut visuals are approximate but physics are authoritative.
- **Lab-only detail:** The polar slice model doesn't ship to clients. Lab is the only place to see per-sector material removal.

## Follow-Up Work

- Integrate `CompactTreeState` into `EntityUpdate` binary serializer
- Add `tree_inspect` message type to delivery lane classification (Reliable lane — triggers progressive detail loading)
- Implement community "cruising log" heat maps (route + inspected trees + fell results)
