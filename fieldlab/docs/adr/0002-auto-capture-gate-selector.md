# ADR 0002 — Auto-capture selector + observe-during-change for one-window behaviour gates

- **Status:** Accepted (2026-07-10)
- **Rung:** I2 / P3 (ownership pin); intended to generalize to I3–I5

## Context

A behaviour-change rung needs a gate that shows both the *change* (the thing we altered) and a
*negative control* (the thing we left alone still works), ideally in **one** hands-free player-join
window on the live world. The naive approach — pre-select which ZDOs to pin by prefab — needs
foreknowledge of what will churn on the route, which we did not have, and risks an empty or lopsided
window.

## Decision

Two design moves, taken together:

1. **Auto-capture selector.** When armed, the pin captures the first `N` (default 25) ZDOs that
   *already have a real owner* and that the churn funnel tries to transfer, then holds them and every
   subsequent change on them. Everything past the cap / unowned transfers normally. No prefab
   foreknowledge; the captured set self-selects from live churn, and the cap keeps the blast radius
   test-scoped (≤25 of ~9.15M objects). An optional prefab allowlist remains for targeted tests.
2. **Leave the observe seam running *during* the change.** The observe-only logger stays on while the
   pin runs, so the transfers the pin *allowed* (pass-throughs) are independently recorded per-object
   by a *separate* patch class writing a *separate* file.

## Consequences

- **One window yields both gates.** Pinned holds (the change) and pass-through transfers (the negative
  control) are captured together; the gate reads `held_with_negative_control` when both are present on
  an authoritative window. On the live run: 25 pinned / 55 holds vs 262 pass-throughs.
- **Built-in cross-check.** The pin's pass-through *count* and the observe seam's per-object
  transitions agree on different objects — an independent second measurement, not a self-report.
- **Ordering constraint.** Two patches on the same method require the observing patch to run first
  (`Priority.Last` on the blocker) or its `__state` is corrupted — see ADR 0001's sibling lesson and
  memory [[valheim-harmony-multi-prefix-ordering]].
- **Trade-off:** the captured set is non-deterministic run-to-run. Fine for proving an invariant;
  for pinning *specific* objects, use the prefab allowlist instead.

## Related

ADR 0001, `evidence/i2-pin/ANALYSIS.md`, `retro/SESSION-RETRO-2026-07-10.md` (lesson `L-2026-07-10-3`).
