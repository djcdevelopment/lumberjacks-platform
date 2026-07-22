# ADR 0013 — Ownership, visibility, delivery, and ack are four things, not one

- **Status:** Proposed (2026-07-21)
- **Rung:** Valheim netcode replacement — multi-player area co-presence (the pinned "multi-player density" item, now with a live repro)

## Context

The first live two-human authoritative-cutover test (2026-07-21 night, `Durracktu` + `Prototyper`)
proved the recipient-partitioned durable queue is **multi-tenant-correct**: two players each drain
their own `(windowId, recipientId)` partition, `rejected=0`, `duplicates=0`, `pending` drained,
independent acks. That half is done (ADR 0020 queue; the `ActiveConsumers >= 1` gate lift, commit
`a66a7d6`).

But **area co-presence failed in-game.** Derek could see Prototyper as a *player*, but none of their
buildings; the moment Prototyper left the area, all the buildings reappeared. Portals showed the same
(`connected=0`). Full write-up: `fieldlab/FINDINGS-multiplayer-copresence-2026-07-21.md`.

The failure is **not** corruption. Canonical `ZDOMan` state, the producer's serialization, the queue,
the WAL, and per-partition acks are all healthy. The failure is isolated to **routing / relevancy** —
exactly where a game engine's replication graph lives.

