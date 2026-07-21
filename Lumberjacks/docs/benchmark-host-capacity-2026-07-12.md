# Host Capacity Benchmark — Cloud vs Local (2026-07-12)

Status: **Observed** (measured this date; updated same date with Follow-up A — the
GPU-contention probe — and Follow-up B — the off-host clean re-runs. Both former
next-steps are done).

## Question

This is the community-compute build-order **step 5**: can local hardware host the
Lumberjacks authoritative workload as well as a rented cloud VM, and at what cost? The
answer decides whether "community-assisted hosting on volunteer nodes" rests on real
economics or wishful thinking.

## Method

- **System under test:** a bare `Game.Gateway`, run **DB-less** (it falls back to the
  in-memory `region-spawn` default when PostgreSQL is absent). The **exact same image
  binary** (`comfy-lumberjacks-p7-gateway:latest`) ran on all three hosts — built once,
  transferred by `docker save`/`docker load`, so no build-variance between hosts.
- **Driver:** [`scripts/load-test-dual-channel.js`](../scripts/load-test-dual-channel.js)
  — spawns N bots that each connect over binary WebSocket, bind a UDP channel, and send
  `player_input` at 20 Hz, receiving `entity_update` over both lanes. Run **co-located**
  on each host (patched so its UDP port is configurable).
- **Sweep:** 50 → 100 → 200 bots, 30 s per level.
- **Captured:** UDP `entity_update` delivered, average round-trip, peak gateway CPU and
  RSS (`docker stats`), and errors/disconnects.
- **Networking caveat:** cloud and AM4 drove the gateway over `--network host` + published
  ports; OMEN (Docker Desktop) used a user-defined bridge with container DNS, because
  Docker Desktop's host-networking/localhost-UDP path is unreliable. This makes OMEN's
  absolute CPU **not** perfectly comparable (see Caveats).

## Hosts

| Host | CPU | RAM | Role | Cost |
|---|---|---|---|---|
| Cloud | GCP `n2-highmem-8`, 8 vCPU | 64 GB | rented experiment VM (us-west1) | **$0.52/hr** list (~$380/mo), trial credits |
| AM4 | Ryzen 9 5900X, 12C/24T | 30 GB | dedicated-ish local worker | **sunk** (power) |
| OMEN | Core Ultra 9 285K, 24C | 128 GB | fleet **coordinator** (Ollama + HEARTH + Hyper-V host); measured via WSL2/Docker Desktop | **sunk** (power) |

## Results

Every level completed with **0 errors** on every host. Throughput is ~equal across hosts
because the load is driver-paced (20 Hz) and all three sustained the full offered load
with no drops. `entity_update` delivered / avg RTT / peak gateway CPU:

| Bots | ☁️ Cloud (8 vCPU) | 🖥️ AM4 (12C/24T) | ⚡ OMEN (24C) |
|---|---|---|---|
| 50 | 158,774 / 22 ms / ~0.22 core | 164,109 / 45 ms / ~0.21 core | 149,967 / 30 ms / ~0.22 core |
| 100 | 568,503 / 31 ms / ~0.5 core | 561,535 / 49 ms / ~0.62 core | 539,369 / 35 ms / ~0.44 core |
| 200 | 2,033,685 / **165 ms** / ~3.0 core (**38% of 8**) | 2,078,649 / **78 ms** / ~3.3 core (**14% of 24**) | 1,910,301 / **58 ms** / ~0.85 core (**3.5% of 24**) |

Gateway RSS stayed **~120–160 MiB** on every host at every level.

## Reading

- **Headroom / latency: OMEN ≥ AM4 > cloud.** The 8-vCPU cloud box is the only host that
  strains at 200 bots (165 ms RTT), because its cores get thread-starved by the
  co-located driver competing with the server and the O(N²) broadcast fan-out.
- **Cost-per-capacity is not close.** Both local boxes carry the full load at sunk cost;
  the cloud VM bills $0.52/hr for the least headroom.
- **The workload is featherweight.** ~1–3 cores and ~150 MiB RSS at 200 bots. Raw capacity
  is not the constraint on any of these hosts.

**Verdict:** the community-compute thesis is strongly supported. Local hardware is more
than adequate for authoritative hosting; the cloud's genuine advantages are elsewhere
(availability, geo-distribution, egress, managed ops) — **not** raw capacity.

## Caveats (do not over-read)

1. **Co-located driver** caps the true server knee on all three hosts — the driver and
   server share CPU, so absolute "max bots" is understated (most visibly on the 8-vCPU
   cloud box). A clean number needs an **off-host driver**. **Resolved same-date** by
   Follow-up B: the true knee is 400 bots on cloud and AM4 alike, and at 200 bots the
   cloud RTT drops from 165 ms (co-located) to 75 ms (off-host).
