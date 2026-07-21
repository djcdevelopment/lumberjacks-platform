# Session brief: find the AoI capacity frontier

**Paste this file's path as the opening prompt of a fresh session.** It is written to be
self-contained: it assumes no memory of the session that produced it.

---

## What this is for (read before the question)

Three player-facing limitations motivate all of it, and the knee number is what makes each of them
decidable rather than arguable. See §0 of [`area-of-interest-findings.md`](area-of-interest-findings.md)
for the full framing:

1. **Multi-person combat and events** — several players, things happening, filtering cost and update
   volume peaking together.
2. **Skirting the coast into the unknown** — moving fast enough that the world cannot keep up.
   *"Lag into the coast, get stuck, jumped and die."* The failure mode has a dead character in it,
   not just a stutter.
3. **Visiting heavy builds** — the best things the community makes are the hardest to share, because
   load time makes inviting people impractical.

The sharpest single requirement: **a lighthouse on the coast, visible at distance.** It needs almost
no updates, so it is not a datagram-filtering problem — it is a load-order and priority problem, and
the current architecture cannot express "this object matters at range" because rank and distance
never meet. If a design makes the lighthouse possible without breaking the tick budget, it is
probably the right design.

Note also that the prior campaign's `priority_expectation` column already specifies a **five-level
graded shedding ladder**, and its `process_budget` column is already three-state
(`green_under_tick` / `yellow_near_tick` / `red_over_budget`) with 1,920 rows predicting yellow or
red. The parameter space was designed deliberately; it was never measured.

## The question

**For a given observer range and a given object count, where does the tick loop stop keeping
up?**

Not throughput. Not average RTT. The **knee** — the frontier in the (range x objects) plane
where the server crosses from comfortable to overrunning its tick budget.

### Define the knee by variance onset, not failure onset

The target here is **consistent fidelity**, not peak capacity. That distinction changes where the
knee actually is, and it is easy to get wrong.

`game.tick.overruns` marks where the budget is *breached* — but by then the experience has already
degraded, because a frame that is merely *late sometimes* feels worse than one that is uniformly
slower. The interesting frontier is earlier: **where p99 starts pulling away from p50.** That is
the point at which the system stops being predictable, which is what a player perceives long before
anything exceeds 50 ms.

So record both, and report them as two separate curves:

| frontier | signal | meaning |
|---|---|---|
| **variance onset** | p99 / p50 ratio for the `interest` phase and for total tick | it stopped being *consistent* — the knee that matters |
| **failure onset** | first non-zero `game.tick.overruns` | it stopped *keeping up* — the hard ceiling |

Expect the first to arrive meaningfully before the second. If it does not, that is itself worth
knowing. `TickMetrics` already keeps p50/p99/max per phase over a rolling ~100-tick window, so both
come out of the same `/tick` read at no extra cost.

This is not a stylistic preference. The whole telemetry schema in `network/telemetry-and-scores.md`
is variance-oriented — `jitter_ms` beside `rtt_ms`, `p95_frame_time_ms` beside `avg_fps`,
`correction_count_recent`, `correction_magnitude_avg`, `time_since_last_authoritative_update_ms`.
Measuring this system by averages would contradict the thing it was instrumented to care about.

The output should be a surface, or at minimum a curve per radius: *at NearRadius = R, the
knee is at N objects.* That number is what makes every downstream AoI decision arguable
instead of guessed.

## Why this and not the obvious alternative

An earlier framing swept bot counts and read delivered-update totals and RTT. That measures
transport capacity, which is already known: `docs/benchmark-host-capacity-2026-07-12.md`
found the workload featherweight (~1–3 cores at 200 bots) with the knee at ~400 bots, and
established that **message volume bends before CPU does**. Re-running that answers a
question already answered.

The unanswered question is what AoI actually controls: the cost of *deciding* what to send,
which scales as observers x changed-entities, and which the existing benchmark never varied
because it never touched the radii.

## Read these first, in this order

1. [`area-of-interest-findings.md`](area-of-interest-findings.md) — the full findings record.
   Sections 1, 2 and 7 are the load-bearing ones. **Section 1 is essential**: there are two
   independent "what matters most" systems in this repo and conflating them will waste hours.
2. [`interest-management.md`](interest-management.md) — the design as documented.
3. [`interest-subscription-events-testing.md`](interest-subscription-events-testing.md) — the
   methodology trap. Non-negotiable reading; see Gotcha 1 below.
4. `fieldlab/evidence/aoi-density-pressure-matrix-20260704/README.md` — the prior campaign,
   its 9,600-row model, and exactly why it produced no usable measurement.

## What already exists — do not rebuild any of this

**The knee detector is already in the code.** This is the key fact and it was nearly missed:

| What | Where | Why it matters |
|---|---|---|
| `TickBudgetMs = 50.0` | `Game.Simulation/Tick/TickMetrics.cs:31` | The budget a knee is defined against |
| `game.tick.overruns` counter | `TickMetrics.cs:205` | Ticks exceeding budget — **this is the knee signal** |
| Per-phase duration histogram | `TickMetrics.cs:191` | Separates `interest` cost from `send` cost |
| `RecordReplication(sent, culled)` | `Game.Gateway/WebSocket/TickBroadcaster.cs:342` | Filtered-vs-sent, already counted |
| `RecordDegraded(bool)` | `TickMetrics.cs:147` | Whether adaptive degrade fired |
| Rolling window p50/p99/max | `TickMetrics.cs` | ~100 ticks, ~5 s at 20 Hz |
| HTTP exposure | `/tick`, and `TelemetryV0Endpoints.BuildTickInfo` | Read the whole snapshot without a profiler |

**The load driver already exists:** `Lumberjacks/scripts/load-test-dual-channel.js` — spawns
N bots that connect over binary WebSocket, bind UDP, and send `player_input` at 20 Hz.
Unattended, free, repeatable. `npm run test:load:50` / `:100` exist as presets.

**The tuning surface is already configurable** (`Game.Simulation/World/ReplicationOptions.cs`,
read once at startup from `IConfiguration`):

`Replication:NearRadius` (100) · `Replication:MidRadius` (300) ·
`Replication:MidTickInterval` (4) · `Replication:Policy` (Tiered|Radius|Full) ·
`Replication:AdaptiveDegrade` (false) · `Replication:SubscriptionEvents` (false) ·
`Replication:SubscriptionSampleTicks` (20)

So a run is: set env config → restart gateway → spawn bots → `GET /tick` → record. Fully
scriptable, no human in the loop.

## The experiment

Sweep two axes and find where `overruns` first goes non-zero:

- **Range:** `Replication:NearRadius` across a meaningful spread. `MidRadius` either held at
  a fixed multiple or pinned, but say which — the mid band only costs on burst ticks
  (`tick % MidTickInterval == 0`), so it is a different cost shape.
- **Objects:** entity count in the region. Note the cost driver is *changed* entities per
  tick, not total — a large static world is cheap. Whatever you use to scale objects, report
  changed-entities-per-tick alongside it or the numbers will not be comparable.

At each point record, from `/tick`: `overruns`, per-phase `interest` p50/p99/max, total p99,
`sent`, `culled`, `degraded`.

**Set `Replication:AdaptiveDegrade=false` for the measurement runs.** It exists to hide
exactly the overrun you are trying to find; leaving it on means the system sheds the mid band
and the knee never appears. Turn it on afterward to measure how much headroom it buys — that
is a second, separate result.

## Four gotchas that have already cost time

1. **Stationary bots measure nothing.** From `interest-subscription-events-testing.md`:
   *"Stationary bots never cross a tier boundary... The default load-test bots random-walk in
   a tight cluster and rarely cross 300u, so you get almost nothing."* Use `BOT_WANDER=1`.
   **Prove traversal before trusting any number** — if subscription events are flat, the run
   is void.

2. **A disconnected client reports zeros, and zeros look like data.** The 2026-07-04 campaign
   produced exactly one real sample, and its `rtt_ms`, `bytes_in_per_sec`, `bytes_out_per_sec`
   and `packets_*` were all `0` because the client sat in Solo mode. Assert connectivity at
   capture time and reject any sample with zero traffic — do not discover it in analysis.

3. **Its 998 other rows were synthetic and flat.** They report `avg_fps` of exactly `60.0`
   and `bytes_out_per_sec` of ~18,000 regardless of density band *or* observer range. They
   are pipeline stubs, not measurements — do not treat that file as a baseline.

4. **AoI only filters the datagram lane.** `InterestManager.cs:16-17`: reliable-lane messages
   always go to the whole region. Do not attribute reliable-lane cost to AoI.

## What would make this genuinely finished

- A knee value per radius, with the changed-entities-per-tick that produced it, on named
  hardware. Three hosts already have capacity baselines in
  `benchmark-host-capacity-2026-07-12.md` if a comparison is wanted.
- The `interest` phase's share of tick time at the knee — this settles whether the filter
  itself is the cost or merely correlated with it. `FilterForObserver` allocates a
  `HashSet<string>` per observer per tick, so allocation may dominate; the per-phase
  histogram will show it.
- **A first validation of the 9,600-row model.** `aoi-density-pressure-matrix-20260704/`
  predicts `estimated_datagram_updates_per_sec`, `estimated_udp_kbps` and a
  `process_budget` classification (`green_under_tick`) across the whole parameter space, and
  **not one row has ever been checked against an observation.** Pick cells to *falsify* it,
  not confirm it. If the model holds even roughly, it becomes a cheap oracle and the
  remaining 94 unrun cells stop mattering.
- Results committed. The prior campaign's artifacts spent three months in a gitignored `var/`
  directory in a since-retired repo.

## Sweep this shape first — it is three config values, not a code change

Derek's proposal, 2026-07-21, refined against what the model already predicts. **Run this before
the general sweep**; if it holds it is most of the win, and it costs nothing to try.

