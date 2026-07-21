# Tech Debt: Tree Felling Lab (2026-03-31)

Technical debt identified during the tree felling physics lab build.

## Summary

| Item | Severity | When It Bites | Fix Effort |
|------|----------|---------------|------------|
| Axe swing animation pivot | **Visible** | Now (cosmetic) | Small |
| No chip formation / multi-strike accumulation | Limiting | When testing USFS technique | Medium |
| Penetration scaling is empirical | Acceptable | When tuning against real data | Small |
| Mixed coordinate systems (sim vs Godot) | Limiting | When porting to server | Medium |
| No tree-on-tree collision | Limiting | Multi-tree scenarios | Large |
| Server still uses flat -5HP model | **Gap** | When shipping felling UX | Large |
| `CompactTreeState` not wired to binary serializer | Blocking | Before E2E tree felling | Medium |

---

## Axe Swing Animation Pivot

**Where:** `TreeFellingLab.cs`, `StartAxeSwing()` method

The axe mesh is a child of `_axeNode`, but the tween rotates `_axeNode` around the wrong axis. The node's origin should be at the player's grip (pivot point), with handle + blade extending outward. Multiple attempts to fix the rotation axis failed because the fundamental node hierarchy is wrong — the meshes are centered on the node instead of offset from it.

**Fix:** Create a proper pivot hierarchy: `_axeNode` (at grip position, empty Node3D) → `_axeHandle` (offset along handle direction) → `_axeBlade` (at handle tip). The tween rotates `_axeNode`, which sweeps the entire handle+blade assembly in an arc.

**Effort:** Small — restructure BuildPlayer(), ~30 lines changed.

---

## No Chip Formation Model

**Where:** `TreeFellingSim.ApplyAxeStrike()`

Each strike independently removes a wedge of material. In reality, chips only pop out when strikes from alternating angles relieve stress (USFS manual: "top, bottom, middle" sequence). The sim doesn't track recent strike angles or model chip accumulation.

**Fix:** Track the last 3-5 strike angles per slice. Apply a "chip factor" bonus when strikes alternate sides of the notch. First strike at an angle removes less; second strike from opposite side pops the chip (bonus removal).

**Effort:** Medium — new tracking array, modified removal logic.

---

## Server Felling Model Gap

**Where:** `SimulationStep.cs` lines 44-89

The server still uses the original flat model: -5 HP per hit, lean accumulated from player direction byte, fall heading = lean + wind. This is disconnected from the lab's physics model (notch geometry, hinge analysis, barber chair detection).

**Bridge plan:**
1. Server receives: strike_angle (1 byte), strike_height (1 byte), swing_effort (1 byte) = 3 bytes input
2. Server runs `ApplyPhysicsStrike()` per hit (the sim has no Godot dependency — portable)
3. Server sends `CompactTreeState` (24 bytes) in entity_update datagrams
4. Client reconstructs visual notch from compact state

**Effort:** Large — requires wiring TreeFellingSim into Game.Simulation, adding CompactTreeState to binary serializer, and updating client deserialization.

---

## CompactTreeState Not Yet Wired

**Where:** `TreeFellingSim.GetCompactState()` exists but is not called by any network code.

The struct is defined and the projection method works, but `EntityUpdate` binary serializer doesn't know about tree-specific state yet. Currently trees are just generic entities with a health float.

**Fix:** Add tree state to the entity_update message type. Either as a sub-type flag in the binary envelope (1 byte type discriminator) or as a separate `tree_update` message in the delivery lane classification.

**Effort:** Medium — touches BinaryEnvelope, PayloadSerializers, and Godot deserialization.