2. **OMEN's CPU is not apples-to-apples.** Its efficient kernel-bridge networking (vs
   host-net + docker-proxy on cloud/AM4) means part of its ~0.85-core-vs-~3-core advantage
   is a networking-method artifact, not pure CPU. The qualitative result (most headroom)
   is solid; a literal ~3× CPU win is not claimed.
3. **DB-less, in-memory.** The EventLog/persistence path was not exercised.
4. **Single region, effectively-full interest** → O(N²) broadcast fan-out is the real
   scaling wall (159k → 569k → 2.03M updates as bots doubled), not CPU. **Correction
   (post-recon, same date):** the spatial interest tiers were *not* "disengaged" — they
   are hardcoded always-on (`InterestManager`: near 100 u every tick, mid 300 u every
   4th tick, far dropped; no config switch exists) and were active in every run here
   (that's the measured `interest` phase). They were merely **geometrically
   ineffective**: the DB-less region is a 1000×1000 square, the 300 u mid radius covers
   ~28 % of it, every player spawns at the same point (0,0,0), and clamped random-walk
   movement pools bots at the walls. All baselines in this doc therefore measure the
   hardcoded *tiered* policy under worst-case clustering; a true `full` (unfiltered)
   baseline does not exist in any binary yet — Phase 1 of the telemetry strategy builds
   it, plus spawn-spread and loader-wander knobs to make radius experiments meaningful.
5. **AM4's higher idle-load RTT** (45 ms vs cloud's 22 ms at 50 bots) reproduced in
   Follow-up A's fresh baseline (43/61/56 ms) — but the probe **weakens** the
   memory-bandwidth suspicion: RTT got *better*, not worse, while inference actively ran.
   Current best hypothesis is **power management** — an idle desktop parks cores in deep
   idle states, while a loaded box (or a busy cloud host) keeps them awake — which also
   explains the counter-intuitive loaded-run speedup.

## Why this matters economically

The fleet's compute is overwhelmingly **GPU/RAM-bound** (inference, ComfyUI, local
models), not CPU-bound. Because the authority server is **CPU-light and RAM-light**
(~1–3 cores, ~150 MiB), it is a near-free **co-tenant** riding the idle CPU on GPU/RAM-bound
nodes — especially in **off-hours / low-GPU windows**. This is the economic engine for the
platform's placement/leasing layer: harvest spare CPU, place authority shards there, and
gracefully drain when GPU or memory-bandwidth demand spikes. The one real coupling to
respect is that this is a **20 Hz real-time** workload (50 ms tick budget): the live risks
under heavy GPU load are **memory-bandwidth contention** and **CPU scheduler jitter**, not
CPU or RAM capacity.

## Follow-up A — GPU-contention probe (AM4, same date)

Answers former next-step 1: can the authority server co-tenant on a GPU node *anytime*,
or only off-hours? Method: a fresh idle baseline sweep, then the identical sweep while a
real local inference job ran continuously — llama.cpp (SYCL) serving Qwen3-30B-A3B fully
resident on AM4's GPUs (**2× Intel Battlemage dGPU** — a hardware correction: the box is
not NVIDIA), ~80 tok/s in back-to-back generations for the whole sweep. Local execution
was verified by package power (RAPL: ~26 W idle → ~118–120 W plateau, back to ~26 W
within 20 s of killing the loop) — necessary because HEARTH can route inference to cloud
backends, which would silently invalidate the probe.

| Bots | Idle (fresh baseline) | GPU-loaded |
|---|---|---|
| 50 | 43 ms / ~0.19 core | 33 ms / ~0.26 core |
| 100 | 61 ms / ~0.5 core | 51 ms / ~0.6 core |
| 200 | 56 ms / ~1.8 core | 63 ms / ~1.8 core |

(avg RTT / gateway steady CPU; **0 errors in all six runs**; RSS 120–190 MiB throughout.)

**Reading: no contention regression at these levels — "anytime", not "off-hours-only".**
Loaded RTT was *lower* at 50/100 and inside the idle run's own interval spread at 200.
The likely mechanism for the counter-intuitive speedup (and for caveat 5) is power
management, not bandwidth. Scope caveats: the model sat fully in dGPU VRAM, so host-DRAM
bandwidth was not heavily exercised — shared-memory/iGPU inference and RAM-bandwidth-heavy
jobs remain untested; and because the bare bench container exports no telemetry (the tick
metrics exist but need an OTLP collector — see Next steps), RTT was the only tick-health
proxy available.

## Follow-up B — off-host clean re-run (cloud + AM4, same date)

