# Decision log — autonomous build run, 2026-07-12 (evening)

Derek stepped away with: "build out everything — do not stop at phase 1, build as much
as you can; leverage HEARTH/mechnet Gemini Pro; be frugal; document decisions; go with
instinct." Phases 1–2 are already in master. This log records what was chosen, why, and
what was deliberately not done, for later review.

## Scope chosen (evidence order, my call)

1. **Track A — G1 Public Telemetry API v0 + G2 Live Community View** (strategy Phases
   3+4). Endpoints under `/api/v0/telemetry/*` on the gateway: `server`, `tick`
   (TickMetrics window + replication block), `sessions` (aggregates only), `delivery`
   (LumberjacksTelemetry snapshots), `regions`. Every response marked
   `stability: unstable` (v0 per strategy refinement #2). CORS: GET-only, any origin.
   The community view ships as a self-contained page served by the gateway at
   `/community`, polling the v0 API — the strategy's "dashboard consumes APIs, owns no
   state" principle made literal.
2. **Track B — Phase 3(a) UDP-socket experiment + adaptive-degrade v2** (benchmark
   next-steps 7a/7c). `Replication__UdpSockets` (default 1 = today's shared client;
   0 = auto = worker count); degrade v2 becomes burst-aligned (suppress the *next burst
   tick's* mid-band after a burst-tick overrun) — the measured fix for Follow-up E's
   miss.
3. **Track C — measurement** (after B): UDP-socket A/B at 500/600 (does the soft-fail
   band clear when the wall drops toward sum/workers?), adaptive-v2 check at tiered/400,
   and the `NearRadius` 100→200→300 dial sweep at 400 bots (next-step 8's gameplay
   table). Fairness quartile splits captured in every run; active code-level diagnosis
   of the inversion is DEFERRED (observation first, instrumentation only if it persists
   under the new socket layout).

**Not attempted tonight:** G3/G4/G5 (NetworkSense integration, quest telemetry, in-game
testing tab) — game-client/UX surface, partly in other codebases, wrong thing to build
unattended; per-client send budgets (needs a design conversation about what "budget"
means per connection quality — ADR-0011 leaves it open); serialize-once payload refactor
(gated on the UDP-socket result by Follow-up E's own logic).

## Standing decisions for this run

- **Nothing merges to master autonomously.** Every master merge today was Derek's
  explicit call; that stays his. Work lands on pushed feature branches + this branch;
  review happens when he's back.
- **Privacy rule enforced in code, not docs:** the v0 API exposes aggregates only — no
  player ids, names, or positions anywhere in a response. A test asserts it.
- **Gemini Pro authorship boundary:** Gemini (via HEARTH, `gcp-gemini-pro`) drafts
  self-contained artifacts — the community-view HTML/JS page, the API reference doc,
  summaries. **C# stays Claude-authored** (integration risk; the fleet rule "never let
  HEARTH-routed generation author C#" predates tonight and held up well). This is the
  frugality split: bulk text/markup → Gemini, judgment/code → Sonnet agents, decisions →
  orchestrator.
- **UDP-socket caveat accepted:** extra send sockets bind ephemeral ports, so replies no
  longer originate from :4105. Fine for the LAN bench (the loader doesn't filter by
  source) and documented as a NAT hazard for real clients — this is an experiment knob,
  not a production default.
- **The rig serializes:** AM4 hosts one measurement at a time; code tracks run parallel
  in isolated worktrees.

## Outcomes (filled in as the run completed)

- **Track A — API v0 + Community View: SHIPPED** on `agent/api-v0-community-view`
  (commit `09f6133`). Five read-only v0 endpoints (aggregates only, privacy test
  enforces no player id/name/position leaks), a Gemini-Pro-drafted `/community` page
  (XSS-hardened + startup-fallback added in a review pass), API reference doc. 149
  Simulation + 31 Gateway tests green. Gemini authored the HTML/prose; Claude authored
  all C# and did the security fix-ups — the authorship split held.
- **Track B — UDP sockets + degrade v2: SHIPPED** on `agent/udp-sockets-degrade-v2`
  (commit `3b0da02`). 187 Simulation + 120 Contracts green. Defaults byte-preserving.
- **Track C — measurement: COMPLETED with a decisive negative result** (benchmark doc
  Follow-up F). The run agent crashed three times (two API stalls, then the Fable-5
  usage limit); I recovered all seven runs' evidence from disk rather than re-running,
  and finished the rig teardown check myself (containers gone on both hosts, ufw back to
  its 7-rule baseline — confirmed clean). **Headline: the shared-`UdpClient` hypothesis
  is refuted** — 8 sockets gave zero parallelism because the `Task.WhenAll` fan-out never
  dispatches (sends complete synchronously). The real Phase-3 lever is thread-pool
  dispatch of the send chunks, not more sockets. Degrade v2 works (halves overruns) but
  isn't enough alone; the dial sweep priced view-distance (~quadratic); the fairness
  inversion persists and rotation didn't fix it.
- **Decision honored:** nothing merged to master autonomously. Both build branches and
  the ledger are pushed for review; master merges remain Derek's call.
- **Judgment note for review:** I stopped short of building Phase-3 (a′)
  (thread-pool-dispatched fan-out) tonight even though Follow-up F points straight at it,
  because it is a real concurrency change to the hot path that deserves review of the
  negative result first — spending more agent time building on an unreviewed conclusion
  felt like the wrong call unattended. It is queued as the top Phase-3 candidate.

## Second session (later, on request: build G3/G4/G5 + rerun the sweep)

- **Sweep rerun: completed cleanly and CONFIRMS Follow-up F** (benchmark doc, "Clean-rerun
  confirmation"). The `send:wall` ratio 0.88–0.97 is the direct proof of the
  no-parallelism finding. It did *not* break this time, so per instruction I documented
  rather than root-caused. The rerun did surface a real gateway robustness bug
  (`AdaptiveDegrade=off` → unhandled FormatException) — filed as a background task; I
  corrected the run agent's claim that this caused the earlier crashes (it did not — the
  earlier stalls were harness/infra/quota).
- **G3/G4/G5 UI first pass:** built on `agent/community-ui-g3-g4-g5` off the API-v0 branch,
  Gemini-Pro-drafted + Claude-reviewed. Design directions I set (documented per-goal in
  `docs/ui/g3-g4-g5-first-pass.md`): G3 = glanceable color-coded overlay (live over
  gameplay > dense table); G4 = evidence timeline reusing the achievements provenance
  vocabulary (one mental model, "evidence immutable, interpretation separate"); G5 =
  clickable confirm-guarded scenario cards, benchmark card wraps the existing load
  harness (UI-over-console, no second load path). Honesty rule enforced: live v0 data
  where it exists, labeled "sample — backend pending" everywhere else.

## Third session (on "approved, onward"): Phase 3 a′ — the ceiling moved

- **Base decision:** built a′ on `agent/udp-sockets-degrade-v2` (the one branch with both
  the fan-out to fix and the per-worker UDP sockets that make true parallelism safe). The
  config fix (`0d9093f`, `claude/kind-panini-1c9767`) is an independent line off master and
  a′ doesn't depend on it (measurement passes valid booleans). Master is still 4 approved
  branches behind — flagged for consolidation on request; did not merge autonomously.
- **Result: SUCCESS — the capacity ceiling moved ~400 → ~600 bots/region (+50%)** — the
  first ceiling move in the whole investigation (Follow-up G). `Parallel.ForEachAsync`
  dispatch delivered 5–7× parallel speedup; radius/500 went from 100-overrun collapse to
  0 overruns; a serial control validated the delta.
- **Interpretation correction I had to make:** the run agent (and my own measurement brief)
  mislabeled the send:wall success direction as "toward 1/8"; the correct signal is the
  ratio rising toward the worker count (serial 0.81 → parallel 5.6). Documented the fix in
  Follow-up G so the record is right.
- **Two honest downgrades in the same pass:** tiered isn't rescued by parallelism (it's
  blocked on a separate mid-band lock — uses *less* CPU, won't scale), and the fairness
  "inversion" from Follow-ups E/F is largely a loader artifact (partial retraction). Better
  to correct the record than let a tidy-but-wrong story stand.
- a′ on branch `agent/parallel-send-dispatch` (197 tests green), pushed for review; not
  merged to master.
