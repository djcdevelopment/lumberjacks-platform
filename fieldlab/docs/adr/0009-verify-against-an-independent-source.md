# ADR 0009 — A check that reads its own output is not a check

- **Status:** Accepted (2026-07-21)
- **Rung:** cross-cutting; binds deploy scripts, test harnesses, evidence capture and fleet assays

## Context

One audit session surfaced five independent systems that **reported success while producing nothing**.
They had nothing in common by subsystem — a PowerShell deploy script, a docker-compose lab, a telemetry
collector, a game capture, and the build-farm's own grading lane — but they shared a structure:

1. **`deploy-gateway.ps1`** tarred two gateway source files from a default root that still held
   pre-cutover content, shipped them, then verified integrity by hashing **the files it had just
   tarred**. The hash always matched, because both sides of the comparison came from the same place. It
   would have shipped stale source and reported a clean deploy.
2. **`rollback-gateway.ps1`** copied source onto `/opt` and *then* ran `docker compose build gateway`,
   which this stack structurally forbids. It could only fail — after mutating the VM.
3. **`valheim-lab.compose.yml`** still set `COMFY_AUTOJOIN` after the code reading it was deleted.
   `docker compose up` would succeed and every client would idle at the character-select screen logging
   `rtt_ms = 0` — exactly the condition auto-join existed to prevent.
4. **The 2026-07-04 AoI campaign** wrote 998 result rows whose `avg_fps` was constant at `60.0` and
   whose `bytes_out_per_sec` varied 0.2% across the full range from empty control to extreme density.
   Its single real capture reported `rtt_ms`, `bytes_in/out` and `packets_in/out` all `0`, because the
   client sat in Solo mode and never connected. A full results file; zero measurements.
5. **The fleet assay** graded two builders **B / 70** whose own records said `empty_build: true`,
   `agent_rc: 3`, `"agent produced nothing"`. It scored `162/162 tests passed` and `has_retro: true`
   from the checkout it was handed, and quoted a retro excerpt three weeks older than the run.

In every case the failing component was *not* silent. It emitted a success, a receipt, a row, or a
grade. What it did not do was compare itself against anything it had not produced.

## Decision

**A verification must compare against a source independent of the thing being verified.** Concretely,
for anything that emits a pass/fail, a receipt, or a measurement:

- **Deploy and transport checks** compare the artifact against the *tracked* source (git object, release
  manifest, or digest recorded before transport) — never against the copy the process just wrote.
- **Capture pipelines reject non-measurements at capture time.** A sample whose transport fields are all
  zero, or whose values are invariant across the experiment's own independent variable, is discarded and
  logged as void — not written and discovered in analysis months later.
- **Graders score the diff, not the workspace.** Any assay must first assert the run produced work
  (`empty_build == false`, non-zero diff) before scoring properties of the tree.
- **A script that mutates then validates must validate first, or be transactional.** Ordering a
  destructive step ahead of a check that can fail turns a clean refusal into a dirty one.

Where a genuinely independent source is unavailable, the check **says so in its output** rather than
reporting a pass.

## Consequences

- **Some checks get slower or need a second input.** Comparing a shipped artifact against the tracked
  object costs a lookup that hashing local files does not. That cost is the entire value.
- **More runs will be voided, and that is the point.** A capture pipeline that rejects all-zero samples
  will throw away runs that previously produced files. Those files were never data.
- **Invariance is a first-class alarm.** If a metric does not move when the experiment's independent
  variable moves, the instrument is broken. This is cheap to assert and was not asserted anywhere.
- **It does not require new infrastructure.** Every instance above was detectable with information the
  system already had: git had the tracked source, the result rows carried their own variance, the assay
  had `empty_build` in the same object it was scoring.
- **Bounded scope.** This is about *self-referential* verification. It does not demand independent
  reimplementation of every check, nor does it forbid caching — only that the comparison's two sides not
  originate from the same write.

## Related

`infra/gcp/p7/scripts/deploy-gateway.ps1` (fixed 2026-07-21); the deletion of `rollback-gateway.ps1` and
`configure-player-gateway.sh`; `fieldlab/evidence/aoi-density-pressure-matrix-20260704/README.md`;
`Lumberjacks/docs/network/aoi-knee-experiment-brief.md` (its capture-time rejection rule);
`retro/SESSION-RETRO-2026-07-21.md` addendum B, lessons `L-2026-07-21-10` and `-11`.
