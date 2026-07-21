# Landmark reach: making distance a scarce, earned property

**Status:** design note, recorded 2026-07-21 from Derek. Not implemented; not scheduled. Captured
because it answers the requirement in
[`area-of-interest-findings.md` §0](area-of-interest-findings.md) and because the machinery it needs
turns out to be almost entirely present already.

## The idea

> "Every limitation is just an opportunity to craft a community that is held together by tangible
> limitations — we make them visible. We don't over-build the distance, but think of it as a reward a
> master builder can place, a quest that lets you earn pieces that are invisible up close but
> structure at great distance."

The inversion matters. The obvious approach to "I want a lighthouse visible from the water" is to
make area-of-interest clever enough to render more things further away — which is unbounded, expensive,
and degrades exactly when the world is busiest.

This proposes the opposite: **long-range presence is a scarce property that must be granted**, to
specific objects, in specific places, by someone who earned it. The cost ceiling is then a design
parameter rather than an emergent property of how much people happened to build. A limitation stops
being something the engine hides and becomes something the community can see, want, and work toward.

It is also an **inverted level of detail**. Normal LOD shows detail up close and simplifies with
distance. A landmark piece is *invisible up close* — you are standing in the real build, which is
loaded — and becomes structure only at range, where a single cheap stand-in represents the whole
silhouette.

## The scoping primitive

Mark a thing, define its reach. Derek identified three selectors, and they are the same mechanism:

| Selector | What it marks | Already expressible as |
|---|---|---|
| **Area + reach** | a region of the world | `ValheimPriorityObject.Position` (absolute `Vec3`) + a new reach field |
| **`ZDOid == guid`** | one specific placed object | `StableKey` — already the planner's dedup and ordering key |
| **`itemid == xxx`** | a class of pieces | prefab allowlist — already implemented three times over `BuildPrefabFilter`, matched by stable hash |

## What already exists

More than expected. None of this needs building:

- **Identity and position.** `ValheimPriorityObject` carries `StableKey`, `ObjectName`, `ObjectKind`,
  `PriorityTier`, `PriorityRank`, `PriorityOrder` and an absolute `Position`.
- **A tier model that already ranks landmarks correctly.** `LumberjacksPriorityClassifier` puts
  `structural_anchor` at rank **2**, above `near_interactive`, `storage_crafting`, `support_piece` and
  `decorative_far`. A tower already outranks a rug; nothing downstream can act on that past
  `MidRadius`.
- **A manifest with a delivery wire.** `ValheimPriorityManifestService` builds and activates plans;
  `POST /valheim/priority-manifests/{manifestId}/broadcast` sends them; the mod's
  `LumberjacksPriorityManifestListener` consumes them and tracks `manifest_id`. The channel for
  "here is the set of things that matter at range" is already live.
- **Lane separation and budgets.** `ValheimPriorityDeliveryPlanner` already sorts into
  reliable / datagram / deferred against caller-supplied budgets, with `ReliableTiers` naming the four
  tiers that get the reliable lane.
- **Prefab allowlists with a deliberate fail-closed default.** `ZdoRedirectPrefabs` refuses to arm on
  an empty list precisely because suppressing everything would freeze world sync — the same care a
  landmark allowlist wants.

## What is actually missing

Four things, in increasing order of difficulty.

### 1. A reach field — the small one

`DistanceMeters` on the delivery records means *how far the observer was when this was sampled*. There
is no field meaning *how far away this should still be present*. Position + reach is the whole
primitive, and everything else in the record already exists.

### 2. Enforcement — the plan is advisory

Established in the 2026-07-21 audit: the delivery **plan** is broadcast metadata. It reorders nothing
in Valheim's own replication. The probe's own scope claim says it *"does not write ZDOs, change
ZNetView ownership, correct transforms, or replace vanilla replication."*

