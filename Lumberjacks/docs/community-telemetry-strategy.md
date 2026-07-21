# Community Telemetry Surface — Strategy (adopted 2026-07-12)

Status: **Adopted** — Derek's draft goals (2026-07-12) plus refinement decisions grounded
in the host-capacity evidence
([benchmark-host-capacity-2026-07-12.md](benchmark-host-capacity-2026-07-12.md),
Follow-ups B/C). Phase 1 is in build.

North star framing: the Community Dashboard evolves from an operator dashboard into the
**primary community-facing telemetry surface** of the
community-compute platform (dashboard §10).

## The six goals (draft, condensed)

- **G1 — Public Telemetry API.** Documented read-only API over the existing telemetry and
  dashboard model; the dashboard becomes one client of the API, not the owner of the
  data; community members can build dashboards, overlays, bots, analytics.
- **G2 — Live Community View.** Real-time ops view (active servers, connected players,
  latency, session duration, region/zone, activity summaries, telemetry streams, health)
  — the "everyone is an alpha tester" dashboard.
- **G3 — Integrated NetworkSense.** Merge ComfyNetworkSense into the primary runtime
  experience (player telemetry, HUD/panel, MCP-assisted actions, local AI summaries,
  diagnostics, multiplayer visibility).
- **G4 — Quest + Network Integration.** The quest trigger system's events (first hit,
  killing blow, weapon usage, projectiles, trigger pipeline, evidence events) become
  first-class gameplay telemetry.
- **G5 — Local Testing Tools.** In-game developer tab for repeatable multiplayer testing:
  spawn configurable enemy groups, scripted encounters, benchmark scenarios, telemetry
  capture, replay routes; UI over console commands.
- **G6 — Network Filtering Experiments.** Tooling to observe, measure, and **compare**
  replication policies — interest management, replication distance, update frequency,
  object filtering, priority, bandwidth reduction, visibility rules — rather than
  immediately optimize.

Design principles (kept verbatim in spirit): the dashboard consumes APIs and does not own
state; APIs are stable, documented, community-consumable; every feature produces evidence
and telemetry; favor clickable workflows; optimize for demonstrations, volunteer
contributors, experimentation, and learning — not just operations.

## Refinements (the adopted deltas)

1. **Sequence: G6 → G1 → G2; G3/G4/G5 trail as a client/UX track.** G6 is listed last in
   the draft but has the strongest tailwind and the highest leverage right now: the
   off-host harness, the per-phase tick metrics (commit `6f1670d`), and a measured
   baseline (400 bots → tick p99 160–175 ms, `send` p99 152–168 ms, 25 % overruns;
   full-interest ceiling ≈ 200 bots/region) all exist as of this date. Replication
   policies are exactly the lever on the measured `send`-phase wall, and every policy
   that moves the ceiling changes capacity-per-volunteer-node — the quantity Hosting
   Credits price. G1 then exposes what exists (`/tick`, `/live/*`, EventLog
   projections); G2 builds on G1.
2. **No stability promise yet.** The telemetry model is actively moving (`tick_timing`
   changed shape this date; interest tiers will add per-tier metrics). The API ships as
   explicitly versioned **v0/unstable**; the "stable, documented" promise activates
   after the fan-out rework settles the schema.
3. **Privacy/griefing policy decided up front.** Live player positions/activity are a
   griefing and stream-sniping surface. Public feeds are **aggregated or delayed**; raw
   live feeds require auth; the public API is served from the **coordinator/control
   plane**, never from volunteer authority nodes (they must not absorb community read
   traffic).
4. **The observation plane stays off the tick path.** The serial send loop is the
   measured knee; any live-view streaming consumes the event log / metrics pipeline as a
   separate consumer, never as another loop inside the tick.
5. **G5's "run benchmark scenarios" wraps the existing harness**
   (`scripts/load-test-dual-channel.js`) rather than growing a second in-game load path
   that would drift from it.
6. **Delegation policy for the build.** Cheap text chores (summaries, table formatting,
   boilerplate drafts, log digestion) offload to **Gemini Flash/Pro via HEARTH**
   (door verified healthy and both backends live-probed this date; call surface:
   `HearthClient().call_sync("local_generate", ..., backend="gcp-gemini"|"gcp-gemini-pro")`
   from the commandcenter venv). Code-editing judgment stays with Claude agents;
   HEARTH-routed generation is never a local-GPU load source (see the Gemini-trap rule
   in the benchmark doc's Follow-up A).

## Phase 1 (in build) — replication-policy experiment rig

Measured baseline to beat (Follow-up C): 200 bots fits the 50 ms budget at ~85 %
consumption; 400 bots → tick p99 160–175 ms, `send` p99 152–168 ms (~20:1 over
interest), 25 % of ticks overrunning; serial fan-out unfair to late joiners
(49 ms vs 1798 ms shard RTT).

Deliverables:

1. **Policy abstraction in the broadcast path** with named, env-selectable policies:
   `full` (baseline, current behavior), `radius` (replication-distance culling),
   `tiered` (distance-banded update frequency). Config via the established env pattern
   (`Replication__Policy` etc.); exact knobs finalized after code recon of the existing
   interest machinery.
2. **Per-policy observability:** active policy name plus per-tick sent/culled counts on
   `GET /tick` and the "Tick timing" window line; per-client update/byte counts where
   cheap. A policy change must be visible in the evidence, not just in config.
3. **A/B measurement:** the off-host AM4 rig re-runs 200/400 (and 800 where policies
   permit) per policy, producing tick-phase tables against the baseline. If the loader's
   bots cluster at spawn (recon question), a movement/spread patch to the loader comes
   first — radius culling measured against co-located bots is meaningless.
4. **Comparison write-up** appended to the benchmark doc; the dashboard ledger records
   the outcome.

Success criteria: a measured knee movement (target: 400 bots under the 50 ms p99 budget
under a defensible radius/tier policy), zero-error load runs, no test regressions, and a
late-joiner fairness re-check.

Explicitly out of scope for Phase 1: **parallelizing the send loop.** Policies first,
optimization after — the draft's own ethos ("observe, measure, and compare policies
rather than immediately optimize"). Parallel send is Phase 2 if the measured policy
ceiling still demands it.

## Phase map

| Phase | Scope | Status |
|---|---|---|
| 1 | G6 replication-policy rig + A/B evidence | in build |
| 2 | Parallel send (if still needed) + G6 broader policies (priority, object filtering) | pending Phase 1 evidence |
| 3 | G1 public API v0 (unstable) over `/tick`, `/live/*`, EventLog projections | queued |
| 4 | G2 live community view as a client of v0 | queued |
| — | G3/G4/G5 client & UX track | parallel/trailing, unblocked by 1–4 |
