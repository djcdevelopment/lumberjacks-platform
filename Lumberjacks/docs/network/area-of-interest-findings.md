# Area of interest: what we learned, and what never got built

**Status:** findings record, written 2026-07-21. Not a plan, not a design. This exists because
a lot of measurement happened between March and July 2026 and very little of it changed the
code that would have used it. When we do implement, start here.

Every claim below is marked with its provenance:

- **MEASURED** — a number exists in a committed artifact, with the file named.
- **IMPLEMENTED** — the behaviour is in code today; file:line given.
- **DESIGNED** — written down as intent; never verified against a running system.
- **GONE** — was measured, but the artifact was never committed and cannot be recovered.
- **OPEN** — the record itself says this was never settled.

Nothing here is inferred from what "should" be true. Where the evidence is missing, that is
stated instead of filled in.

---

## 1. The thing to understand first: there are two systems, and they never met

This is the single most important fact in this document, and it is easy to lose. The repo
contains **two independent notions of "what matters most"**, built months apart, measured
separately, and connected to each other by nothing at all.

### System A — Lumberjacks spatial interest management

The simulation's own AoI. Decides which *entity updates* each player receives per tick.

- ADR 0015, **Accepted 2026-03-28** — predates all the priority work by three months.
- `Game.Simulation/World/InterestManager.cs`, `InterestSubscriptionTracker.cs`,
  `ReplicationOptions.cs`.
- Bands by distance: Near every tick, Mid every Nth tick, Far dropped.

### System B — Valheim ZDO priority / importance

The redirect path's ranking. Decides which *Valheim ZDOs* cross the network boundary.

- Built July 2026 in the mod plus gateway.
- `LumberjacksPriorityClassifier.cs` (7 tiers), `ValheimPriorityDeliveryPlanner.cs`,
  `ZdoIntegrationContract.ImportanceAllows`.

### They do not talk to each other

`docs/network/interest-management.md` states it outright — the Gateway consumer

> does **not** currently re-run `InterestManager` tiers over the Valheim ZDO stream.

So System B's tier model, which was measured heavily (§3), never informs System A, which is
the engine that would actually shed load. And System A's radii and cadence know nothing about
Valheim object importance. **The measurement campaign happened on one side of a wall.**

The same document already names the consequence as an unclosed action, and this is the
closest thing to a direct answer to "why did nothing get incorporated":

> The next AoI audit must measure Valheim relevance selection separately from Lumberjacks
> player-tier filtering before making a multi-client scalability claim.

**OPEN.** That audit never happened.

---

## 2. What System A actually does today

**IMPLEMENTED** — read from `InterestManager.cs` directly, not inferred.

| Band | Rule | Default |
|---|---|---|
| Near | every tick | 0–100 units |
| Mid | every `MidTickInterval`-th tick | 100–300 units, interval 4 |
| Far | dropped | beyond 300 units |

Policies: `Tiered` (default), `Radius` (hard cutoff at Near), `Full` (no filtering).
All values come from `ReplicationOptions`, read **once at startup** from configuration —
`Replication:NearRadius`, `:MidRadius`, `:MidTickInterval`, `:Policy`, `:AdaptiveDegrade`,
`:SubscriptionEvents`, `:SubscriptionSampleTicks`.

### Scope limit that is easy to miss

`InterestManager.cs:16-17`:

> Reliable-lane messages (structure placed, entity removed, etc.) always go to the full
> region. This class only filters datagram-lane tick broadcasts.

AoI therefore governs *position/state churn only*. Reliable world mutations are never
filtered. Any future budget work must not assume AoI covers them.

### What it does NOT do — each verified against the source

- **No byte accounting or bandwidth budget.** `FilterForObserver` returns a
  `HashSet<string>` of entity ids. Nothing measures payload size, nothing compares against
  the 3.6 KB/s target from ADR 0015. The budget exists as a design goal with no enforcement
  point in this class.
- **No priority ordering.** The result is an unordered set. If 5,000 entities change inside
  the near band, all 5,000 are included with equal standing. There is no tier, no sort, no
  head-of-line concept. **This is the specific hole System B's tier model would fill.**
- **No hysteresis at band boundaries.** `InterestManager.cs:113` and `:118` are plain `<=`
  comparisons. An entity oscillating around 100.0 units flips between Near and Mid every
  tick, with no dead-band.
- **No adaptive radius.** Radii are fixed at construction. They do not contract as density
  or client count rises.
- **Shedding exists, but it is binary.** `suppressMidBand` (ADR 0011, driven by
  `AdaptiveDegrade.ShouldSuppressMidBand`) turns the entire mid band off on overrun. It is
  a switch, not a budget: there is no partial shed, and nothing chooses *which* mid-band
  entities to keep.

### Cost shape

- `FilterForObserver` is O(observers x changed entities) with a grid distance lookup each,
  and allocates one `HashSet<string>` per observer per tick.
- `InterestSubscription.ComputeSubscriptions` compares every player against every other
  player **in the same region** — O(players squared) per region, run every
  `SubscriptionSampleTicks` (default 20, so about 1 Hz).