Note the asymmetry — the *rank* IS enforced, at `ZdoRedirectRunner.cs:337` via
`ZdoIntegrationContract.ImportanceAllows`, which drops ZDOs above a configured maximum rank. So there
is a live enforcement point that already consults rank. A landmark exemption is plausibly a change to
that predicate rather than a new subsystem: *admit if rank allows, **or** if this object is a landmark
within its reach of the observer.*

### 3. The proxy asset and the swap rule — the content problem

"Invisible up close but structure at great distance" needs an actual far-field stand-in to exist, and
a rule deciding which of the two you see. Two failure modes to design against:

- **Double render** — proxy and real build both visible during the handover.
- **Pop** — the swap happening somewhere the player is looking.

This is authoring and client-side presentation, not networking, and it is the part with no existing
machinery at all.

### 4. The earning mechanic — game design

Quest, reward, and whatever governs who may place a landmark and how many. Out of scope for this
document, but it is the thing that keeps the cost ceiling a design parameter. Without scarcity the
whole argument collapses back into "render more at distance".

## Why this is the affordable shape

The §0 requirement was a lighthouse visible from the water. What makes it tractable is that a
lighthouse **needs no updates** — it does not move. So it is not a datagram-filtering problem at all,
and none of the per-tick cost analysed in `area-of-interest-findings.md` §2 applies to it. It is a
load-order and admission question: does this object reach my client at all, and how early.

That is why it can be solved without making `InterestManager` more expensive, and why it should be
kept separate from the tick-budget work the
[knee experiment](aoi-knee-experiment-brief.md) is measuring. Two different scenarios from §0, two
different systems:

| §0 scenario | System | Question |
|---|---|---|
| Visiting heavy builds; the lighthouse | ZDO priority / admission | *does it arrive, and in what order* |
| Multi-person combat; skirting the coast | Interest manager / tick budget | *how much per-tick churn can we afford* |

## Open questions worth settling before building

- **Does reach interact with the tick budget at all?** If landmark objects are static and reliable-lane,
  they should cost once at arrival and never again. Confirm that before assuming it is free — a
  landmark that re-broadcasts is a landmark that scales badly.
- **What bounds the total?** Per-player, per-region, per-world? The scarcity mechanic has to produce a
  number the engine can rely on.
- **What happens when a landmark's real build is deleted or changes shape?** The proxy is a promise
  about something that may no longer be there.
- ~~**Does the existing `far_suppressed` interest bucket need a fourth state**, or is landmark reach a
  parallel path that never consults interest buckets at all?~~ **Settled 2026-07-21: parallel path.**
  Not merely simpler — *necessary*, for a reason that only appears once the near radius is cut
  aggressively. See below.

## The discovery problem, and why the channel must be parallel

Cutting the interest radius hard (see the three-tier sweep in
[`aoi-knee-experiment-brief.md`](aoi-knee-experiment-brief.md)) creates an obvious hole: **if
everything past the zone boundary is dropped, how does a client ever learn a landmark exists at
500 m?** The announcement would have to travel the same path that was just severed.

The tempting answer is to widen the radius back out so clients can "listen" for distant great works.
That gives back precisely the saving the cut just bought, and it scales with distance — the thing we
were trying to stop paying for.

**The right answer is that landmarks were never on that path.** The priority manifest is *broadcast*,
not interest-filtered: `POST /valheim/priority-manifests/{manifestId}/broadcast` on the gateway,
`LumberjacksPriorityManifestListener` on the mod. `InterestManager` never sees it. So the client can
be standing in a 30 m bubble and still receive *"structural_anchor at (x,z), reach 1500 m"*.

What arrives is an **announcement, not a stream**: identity, position, tier, reach. The client then
spawns the far-field proxy locally. The real build is never replicated at range — which is the whole
reason the proxy exists.

This keeps the two costs independent, and that independence is the load-bearing property:

| cost | bounded by |
|---|---|
| per-tick churn | the interest radius |
| landmark discovery | **how many great works exist** — not how far away they are |

An aggressive near cut is therefore affordable *because* discovery is a separate, sparse, distance-free
channel. Fold the two together and the cut defeats itself.