Answers former next-step 2: drive each host from a separate machine so the driver never
shares CPU with the host under test. Cloud: driven from an in-region `e2-standard-8` VM
inside the same VPC (driver VM deleted after; the gateway VM was found stopped and was
restored to stopped). AM4: driven from OMEN over LAN (ufw opened for 4100/4105 during the
run, then reverted). Levels above 200 bots ran as parallel 200-bot loader shards — a
single node driver process is single-threaded — and driver CPU was sampled at every
level and never came close to bottlenecking. Ops note: overriding the gateway's listen
address requires a literal `Urls=` env var; the image's baked `appsettings.json` beats
`ASPNETCORE_URLS` in this app's config order.

| Bots | ☁️ Cloud (8 vCPU) | 🖥️ AM4 (12C/24T) |
|---|---|---|
| 100 | 30 ms / ~20.0 Hz / ~0.61 core | 32 ms / ~0.5 core |
| 200 | 75 ms / ~19.2 Hz / ~2.9 core | 49 ms / ~1.3 core |
| 400 | **752 / 958 ms — tick ~12 Hz** / ~3.75 core (47 % of 8) | **1571 / 2115 ms, climbing** / ~3 core (~12 % of 24) |

(avg RTT per 200-bot shard / observed tick rate where measured / gateway steady CPU.
Cloud tick rate came from polling the gateway's `GET /tick` counter; AM4's was not
polled. **0 errors at every level on both hosts** — degradation is pure latency/tick-lag,
never failures.)

**Reading: both hosts knee at the same level — 400 bots — with CPU nowhere near
saturated.** That is the signature of the **single-threaded O(N²) broadcast fan-out**
blowing the 50 ms tick budget: an architectural wall, independent of host hardware. Two
corollaries:

- Caveat 1 is confirmed and quantified: at 200 bots the cloud box measured 165 ms with a
  co-located driver vs **75 ms** off-host (AM4: 56 → 49 ms).
- Hardware is not the capacity lever. A single full-interest region tops out between 200
  and 400 bots on an 8-vCPU cloud VM and a 24-thread desktop alike; the lever is
  **interest management / parallelizing the fan-out** (build-order steps 3–4).

## Follow-up C — tick-phase attribution (same date, new instrumentation)

Commit `6f1670d` (branch `claude/optimistic-mclean-435ef7`) landed per-tick/per-phase
timing: `game.tick.duration` histograms plus a DB-less ~5 s rolling window emitted as a
"Tick timing" log line and as `tick_timing` on `GET /tick`. A fresh image built from that
commit (`lumberjacks-gateway:tick`, left staged on AM4) re-ran the off-host AM4 levels —
this converts Follow-up B's RTT-inferred knee into a measured tick p99 with phase
attribution (steady-state windows; 60 s levels; driver on OMEN):

| Bots | tick total p50 / p99 | overruns per 100 ticks | interest p99 | send p99 |
|---|---|---|---|---|
| 0 | 0.01 / 0.03 ms | 0 | 0 | 0 |
| 200 | ~8 / 39–44 ms | 0–1 | ~2 ms | 37–42 ms |
| 400 | ~30 / **160–175 ms** | **25** | ~8 ms | **152–168 ms** |

Findings, in order of importance:

1. **The wall is the `send` phase.** At 400 bots, broadcast is ~100 % of tick time and
   `send` outweighs `interest` ~20:1 (152–168 ms vs ~8 ms). Serializing/writing updates
   to N clients in a single-threaded loop is the whole knee; sim (~0.4 ms) and hash
   (~0.2 ms) are irrelevant at these scales.
2. **The full-interest safe ceiling is ~200 bots/region.** 200 bots fits the 50 ms
   budget but consumes ~80–88 % of it at p99 (one window grazed 49.8 ms with 1 overrun);
   400 blows it 3.3× with a quarter of all ticks overrunning.
3. **Overrun delays are self-inflicted, not host jitter.** `interval` p99 (tick-start
   spacing) sits at the nominal ~50–51 ms at idle and 200 bots, and tracks total p99
   exactly at 400 — ticks start late because the *previous* tick overran, not because
   the scheduler starved the loop. This also retro-validates Follow-up A: co-tenant GPU
   load never disturbed tick spacing.
4. **Ticks are bimodal under load** (400 bots: p50 30 ms vs p99 166 ms) — the cost
   concentrates in the ~25 % of ticks doing heavy broadcast work.
5. **The serial fan-out is unfair to late joiners.** At 400 bots the first-connected
   shard averaged 49 ms RTT while the second averaged 1798 ms (and finished 4 bots shy
   of clean disconnect, 0 errors) — early sessions get served first in the send loop and
   late sessions absorb nearly the entire send-phase delay. Re-test fairness after the
   fan-out rework.
6. **An idle region is essentially free** (total p50 0.01 ms) — reinforcing the
   co-tenancy economics from Follow-up A.

## Follow-up D — replication-policy A/B (same date; Phase 1 of the telemetry strategy)