Neither has been measured under density. See §5.

---

## 3. What System B actually does, and what it proved

Seven tiers, rank 0 highest (`LumberjacksPriorityClassifier.cs`):

| Rank | Tier | Qualifies |
|---|---|---|
| 0 | `player_critical` | players, beds, wards, hearths, fires |
| 1 | `portal` | portals, teleporters |
| 2 | `structural_anchor` | poles, beams, walls, floors, foundations |
| 3 | `near_interactive` | doors, gates, signs, chairs, ships |
| 4 | `storage_crafting` | chests, forges, workbenches, smelters |
| 5 | `support_piece` | default for any other loaded piece |
| 6 | `decorative_far` | rugs, banners, decor — **only past 66% of scan radius** |

Ranking sorts by rank, then horizontal distance, then name. Distance is a **tie-breaker
inside a tier**, not a tier determinant — except the 0.66 radius rule that demotes decor.

### Enforced versus advisory — get this right

Two different things use this tier model, and only one of them enforces:

- **ENFORCED.** `ZdoRedirectRunner.cs:337` calls
  `ZdoIntegrationContract.ImportanceAllows(candidate.PriorityRank, zdoRedirectMaxPriorityRank)`,
  which is `rank >= 0 && rank <= max`. A ZDO whose rank exceeds the configured maximum
  **does not cross the boundary**. This is a live admission filter on real traffic.
- **ADVISORY.** `ValheimPriorityDeliveryPlanner` sorts objects into reliable / datagram /
  deferred buckets against caller-supplied budgets. That plan is broadcast as a manifest.
  It reorders nothing in Valheim's own replication. The probe runner says so in a string
  it stamps into its own telemetry: it *"does not write ZDOs, change ZNetView ownership,
  correct transforms, or replace vanilla replication."*

So: rank gates admission, the plan is metadata. Do not describe System B as "priority
replication" without that qualifier.

### MEASURED — the P7 gold run, 2026-07-16

Single enrolled client, single window, `lumberjacks-primary`
(`fieldlab/evidence/p7-gold-run-20260716-011112-authoritative-priority-cutover/report.md`):

| Metric | Value |
|---|---|
| Receipts / acknowledged | 83,220 / 83,220 |
| Pending, rejects, duplicates, retries, ack failures | 0 across the board |
| Coverage / native-only | 100% / 0 |
| Priority tagged | 83,220 (100%) |
| **Fast lane** | **47,534 (57.1%)** |
| Applied + superseded | 72,946 + 10,274 |
| Max client queue | 960 of a 1,024 poll |
| First peer to first apply / to complete | 6.72 s / 102.11 s |
| Acceptance sample | 121.2 FPS, p95 8.5 ms |
| WAL compaction | 168,987,408 → 256,244 bytes (99.848%) |

**The 57.1% is the number to carry forward.** Under a real Era16 world, well over half of
redirected ZDO traffic classified into the fast lane. That is a strong signal that tiering
is worth enforcing — and it is precisely the finding that System A never received.

**Scope, stated in the artifact itself:** single client only. It "does not establish
replacement of Steam login, Valheim simulation, native candidate relevance selection,
non-ZDO RPCs, or recipient isolation under multiple clients."

---

## 4. MEASURED — host capacity, 2026-07-12

`docs/benchmark-host-capacity-2026-07-12.md`. Same gateway image on three hosts, bots at
20 Hz, `entity_update` delivered / avg RTT / peak gateway CPU:

| Bots | Cloud (8 vCPU) | AM4 (12C/24T) | OMEN (24C) |
|---|---|---|---|
| 50 | 158,774 / 22 ms / ~0.22 core | 164,109 / 45 ms / ~0.21 core | 149,967 / 30 ms / ~0.22 core |
| 100 | 568,503 / 31 ms / ~0.5 core | 561,535 / 49 ms / ~0.62 core | 539,369 / 35 ms / ~0.44 core |
| 200 | 2,033,685 / **165 ms** / ~3.0 core | 2,078,649 / **78 ms** / ~3.3 core | 1,910,301 / **58 ms** / ~0.85 core |

Follow-up B puts the true knee at **400 bots** on cloud and AM4 alike. The document's own
reading: the workload is featherweight — roughly 1–3 cores and ~150 MiB RSS at 200 bots —
and what bends first is **message volume**, not CPU.

Also **MEASURED** (`docs/network/validation.md`): 50 clients for 30 s produced 152,118 UDP
entity updates with zero errors locally; on Azure Container Apps UDP was blocked and the
WebSocket fallback carried it, also with zero errors.

**Why this matters for AoI:** the bottleneck these runs found is update volume. AoI is the
lever that reduces update volume. The capacity work therefore measured the problem AoI
exists to solve — and then nothing tightened AoI in response.

---

## 5. The methodology trap — the most reusable thing we learned

`docs/network/interest-subscription-events-testing.md`, and it will waste a day if
forgotten:

> Stationary bots never cross a tier boundary. A subscription only changes when a player
> moves... The default load-test bots random-walk in a tight cluster and rarely cross 300u,
> so you get almost nothing.

A naive load test against interest management **measures nothing**, because subscription
churn is the thing under test and clustered bots never generate it. `BOT_WANDER=1` exists to
force real traversal.

Generalised: *the interesting behaviour is at the boundary, so the test has to cross it.*
Any future AoI measurement must prove its bots actually traverse bands before any number it
produces means anything.

---

## 6. GONE — the density-pressure matrix

Between 2026-07-04 and 07-07 a swarm-driven collector was built for an
"era16-density-pressure-matrix": each client checked out a cell (a density band coordinate,
an **observer range offset**, an event profile, a benchmark window), ran it, and reported.
Observer range offset is the AoI knob; this was an AoI tuning campaign.

**Its data is not recoverable:**

- `modeled-pressure-matrix.csv` (the modeled baseline) — never tracked in git.
- `results.jsonl` (the collected reports) — never tracked in git.
- `plan.json` (run state) — absent; `network/mcp/var/matrix/` does not exist.

The client half was deleted 2026-07-21 (`SWARM-HARNESS-REMOVED.md`); the server half is
`network/mcp/comfy_gateway/toolsurface/matrix.py`. **Deleting the code lost nothing that the
missing data had not already lost.** But whatever that campaign found about observer range
versus density is gone and would have to be re-collected.

This is the concrete cost of writing results to a gitignored var directory. If we re-run it,
**commit the results.**

---

## 7. OPEN — six experiments defined, none answered

`network/observability-and-experiments.md` defines six experiments. Searching every
committed artifact, **not one has a recorded answer.** Two bear directly on AoI:

1. *Baseline solo field work* — "how much does decorative density affect client and server
   pressure?" and "which signals shift even before the player feels lag?"
2. *Low impact mode trial* — "what can be reduced without harming trust?" and "does the
   player actually notice the trade?"

The remaining four cover convergence warning, pre-combat staging, owner-swap sensitivity and
benchmark usefulness. All **OPEN**.

The instrumentation to answer them is fully specified in `network/telemetry-and-scores.md` —
including `region_pressure_score`, `messages_by_priority`, `bytes_by_priority`,
`deferred_low_priority_count`, `dropped_low_priority_count`. **Note those last four field
names: the telemetry schema already anticipates priority-aware shedding that the code does
not do.** The design intent survived; the implementation never caught up.

---

## 8. So what should actually change when we implement

Derived only from gaps evidenced above.

1. **Join the two systems.** Give `InterestManager` a notion of per-entity importance and
   feed System B's tier model into it. Today the fast/slow split is decided on the Valheim
   side and thrown away before the engine that sheds load ever sees it. The 57.1% figure is
   the argument that this is worth doing.
2. **Add a budget, because there isn't one.** ADR 0015's 3.6 KB/s is a goal with no
   enforcement point. A budget needs byte accounting in the filter path, which means
   `FilterForObserver` has to stop returning a bare id set.
3. **Order within the near band.** Currently everything inside 100 units is equal. Under
   density that is exactly where the pressure is, and rank 0 (`player_critical`) is
   indistinguishable from rank 5 (`support_piece`).
4. **Make shedding graded, not binary.** `suppressMidBand` is all-or-nothing. With a tier
   model plus a budget, shed by rank instead.
5. **Add hysteresis.** Plain `<=` at both boundaries means oscillating entities thrash.
   Cheap to fix, and it will show up as subscription-event noise before it shows up as a
   performance problem.
6. **Fix the O(players squared) subscription scan** before any many-player claim. It is per
   region, per sample tick.
7. **Instrument first.** `InterestManager` emits no counters and no metrics — nothing
   measures filtered-vs-sent, band populations, or boundary crossings. Any tuning campaign
   without this is blind, which is part of how the last one produced nothing durable.
8. **Test under density.** Existing tests pin set membership and fallback correctness only.
   There is no test with many entities, many clients, or boundary churn.
9. **Re-run the density campaign, and commit the results this time.**

## 9. Provenance notes

Assembled from four parallel gemini-pro passes over the design docs, the implementation, the
Valheim priority path, and the evidence record. **Every load-bearing claim was re-checked
against source before being written here**, which caught four errors worth recording:

- One pass reported `LumberjacksPriorityManifestListener.cs` and
  `ValheimPriorityManifestEndpoints.cs` as "entirely missing". Both exist; they simply were
  not in that pass's packed file set. Absence from a context window is not absence from the
  repo.
- It also concluded the priority system was purely advisory. Half right — the delivery
  *plan* is advisory, but the *rank* is enforced at `ZdoRedirectRunner.cs:337`.
- Another pass reported `load-test-dual-channel-results.md` and
  `benchmark-host-capacity-2026-07-12.md` as referenced-but-missing. Both exist; the second
  is at `docs/`, not `docs/network/`. Had that gone unchecked, this document would have
  claimed the capacity evidence was lost when it is the strongest measured artifact we have.
