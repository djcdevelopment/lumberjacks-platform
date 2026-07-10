# ADR 0001 — Guard `ZDO.SetOwnerInternal` directly, not the OwnerRevision race

- **Status:** Accepted (2026-07-10)
- **Rung:** I2 / P3 (ownership pin)
- **Supersedes:** the caveat-#2 "option b" in `NETCODE-OWNERSHIP-MAP.md` (as originally written)

## Context

The I2 ownership pin must stop the server from transferring a pinned ZDO's owner. Ownership moves
through two funnels: the server-driven churn (`ZDOMan.ReleaseNearbyZDOS` → `ZDO.SetOwner`) and the
revision-silent remote-apply (`ZDOMan.RPC_ZDOData` → `ZDO.SetOwnerInternal`). The ownership map
offered two strategies for the second funnel:

- **(a)** guard `SetOwnerInternal` directly, or
- **(b)** keep the pinned ZDO's `OwnerRevision` highest so `RPC_ZDOData` rejects inbound claims at
  its revision gate.

The observe-only capture (mod 0.5.9) suggested (b) might hold, because the observed remote applies
arrived at low revisions.

## Decision

**Guard `SetOwnerInternal` directly (strategy a).** A Harmony prefix returning `false` skips the
vanilla owner change on pinned ZDOs, scoped to the `RPC_ZDOData` funnel via a depth flag so
create/load/convert calls are untouched.

## Why — the source, not the doc

Re-reading the decompiled `RPC_ZDOData` body (not the summary) revealed a **hole in strategy (b)**:
the OwnerRevision gate only applies on the *stale-data* branch (`num4 <= DataRevision` →
`if (num3 > OwnerRevision) SetOwnerInternal(...)`, ZDOMan:826-834). On the *newer-data* branch
(`num4 > DataRevision`, ZDOMan:842-844) the code runs `OwnerRevision = num3; SetOwnerInternal(owner)`
**unconditionally** — a client that modifies a pinned object's data *and* claims it would flip the
owner regardless of revision. Strategy (b) alone leaks that path.

The live gate confirmed it: **21 of 55 holds fired on the `SetOwnerInternal` path.** Under strategy
(b) those 21 would have leaked.

## Consequences

- The pin covers both funnels; the "owner stays pinned" gate passed with a live negative control.
- `SetOwnerInternal` is a hot, widely-called method — the guard is gated on `_active != null` (a
  volatile read) and a funnel-scope depth flag, so idle cost is negligible and non-churn callers
  (create/load) are never affected.
- General rule this establishes: **design behaviour-changes against the decompiled bodies, not the
  map** (see [[post-i0-distrust-rule]]; retro lesson `L-2026-07-10-1`).

## Related

`NETCODE-OWNERSHIP-MAP.md` (corrections section), `evidence/i2-pin/ANALYSIS.md`, ADR 0002,
`retro/SESSION-RETRO-2026-07-10.md`.