Branch `agent/replication-policies` added env-selectable policies to the broadcast path —
`tiered` (default; the previously hardcoded near-100 u / mid-300 u-every-4th-tick / far-drop
behavior), `full` (no filtering; the true baseline that never existed in a binary), and
`radius` (hard cutoff at NearRadius) — plus sent/culled counters in the tick-timing
window, a `World__SpawnSpread` knob, and a loader `BOT_WANDER` waypoint mode. The
off-host AM4 rig then ran the matrix (60 s runs, fresh gateway per run; "spread" =
SpawnSpread + wander, i.e. realistic spatial distribution; "clustered" = the old
everyone-spawns-at-origin behavior):

| Policy | Bots | Spatial | tick p50/p99 | overruns/100 | send p99 | sent:culled |
|---|---|---|---|---|---|---|
| tiered | 400 | clustered | 30 / **168 ms** | 25 | 160 ms | 1:14.4 |
| full | 400 | spread | no steady state — p99 0.9–2.4 s | 27–76 | up to 2.36 s | 1:0 |
| tiered | 400 | spread | 40 / **272 ms** | 25 | 264 ms | 1:8.6 |
| **radius** | **400** | spread | 40 / **46 ms** | ~0 | 39 ms | 1:23.7 |
| full | 200 | spread | no steady state — overloaded entire run | 84–100 | 206–467 ms | 1:0 |
| tiered | 200 | spread | 11 / 71 ms | 25 | 68 ms | 1:8.3 |
| radius | 200 | spread | 11 / **15 ms** | 0 | 12.5 ms | 1:21.9 |
| radius | 800 | spread | knee: transient p99 2.7 s, then 100/100-overrun plateau | 100 | 128 ms+ | 1:27.1 |

Zero errors in every run; drivers proven non-bottlenecked (27–60 % per loader container).

**Findings:**

1. **Continuity holds.** The new binary's `tiered` under clustered spawns reproduces
   Follow-up C exactly (p99 167–169 ms, 25/100 overruns) — the refactor changed no
   physics, and cross-run comparisons are valid.
2. **The Phase-1 success criterion is met: `radius` (100 u) holds 400 bots at p99
   ~46 ms with ~0 overruns** — a ~6× tick-time improvement over `tiered` under the same
   spatial conditions, doubling the previous ~200-bot ceiling. At 800 it collapses
   (transient p99 2.7 s, then a sustained 100/100-overrun plateau that only recovers as
   bots drop) — a hard knee, not graceful degradation. Bisection narrows the bracket
   (see below).
3. **`full` cannot reach steady state even at 200 bots.** The unfiltered baseline is
   not merely slower — it is unservable at loads the tiers handle easily. Every capacity
   number this repo has ever measured owes its feasibility to interest filtering.
4. **Realistic spatial distribution is *harsher* than the old clustered behavior for
   `tiered`** (400 bots: p99 168 → 272 ms; cull ratio 1:14.4 → 1:8.6). The historical
   baselines — measured under origin-spawn clustering — *flattered* the tiered policy;
   wall-pooled clusters put more pairs in the far-drop band than a uniform spread does.
   Carry this forward: spread+wander is the honest load shape.
5. **The late-joiner fairness problem persists under every policy** (shard whole-run
   RTT spreads up to ~2× at 800 bots, divergent disconnect counts). Policies cut send
   volume; they don't reorder the serial send loop. This is Phase-2 material.
6. **Measurement caveat:** the loader's "avg RTT" is a whole-run average dominated by
   the join surge (e.g. radius@400 shows seconds-scale averages while every steady-state
   tick fits the budget). Trust the tick windows for server health; treat loader RTT as
   a surge-inclusive aggregate until the loader reports steady-state percentiles.
7. **Policy semantics are a gameplay trade, not a free win:** `radius`(100 u) means
   players beyond 100 u receive no position updates at all. The rig exists to price
   these trades (radius value, tier intervals) in tick-budget terms; 100 u is the
   aggressive end of the dial, not a recommendation.

**Knee bisection (radius policy, spread): the ceiling is 400 bots; the knee sits in
(400, 500].** 500 bots *soft-fails* — tick p99 settles at 76–112 ms (~1.5–2× budget) with
97–100/100 overruns, sustained but stable. 600 bots *collapses* — p99 to 576 ms with a
**20.9-second max-tick stall** and 100/100 overruns, same runaway signature as 800. Two
distinct failure regimes, both send-dominated (interest stays 9–16 ms even at 600): a
narrow soft-overrun band just past the knee, then a cliff. The cliff — and the fact that
recovery only ever comes from bots disconnecting — is the strongest argument yet that
Phase 2 needs backpressure/shedding in the send loop, not just more culling.

