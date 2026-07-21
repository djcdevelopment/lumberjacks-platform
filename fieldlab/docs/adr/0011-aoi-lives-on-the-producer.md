# ADR 0011 — Area-of-interest lives on the producer, and suppress/ack/emit are three things

- **Status:** Accepted (2026-07-21)
- **Rung:** Valheim netcode replacement (I-ladder, ZDO redirect); armed as normal-play behaviour on P7

## Context

The measurement in `fieldlab/evidence/aoi-baseline-20260721/` settled that **send-volume is the tick
ceiling** — the AoI *filter* is ~4% of tick time; the cost is serialising and shipping updates — and
that an aggressive distance-band cut buys ~8× p99 headroom. So the win is to send less, shaped by
distance, per observer.

Two systems could host that shaping, and they are not interchangeable (see
`aoi-baseline-20260721`, §"the two systems"):

- The **Lumberjacks game server** already has a mature three-tier `InterestManager` — but it filters
  *its own* game's player broadcasts and is not in the Valheim path at all.
- The **Valheim path** is a Harmony mod that intercepts the dedicated server's ZDO send
  (`ZDOMan.CreateSyncList`, once per peer) and **redirects** chosen ZDOs to the Lumberjacks gateway,
  which relays them to other clients' consumers by pull (`/pending` + ack).

Mapping the Valheim path showed the spatial decision has exactly one possible home: the **mod
(producer)** — it is the only place that sees the observing peer's position and each ZDO's position at
decision time. The gateway is a passive, recipient-partitioned reliable queue with **no** spatial
filter (distance only tie-breaks delivery order); making it distance-aware would mean teaching a dumb
relay the geometry the producer already has.

## Decision

**Distance-band AoI for the Valheim path is enforced mod-side, at redirect time, on the producer.**
Per observing peer: near (0–`inner`) redirect every pass; mid (`inner`–`outer`) thinned to `thinHz`;
far (>`outer`) dropped; landmarks (granted reach) always emitted regardless of band. The gateway stays
a passive relay. It is **armed as normal-play behaviour** on P7 (not a test-only mode), behind
`zdoBandShapingEnabled` (default false) for instant rollback.

### The load-bearing invariant: suppress, ack, and emit are three separate operations

The original redirect **fused** three things into one call: (1) remove the ZDO from Valheim's send
list, (2) **ack** it into the peer's bookkeeping so vanilla re-offers it only on revision change, (3)
emit the wire envelope to the gateway. Band-shaping requires splitting them, and the split has hard
rules:

1. **Any ZDO removed from the native send list MUST be acked.** Remove-without-ack makes vanilla
   re-select it every tick — a duplicate storm. This is non-negotiable and applies to *every* band
   action (full, thinned-emit, thinned-hold, drop, landmark).
2. **Only emit for full / thinned-due / landmark.** A dropped or held ZDO is ack-but-don't-emit.
3. **Suppressed-not-emitted ZDOs must NOT touch the delivery-gate counters** (`seq`/`suppressed`).
   Those count *emitted* redirects and the gate reads gateway `distinct_seq` against them; a dropped
   ZDO that bumped `seq` would read as false loss (`missing_seq`).

Thinning is **time-gated against a clock** (`Time.time`, keyed per `(recipient, zdo uid)`), not a
mod-owned counter — because the mod only sees a ZDO when native presents it, once per peer per pass.

## What this changes

- `ZdoRedirectRunner.Redirect` split into `SuppressNative` (the ack) + emit; `Process` classifies each
  admitted candidate via the pure, Unity-free `ZdoBandPolicy.Classify` and routes it.
- New config `[Netcode]`: `zdoBandShapingEnabled` / `zdoInnerRadiusMeters` (30) / `zdoOuterRadiusMeters`
  (64 = Valheim's zone size) / `zdoThinHz` (5).
- Landmark reach (ADR-less, task 7) is the escape hatch that makes an aggressive near-cut affordable:
  a granted-reach object rides the region-wide reliable lane and is never subject to the band cut.

## Consequences

- **Validated at worst-case single-player density in production:** ~85% of redirect candidates dropped,
  losslessly (`missing_seq=0`, `duplicates=0`, consumer caught up). Reference:
  `fieldlab/evidence/aoi-band-shaping-p7-baseline-20260721/`.
- **Two unproven risks, accepted deliberately** (Derek chose to keep it armed as normal play):
  1. **Far → approach re-sync.** Because a dropped far object is *acked*, native believes the peer has
     it; a **static** far object a player leaves and returns to may not be re-offered by native and is
     not re-emitted by the mod. Zone-entry force-send *probably* re-triggers it, but this was not
     exercised. If "distant builds don't reload on return" is ever reported, this is the first suspect.
  2. **Multi-player.** Cost is observers × changed-entities; two players in one dense area is untested.
- **The primary-mode coupling makes the drop real.** In `lumberjacks-primary` the native send is
  suppressed and delivery relies on the gateway, so a drop genuinely withholds state — this is only
  safe because far state is not needed until proximity re-includes it (ADR 0010).
- **Damping still owed.** Per ADR 0010, the band edges (30/64m) need hysteresis or an object riding a
  boundary flaps bands every pass. Folded into "v.5".

## Related

`network/mod/ComfyNetworkSense/Core/Services/ZdoBandPolicy.cs`,
`ZdoRedirectRunner.cs`, `ZdoIntegrationContract.cs`; ADR 0010 (why far-drop is safe — predictable
falloff); `fieldlab/evidence/aoi-baseline-20260721/` and `.../aoi-band-shaping-p7-baseline-20260721/`;
`Lumberjacks/docs/network/landmark-reach-design.md`; the approved plan (AoI end-to-end, MVP → v.5).