**Where the cost actually is.** In the recovered model at `extreme` density / `combat_build`, the
bands are wildly asymmetric:

| band | modeled distance | bucket | worst case |
|---|---|---|---|
| self | 0 m | `near_20hz` | 2000 updates/s · 1536 kbps |
| near | 50 m | `near_20hz` | 2000 updates/s · 1536 kbps |
| mid | 200 m | `mid_5hz` | 5–500 updates/s · 3.84–384 kbps |
| far | 500 m | `far_suppressed` | **0.00 · 0.00** |

Two consequences. **Far is already free** — cutting harder at distance buys nothing, it already
sends zero. And the 50 m band costs the same as standing still, so **the entire budget lives in
`near_20hz`, 0–50 m at 20 Hz.** Since area goes as r², pulling the full-rate radius from 50 m to
~30 m removes roughly 71% of the objects in the only expensive band. That is the largest single
lever in the model and it has never been pulled.

**Cut the rate, not the object.** Valheim activates and renders by **zone, and its zone size is
64 m** — the mod's own `NearbyRadiusMeters` and `BuildScanRadiusMeters` both default to 64 for that
reason, and the code uses `ZoneSystem.GetZone` / `IsZoneLoaded` throughout. An object 40 m away is
therefore in the same loaded, active zone as the player. If replication *drops* it at 30 m it stays
visible and interactable while its state goes stale — present but wrong, a desync between two
systems' notions of "nearby".

So thin instead of drop, which is what the model's own `thin_datagrams_to_5hz_defer_detail` already
describes — just applied at 200 m today instead of 30 m. Bandwidth is rate x count; 20 Hz → 5 Hz
past 30 m takes most of the saving with none of the staleness risk.

**The shape to test:**

| zone | radius | rate | rationale |
|---|---|---|---|
| full | 0 – ~30 m | 20 Hz | where the player actually interacts |
| thinned | ~30 m – 64 m | 5 Hz (or lower) | still inside Valheim's active zone, so it must stay coherent |
| dropped | beyond 64 m | none | Valheim's own zone boundary; both systems agree nothing is needed |
| landmark | by grant, any range | announced, not streamed | **a separate channel, not subject to the cut at all** — see below |

**The cut creates a discovery problem, and the answer is not to un-cut it.** If everything past the
zone boundary is dropped, a client can never learn that a landmark exists at 500 m — the announcement
would travel on the path that was just severed. The temptation is to widen the radius back out to
"listen" for distant great works, which gives back exactly the saving the cut just bought.

Don't — because **the dual-channel transport already solved this.** `InterestManager`'s own header:
*"Reliable-lane messages (structure placed, entity removed, etc.) always go to the full region. This
class only filters datagram-lane tick broadcasts."* The reliable lane is region-wide and never
consults an interest radius, and `ValheimPriorityDeliveryPlanner.ReliableTiers` already routes
`structural_anchor` — the lighthouse tier — onto it.

The lane split is semantic: reliable carries *"this exists / this changed"* (rare, region-wide),
datagram carries *"where it is right now"* (every tick, filtered). **A static landmark is pure
reliable-lane traffic** — one message when placed, zero datagrams forever, because it does not move.
Its cost is a function of how often it changes, not of how far away it is.

So the aggressive datagram cut costs landmarks **nothing**; they were never datagram traffic. The
client hears *"structural_anchor at (x,z), reach 1500 m"* and spawns the proxy locally.

That keeps the two costs independent: the interest radius bounds per-tick churn, and the landmark
count bounds announcements. Discovery cost scales with **how many great works exist**, not with how
far away they are — which is what makes an aggressive near cut affordable rather than self-defeating.

Concretely: `Replication:NearRadius` ~30, `Replication:MidRadius` ~64, and `MidTickInterval` tuned
for the thinned rate. All three are startup config in `ReplicationOptions`, so a run is a config
change plus a restart.

**What would falsify it:** the knee does not move when `NearRadius` drops (meaning the cost is not
where the model says), or objects between 30 m and 64 m visibly stale or pop despite being thinned
rather than dropped (meaning 5 Hz is not enough inside an active zone). Watch `culled` versus `sent`
from `/tick` — the ratio should shift sharply and the `interest` phase p99 should fall.

## One likely finding worth pre-registering

`InterestSubscription.ComputeSubscriptions` compares every player against every other player
in the same region — O(players squared), every `SubscriptionSampleTicks` (default 20, ~1 Hz).
With `SubscriptionEvents=false` (the default) this path is skipped entirely.

So: **run the sweep with subscription events off, then repeat one column with them on.** If
the knee moves sharply, the quadratic scan is a real ceiling and that is a finding in its own
right. If it does not move, that is equally worth knowing and stops it being a worry.

## Scope discipline

This is a **measurement** session. Do not implement the fixes in
`area-of-interest-findings.md` §8 — budgets, tier ordering, hysteresis — during it. The whole
reason that list exists unbuilt is that prior campaigns produced no numbers to justify a
specific design. Get the frontier, then design against it.