## Follow-up E — send-loop rework after-snapshot (same date; Phase 2)

Branch `agent/send-loop-phase2` (on top of the merged rig) added four independently
toggleable mechanisms, all default-off/serial: chunked parallel fan-out
(`Replication__SendWorkers`, 0 = auto→8), always-on fairness rotation, per-broadcast
deadline shedding (`Replication__BroadcastDeadlineMs` — a send that misses the deadline
gets its socket aborted), and adaptive degrade (`Replication__AdaptiveDegrade` — halve
load the tick after an overrun). The loader gained steady-state RTT percentiles with a
first-vs-last-quartile bot split. After-matrix (radius policy, spread+wander, 60 s,
off-host AM4; before-curve = Follow-up D):

| Run | knobs | tick p99 (steady) | worst tick | over/100 | verdict |
|---|---|---|---|---|---|
| 400, 8 workers | parallel only | 44–53 ms | 57 ms | 0–21 | ≈ serial before (~46 ms) — 400 was never worker-bound |
| 500, 8 workers | parallel only | 82–107 ms | 109 ms | 95–100 | soft-fail band unchanged (before 76–112) |
| 600, 8 workers | parallel only | 95–139 ms | 374 ms | 90–100 | **cliff → slope** (before: 576 ms p99, 20.9 s stall) |
| 800, 8 workers | parallel only | 163–167 ms | 7.6 s (join surge, no deadline) | 100 | server bounded; clients drown (RTT p95 20–22 s) |
| tiered 400, 8 workers | parallel only | 236–291 ms | 299 ms | 24–32 | tiered not rescued — volume-bound, not worker-bound |
| 600, serial + 150 ms deadline | deadline only | 123–174 ms | **249.8 ms** | 100 | **cliff killed: worst tick 20,900 → 250 ms (~84×), 17 aborts total** |
| tiered 400, adaptive | adaptive only | 261–286 ms | 316 ms | 25 | **miss** — degrade fires the tick *after* the overrun, which for tiered's every-4th-tick burst is a cheap tick; wrong phase |

**Reading:**

1. **The budget-compliant ceiling stays ~400 bots (radius).** Parallelism did not raise
   it: 500 fails the same, 400 is no faster than serial.
2. **What Phase 2 actually bought is failure shape.** 500→800 now degrade roughly
   linearly (~0.2 ms of p99 per bot past 400, all bounded) instead of collapsing, and
   deadline shedding converts the 20.9-second stall into a 250 ms worst tick at the cost
   of 17 targeted disconnects. Cliff → slope + bounded stalls is exactly what a public
   alpha needs from overload — it just isn't more capacity.
3. **Why parallelism didn't help — the parallel-efficiency signal fired:** summed
   worker send time ≈ broadcast wall time in every parallel run (e.g. 600 bots: sum
   87 ms vs wall 102 ms; 8 workers should approach wall ≈ sum/8). The workers are not
   overlapping. Prime suspect: **the single shared `UdpClient`** — ~97 % of update
   volume leaves through one socket via synchronous `Send`, so eight workers funnel
   into one kernel-serialized resource. Testable Phase-3 hypothesis: per-worker UDP
   sockets (or async batched sends) should pull wall toward sum/8. (CPU headroom was
   ample — ~3.4 of 24 cores — so it is not compute-bound.)
