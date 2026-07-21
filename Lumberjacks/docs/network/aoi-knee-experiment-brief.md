# Session brief: find the AoI capacity frontier

**Paste this file's path as the opening prompt of a fresh session.** It is written to be
self-contained: it assumes no memory of the session that produced it.

---

## The question

**For a given observer range and a given object count, where does the tick loop stop keeping
up?**

Not throughput. Not average RTT. The **knee** — the frontier in the (range x objects) plane
where the server crosses from comfortable to overrunning its tick budget.

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