**Root cause — a conflation.** The producer collapses four independent concepts onto one scalar
`recipient` stamp (backed by a global native-suppression and Valheim's ownership token):

1. `ZdoRedirectRunner.Process` (a Harmony postfix on `ZDOMan.CreateSyncList`, fired once per peer)
   stamps each redirected ZDO `envelope["recipient"] = RecipientFor(peer)` — **one** SteamID — and
   `SuppressNative` removes it from the native send list.
2. Under `lumberjacks-primary`, native sync is globally suppressed (`coverage_native_only → 0`), so a
   peer gets a ZDO **only** via an envelope stamped for *that* peer.
3. Valheim's own ownership/force-send bookkeeping decides which peer's pass carries a shared building
   ZDO on a given tick. Whoever "wins" gets the stamped copy; the other peer's pass sees it as
   already-synced and does not re-emit. Ownership moving between co-located players (native behaviour)
   flips which partition the ZDO lands in — hence the flicker, and "reappears when they leave."

So **visibility is being expressed by moving delivery and authority.** There is no representation for
"ZDO X is visible to {A, B} at once"; only "X's next copy goes to *one* recipient," with Valheim's
ownership token picking which one. B entering range is silently an ownership steal from A.

Every mature replication system already solved this by separating relevancy from authority (Unreal
Replication Graph's per-connection relevant set with `ROLE_Authority` orthogonal; Unity DOTS-NetCode
ghost relevancy with a separate `GhostOwner`; snapshot-replication PVS enter/leave). We borrow those,
we do not invent.

## Decision

**Represent authority, visibility, delivery, and ack as four independent facts, and split them apart.**

| Concept | Question | Cardinality vs a ZDO | Home |
|---|---|---|---|
| **Authority** | Who may mutate it? | **1** owner | Valheim owner token / single-writer lease |
| **Visibility** | Who should see it now? | **N** observers (AoI-shaped, moves every step) | Per-observer visibility set over a spatial index |
| **Delivery** | Which channel carries a copy? | **N** partitions | `(windowId, recipientId)` queue partitions |
| **Ack** | Who confirmed receipt? | **N** cursors | Per-partition `_pending` / consumer `_seen` |

A scalar cannot hold a set. The four also move on **different clocks** — authority rarely, visibility
constantly — so binding authority to visibility means every footstep threatens a write-authority
change. They must move independently.

**The mechanism:** compute a **per-observer visibility set** from a shared spatial index (a
Replication Graph), and **fan out a read copy** of each AoI-relevant ZDO into *every* in-range
observer's existing partition. Ownership stays single; visibility becomes many; delivery and ack stay
exactly as they are today. This is *evolution*, not a rewrite — the durable queue, WAL, per-recipient
acks, exactly-once, and replay are **fixed infrastructure the new layer sits on top of**, unchanged.

The substrate already exists: `CreateSyncList` already fires per peer, each pass already holds that
peer's `m_refPos` (`PeerReferencePosition`) and identity (`RecipientFor`), and `ZdoBandPolicy.Classify`
already computes per-observer relevancy (ADR 0011). We un-weld the visibility decision from the
single-recipient stamp and the native-kill; we do not add multicast machinery.

### Recommended target (over region-shared partitions)

**Per-observer fan-out + a per-observer last-sent-revision cache (seed once, then deltas) +
snapshot-cache-assisted seeding on enter-relevance.** Region-shared partitions (co-located players
consuming one area log at independent Kafka-style cursors) are held in reserve for a later phase and
adopted **only against measured WAL amplification** — because they would cost the two properties this
design is praised for: **independent per-recipient acks** and **per-observer band-shaping** (a shared
stream cannot serve a near-full and a mid-thinned observer at once).

### Migration — evolutionary, flag-gated, each phase proven by a live human check

- **Phase 0 — Shadow.** Compute the explicit per-observer visibility set and *log* the fan-out that
  would happen; change nothing. Acceptance: the shadow log names the exact ZDOs the starved observer
  was denied, matching the in-game blank set.
- **Phase 1 — Visibility ≠ recipient.** Give each peer's pass its own last-sent-revision cache so B's
  pass emits the building to B regardless of A's pass. Ownership untouched. Likely fixes the symptom
  outright; the keystone experiment (open question 1) resolves here.
- **Phase 2 — Fan-out (keystone).** Enqueue a read copy into every in-range observer's partition from
  the shared index. Cost becomes `observers × changed-area-ZDOs`.
- **Phase 3 — Snapshot seeding.** On enter-relevance (join / approach-from-far), seed the region's
  current full ZDO set (chunked under the `/pending?limit=1024` window), then deltas. Closes ADR 0011
  accepted-risk #1 (far→approach re-sync).
- **Phase 4 — Delta / partition strategy.** Revision deltas against the baseline cache; evaluate
  region-shared partitions *iff* WAL amplification is measured to matter.
- **Phase 5 — Scaling & durable seats.** Durable N-seat leases (replace the ephemeral in-memory
  `SeatCapacity=0`) with per-holder liveness — `ValheimWindowActivityService.Touch` already tracks it;
  `ValidateContext`'s `0..1` clamp lifts to N. Validate cost/memory/WAL at target player count.

## Consequences

- **The queue, acks, and persistence are untouched** (hard constraint honoured). Fan-out enqueues N
  envelopes instead of 1; each partition stays independently replayable. WAL schema may extend to v3
  *additively* — the fsync-before-mutate invariant and replay path are unchanged.
- **The ADR-0011 three-op invariant tightens.** "Every ZDO removed from native send MUST be acked into
  `peer.m_zdos`" now must hold **per observer**; a missed per-observer ack is a duplicate storm on that
  peer. This is the sharpest edge — Phase 1 must prove `duplicates=0`/`rejected=0` under two clients
  before Phase 2.
- **Primary-mode has no fallback.** Every phase runs where a visibility miss is a *blank* building, not
  a stale one. Each phase stays flag-gated; `mirrored` cutover mode is the escape hatch.
- **Cost grows `observers ×`.** Mitigated by the per-observer delta cache (seed once, deltas after);
  measured before adopting region partitions; budgeted at Phase 5.
- **Operational carry-overs:** the seat gate is ephemeral until Phase 5; gateway deploys must build
  locally and ship via `docker save/load` + re-pin (the VM source roots are stale — memory
  `p7-gateway-image-pinned`).

### Open questions (need experiment before committing)

1. **Does ownership need intercepting at all**, or does read-copy fan-out suffice while the server keeps
   the owner token? If clients accept `RPC_ZDOData` read copies without claiming ownership, we never
   touch the token — the cleanest outcome. *(The keystone experiment, Phase 1.)*
2. Fan-out vs region-partition break-even (players/area) — Phase 4 measurement.
3. Seeding volume vs the `/pending?limit=1024` window — almost certainly needs chunked, back-pressured
   seeding.
4. Band interaction under sharing — likely the decisive argument keeping partitions per-observer.
5. Handoff without flicker when authority *does* legitimately move (read-copy-before-ownership-move;
   band-edge hysteresis — the ADR-0010 damping debt).
6. Ack semantics for one logical ZDO across N partitions — confirm exactly-once *per observer* and
   faithful WAL replay after a crash mid-fan-out.

## Related

`fieldlab/FINDINGS-multiplayer-copresence-2026-07-21.md` (the live repro + data);
ADR 0010 (consistency = predictability — why far-drop / band edges are safe);
ADR 0011 (AoI lives on the producer; suppress/ack/emit are three operations — this ADR adds the
fourth axis, delivery-vs-visibility); ADR 0020 (recipient-scoped durable delivery — the queue kept
as substrate); `fieldlab/PINNED-aoi-optimization.md` item 2 (multi-player density, now active here).
Component inventory: `ZdoRedirectRunner` / `RecipientFor` / `SuppressNative` /
`PeerReferencePosition` / `ZdoBandPolicy` (mod); `ValheimZdoRedirectService` (queue+WAL) /
`ValheimTelemetryHeartbeatService` (health gate) / `ValheimHandshakeService` (seat) /
`ValheimWindowActivityService` (liveness) (gateway). Superseded plan framing:
`~/.claude/plans/multi-player-queue-plan.md` (its "finish leases/health" scope was incomplete).