4. **Adaptive degrade has a design bug, with a known fix:** the halving must be
   burst-aligned (suppress the *next burst tick's* mid-band after a burst-tick overrun),
   not next-tick-aligned. Not rebuilt this phase.
5. **At 800 the bottleneck moves to the clients** — the server ships ~460 k updates/s
   bounded, and client RTT hits 20+ s anyway. More server throughput is the wrong lever
   past the interest ceiling; **per-client send budgets** (ADR-0011's real intent) are
   the missing mechanism.
6. **Fairness inverted rather than fixed:** with rotation on, *first*-connected bots
   now fare consistently worse than last-connected (e.g. p50 3.7 s vs 0.8 s at 400) —
   open issue; the rotation/join-order interaction needs a look before trusting any
   fairness claim.

## Follow-up F — Phase 3(a): UDP-socket hypothesis, degrade v2, dial sweep (same date)

Branch `agent/udp-sockets-degrade-v2` added `Replication__UdpSockets` (N send-only
sockets so workers stop sharing one `UdpClient`) and a burst-aligned adaptive-degrade v2.
Measured off-host on AM4 (radius/spread+wander, 8 workers + 8 UDP sockets unless noted;
`/tick` window snapshots — note the whole matrix ran deadline-off, so it isolates
parallelism, not shedding). The run agent crashed twice mid-matrix (API stalls, then the
Fable-5 usage limit); all seven runs' evidence was recovered from disk.

| Run | knobs | tick p99 | overruns/100 | interest (sum) | send (sum) | bcast **wall** | sent:culled |
|---|---|---|---|---|---|---|---|
| radius 500, 8w/8sock | parallelism test | 87 ms | 100 | 9.6 | 78.3 | **87.0 ms** | 1:19 |
| radius 600, 8w/8sock | " | 107 ms | 100 | 13.8 | 94.3 | **106.4 ms** | 1:22 |
| radius 400, 8w/8sock | " | 49 ms | ~1 | 7.4 | 41.8 | **49.0 ms** | 1:23 |
| radius 400, NearRadius=200 | dial | 141 ms | 100 | 7.4 | 134.2 | 140.7 ms | 1:6.3 |
| radius 400, NearRadius=300 | dial | 469 ms | 69+ | 15.0 | 461.2 | 468.8 ms | 1:3.2 |
| tiered 400, adaptive v2 | degrade | 288–322 ms | **12–19** | 8.6 | 279–314 | 288–321 ms | 1:11 |

**Findings:**

1. **The shared-`UdpClient` hypothesis is REFUTED — and the real bottleneck is now
   named.** With 8 independent send sockets, broadcast **wall time still equals
   interest+send summed across workers** in every run (500 bots: wall 87.0 ≈ 9.6+78.3;
   600: 106.4 ≈ 13.8+94.3; 400: 49.0 ≈ 7.4+41.8). That is *zero* wall-clock parallelism —
   statistically identical to Phase 2's one-socket result (500 bots: 87 ms now vs
   82–107 ms then). The socket was never the constraint. Root cause: the `Task.WhenAll`
   fan-out doesn't parallelize because the per-chunk sends **complete synchronously**
   (UDP `Send` is sync; small-frame WebSocket `SendAsync` completes inline on the LAN),
   so all chunks run inline-serial on the tick thread with no thread-pool dispatch — CPU
   stayed ~3 of 24 cores, confirming it isn't compute-bound, just undispatched. **The
   Phase-3 fix is to force thread-pool dispatch** (`Task.Run` / `Parallel.ForEachAsync`
   per chunk) so the CPU-bound per-entity serialization actually spreads across cores;
   more sockets were the wrong lever.
2. **Ceiling unmoved: ~400 bots (radius, NearRadius=100).** 500/600 fail exactly as
   before. Consistent with #1 — nothing actually parallelized.
3. **Adaptive degrade v2 is FIXED but insufficient alone.** v2 now fires on burst ticks
   (degraded_ticks 12–13/window, vs v1's effective zero), halving tiered/400 overruns
   from ~25/100 to **12–19/100** — a real, measured win over the v1 no-op. But the
   un-suppressed burst ticks still cost ~250–320 ms, so p99 stays high: degrade reduces
   overrun *frequency*, not per-burst cost. Tiered@400 still needs radius or true
   parallelism.
4. **Dial sweep — view distance costs ≈ quadratically** (radius, 400 bots): NearRadius
   100 → p99 49 ms (fits, 1:23 culled); 200 → 141 ms (fails, 1:6.3); 300 → 469 ms
   (fails, 1:3.2). **100 u is the only budget-fitting radius at 400 bots**; each step up
   roughly triples send cost. This is the gameplay-vs-capacity price list the strategy
   asked for: wider awareness is affordable only by trading player count or by real
   parallelism.
5. **Fairness inversion PERSISTS and rotation did not fix it.** Earliest-joined bots
   (first quartile) suffer p50 RTT 2776–3785 ms while last-quartile see 43–67 ms — a
   40–65× penalty, consistent across every run. Since the rotation equalizes send
   *order* each tick, the penalty is almost certainly **not** send-order; likely
   loader-side (earliest bots accumulating receive backlog) or a per-session endpoint
   effect. Needs a dedicated probe before any fairness claim — do not assume rotation
   helps.

**Clean-rerun confirmation (same date).** The full matrix was re-run from scratch against
the staged `phase3a` image and completed cleanly — every Follow-up F conclusion held. The
**`send:wall` ratio** (send-phase sum ÷ broadcast wall) landed at **0.88–0.97 across all
parallel runs** — the crispest possible confirmation of the no-parallelism result (true
parallelism would drive the ratio toward 1/workers; ≈0.9 means the wall *is* the serial
sum). radius/500 → p99 94 ms, 100 overruns; radius/400 → p99 51 ms, 3 overruns (the
ceiling); radius/600 → population collapsed (98 % disconnect, never sustained — the
hard-ceiling signature); tiered/400 v2 → overruns 12–13 (halved), p99 277 ms (insufficient
alone); NearRadius 200/300 → p99 137/275 ms (the quadratic dial holds). Fairness inversion
reproduced (first-quartile ~1.8–2.3 s vs last-quartile ~50–200 ms p50) — though under the
worst thrash (NearRadius 300) the order effect breaks down entirely, another sign it is a
saturation artifact rather than a scheduling property.

**Robustness bug found during the rerun:** `Replication__AdaptiveDegrade=off` — a
plausible operator value — hard-crashes the gateway with an unhandled `FormatException`
(exit 139), because the option binds to a .NET `bool` (only `true`/`false` accepted).
This did **not** cause the earlier agent stalls (those were harness/infra/quota; the
earlier gateways ran fine and produced valid data) — but startup config binding should
validate-and-default, not crash the process. Filed as a background task.

## Follow-up G — Phase 3 a′: thread-pool-dispatched fan-out MOVES the ceiling (same date)

Branch `agent/parallel-send-dispatch` made the existing `SendWorkers>1` path genuinely
dispatch its chunks to the thread pool (`Parallel.ForEachAsync`, `MaxDegreeOfParallelism
= workers`), with per-socket locks + per-chunk-local accumulators for thread-safety;
`SendWorkers=1` is byte-identical serial. Measured off-host on AM4 (radius/spread+wander,
W=8/U=8 unless noted) with a **serial control** (W=1/U=1) to attribute the delta.

| config | bots | tick p99 | overruns/100 | **broadcast wall p99** | send:wall | CPU cores | verdict |
|---|---|---|---|---|---|---|---|
| serial control | 500 | 68 ms | 100 | 68 ms | 0.81 | ~3.4→collapse | old failure; bots 496→200 |
| parallel | 400 | 12.7 ms | 0 | 12.3 ms | 5.3 | 1.8–1.9 | headroom (was ~49 ms) |
| parallel | 500 | 15.9 ms | 0 | 15.4 ms | 5.6 | 2.4–2.8 | **FITS — ceiling broken** |
| parallel | 600 | 22.5 ms | 0 | 21.9 ms | 5.8 | 2.7–3.3 | holds |
| parallel | 800 | 102 ms | 92–98 | 101 ms | 7.0 | high | cliff — O(N²) wins |
| parallel tiered | 400 | 261 ms | 26–32 | 260 ms | 6.4 | 1.0–1.8 | NOT rescued |

1. **The ceiling moved for the first time in this investigation: ~400 → ~600 bots
   (radius/100), a ~50 % capacity gain.** radius/500 went from 100/100 overruns (serial,
   p99 68 ms, bots collapsing 496→200) to **0 overruns** (parallel, p99 16 ms, all 500
   sustained). radius/600 also holds (p99 22 ms). Capacity-per-volunteer-node rises
   accordingly.

2. **The `send:wall` ratio measures parallel speedup — and confirms the fix.
   [Corrects Follow-up F's framing.]** It is send-summed-across-workers ÷ broadcast wall:
   serial ≈ 1 (work = elapsed), perfect 8-way ≈ 8 (work spread over 8 threads → wall =
   work/8). Measured: serial control **0.81**, parallel **5.3–7.0** = **5–7× effective
   speedup, ~70–88 % efficiency.** (Follow-up F and the run brief mislabeled the target as
   "toward 1/8"; the correct success signal is the ratio rising toward the worker count.
   The wall-clock collapse 68 → 15 ms at 500 bots is unambiguous regardless.)

3. **The win is cheaper than "CPU-bound" predicted — it overlaps I/O too.** CPU rose only
   to ~2.8 cores while delivering 5.6× speedup; purely CPU-bound work would need ~5.6
   cores. So a large part of the send phase was **socket-write latency** that serial
   execution couldn't overlap and parallel execution gets nearly for free — refining
   Follow-up F: the serial bottleneck was CPU serialization *and* un-overlapped I/O wait,
   not CPU alone.

4. **New ceiling 600–800; O(N²) fan-out is the ultimate wall.** At 800, send-summed hits
   707 ms → even 8× parallelism (wall ~90–130 ms) can't fit the budget, and interest
   itself rose to ~39 ms. Parallelism bought a constant factor (~the worker count), not an
   asymptotic change. Past ~600–700 the lever is attacking O(N²) directly (tighter
   interest, or serialize-once-per-entity payload sharing), not more threads.

5. **Tiered/400 is NOT rescued, and reveals a second, distinct bottleneck.** p99 stayed
   ~261 ms with ~30 overruns — and it used *less* CPU (1.0–1.8 cores) than radius under
   the same parallel config. Lower CPU + no gain under parallelism ⇒ it is blocked on a
   lock or serial section in the mid-band broadcast path, not on send throughput. A
   separate investigation from the send fan-out.

6. **The serial control validates the comparison** — it reproduces the historical
   ~68–87 ms / 100-overrun serial failure (including the bot collapse), so the runs-1–3
   improvement is the dispatch fix, not rig drift.

7. **Fairness — partial retraction.** The loader's first-vs-last-quartile RTT split that
   Follow-ups E/F read as a server fairness inversion is substantially a **loader
   artifact**: each Node shard serially services 200 bots, so the earliest bots in that
   loop see structurally different RTT regardless of server behavior. The server-side
   rotation may be working; the "inversion" is not a trustworthy server signal. A real
   fairness measurement needs a multi-process-per-bot loader.

## Robustness bug found during the rerun (Phase-3a, same date)

Setting up the Phase-3a matrix, a gateway launched with `Replication__AdaptiveDegrade=off`
(a plausible operator value — `off`/`on` read as booleans everywhere else) **hard-crashed on
startup**: an unhandled `FormatException` from the config bool-binder took the process down
with exit 139 before the first tick. The same failure mode existed for every numeric knob
(`SendWorkers=lots`, `BroadcastDeadlineMs=soon`, `NearRadius=close`, …): the raw
`IConfiguration.GetValue<T>` binder throws rather than degrading when a present value can't be
converted. A single mistyped env var could kill a volunteer-hosted shard on boot — unacceptable
for the community-hosting posture.

**Fix:** [`ReplicationOptions.FromConfiguration`](../src/Game.Simulation/World/ReplicationOptions.cs)
now parses every key with explicit `TryParse`-and-fall-back-to-default semantics (the policy
string already did this). An unrecognized value logs a clear warning naming the key, the bad
value, and the expected shape, then substitutes the documented default — the process starts.
Regression coverage in
[`InterestManagerTests`](../tests/Game.Simulation.Tests/InterestManagerTests.cs) asserts that
`AdaptiveDegrade=off` and a garbage `SendWorkers=lots` yield defaults, not an exception, and that
the warning sink fires with the offending key/value.

## Next steps

1. ~~Contention probe~~ — **done** (Follow-up A): no regression under local dGPU
   inference; "anytime" co-tenancy holds at these load levels.
2. ~~Off-host clean re-run~~ — **done** (Follow-up B): true knee = 400 bots on both
   hosts, and it is architectural, not hardware.
3. ~~Make tick health observable in the bench harness~~ — **done** (Follow-up C):
   commit `6f1670d` added the DB-less tick-timing window (log line + `GET /tick`), and
   the confirmation run measured the knee directly. Merge that branch.
4. ~~Move the knee~~ — **done** (Follow-up D): `radius`(100 u) holds 400 bots at p99
   ~46 ms; the policy rig now prices interest policies in tick-budget terms.
5. **Merge & reconcile.** `agent/replication-policies` (master-based: TickMetrics +
   policies) and this docs branch (delivery metrics / `LumberjacksTelemetry`) each carry
   instrumentation the other lacks; reconcile `TickBroadcaster` at merge (suggested:
   TickMetrics canonical for tick timing, LumberjacksTelemetry keeps delivery).
6. ~~Phase 2 — the send loop itself~~ — **done** (Follow-up E): cliff killed (worst
   tick 20.9 s → 250 ms via deadline shedding), overload now degrades linearly; ceiling
   unmoved at ~400; adaptive degrade missed (fix known); fairness inverted (open).
7. **Phase 3 candidates, updated by Follow-up G:** (a′) ~~force thread-pool dispatch~~
   **DONE — ceiling moved ~400→~600** (Follow-up G; 5–7× speedup). (b) ~~adaptive degrade
   v2~~ **done**. **New priorities past the ~600 ceiling:** (f) **attack the O(N²) fan-out**
   — this is now the ultimate wall (serialize-once-per-entity payload sharing, tighter
   spatial interest tiers); more threads won't help past ~700. (g) **the tiered lock** —
   tiered/400 uses *less* CPU than radius yet won't parallelize, so it's blocked on a
   serial section in the mid-band path; find and remove it. (h) **per-client send budgets**
   (ADR-0011) for the client-drowning regime past the interest ceiling. (i) **multi-process
   loader** — the current single-Node-per-shard loader can't measure server fairness (its
   RTT quartile split is a loader artifact; Follow-up G #7).
8. **Dial-tuning for gameplay:** sweep `Replication__NearRadius` (100 → 200 → 300) and
   tier intervals to find the largest gameplay-acceptable policy that still fits the
   budget at target load — the rig makes this a table, not a debate.
9. ~~Loader improvement~~ — **done** (Phase 2): steady-state RTT percentiles + quartile
   fairness split shipped. Note the pre-existing JSON-lane RTT parse bug (chipped as a
   background task) — benchmarks ride the binary/UDP lane, which records correctly.
10. **Contention probe, hard mode (optional):** repeat Follow-up A with a
    host-DRAM-heavy job (shared-memory/iGPU or CPU inference); the `interval` phase
    metric now makes scheduler jitter directly visible.
