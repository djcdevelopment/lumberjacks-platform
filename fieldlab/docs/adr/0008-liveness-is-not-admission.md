# ADR 0008 — Record heartbeat liveness before admission; keep the primary gate strict

- **Status:** Accepted (2026-07-21)
- **Rung:** M1 / telemetry; binds the `lumberjacks-primary` admission path and anything reading `/api/v0/telemetry/cutover`

## Context

`POST /valheim/telemetry/heartbeat` rejects a `lumberjacks-primary` beat with 409 unless the
authoritative window is fully applied and acknowledged and traffic coverage is total —
`CanAcceptPrimaryHeartbeat` → `IsAuthoritativeComplete`, which demands `redirect.Pending == 0`,
`consumer.Pending == 0`, `consumer.Rejected == 0` and one active consumer. Dashboard staleness
trips at 15 seconds without an accepted beat.

The endpoint returned the 409 *before* calling `Record`, so a rejected beat never advanced
`_lastSeen`. Under sustained load with peers connected — exactly when a real session is busiest —
every beat is rejected, the 15s clock runs out, and the dashboard goes to `stale`.

What actually degraded was narrower and stranger than "the dashboard goes blind". `CutoverSnapshot`
reads its queue counters live from `ValheimZdoRedirectService` / `ValheimZdoConsumerTelemetryService`,
so `pending`, `active_consumers` and `consumer_draining` kept updating. Everything sourced from
`_latest` — `coverage_total`, `coverage_lumberjacks`, `coverage_native_only`, `mode`, `mod_version` —
froze at its last admitted value while the headline read `stale`. The coverage figures that *prove
the cutover is working* were the ones that stopped moving, next to queue counters that visibly did
not. The operator was shown a contradiction.

The question was whether the gate should tolerate load-induced backlog. It should not: the
invariant is real. A `lumberjacks-primary` beat with a backlog is genuinely not a fully authoritative
window, and the 409 is the honest answer to "may I be treated as primary?".

## Decision

**Separate liveness from admission.** `ValheimTelemetryHeartbeatService.RecordAndAdmit` records the
beat and then answers admissibility; the endpoint calls that one method instead of ordering two calls
itself. The 409 and the gate's conditions are unchanged.

A rejected heartbeat is still proof that the server is alive and reporting. That is liveness, and it
is a different question from admission. The gate answers "is this window fully applied?"; `_lastSeen`
answers "is anyone home?". Conflating them meant a correct rejection silently destroyed unrelated
information.

The ordering lives in a named service method rather than as line order inside the endpoint lambda,
because line order in a lambda is precisely what regressed here and nothing could have failed to
notice.

Malformed beats are still never recorded: the endpoint's 400 checks on `instance_id`, `mod_version`,
`timestamp_utc` and `cutover_mode` run ahead of admission, as does the telemetry-key check.

## Consequences

- **The dashboard can now say "fresh but draining".** `CutoverSnapshot` recomputes
  `IsAuthoritativeComplete` per request, so recording a rejected beat cannot make it claim
  completeness — `complete` stays `false` and `consumer_draining` stays `true` while `stale` goes
  `false`. The two community pages already preferred `consumer_draining` over the stale headline
  (`community.html`, `networksense.html`), so they were written expecting a state the gate had made
  unreachable.
- **`EnrollmentSnapshot.state` now reads `advertised` rather than `stale` during backlog.** Checked
  before landing: nothing gates on that value — the guest package preflight reads
  `lumberjacksEnrollmentId` from its bootstrap body, not this field. `coverage_gate.complete` in the
  same payload remains computed from the live beat, so the honest signal is still there.
- **No client-side effect.** The mod's `LumberjacksTelemetryHeartbeatRunner.Post` is fire-and-forget
  and only logs on throw, so the unchanged 409 changes no mod behaviour. The mod-side log noise it
  produces is a separate queued fix.
- **This generalizes a fix already made once, narrowly.** `CanAcceptPrimaryHeartbeat` already carves
  out `PeerCount == 0` with the comment that rejecting every beat after a restart "makes the
  dashboard stale until a player joins" — the same bug, patched for the empty-server case only. The
  carve-out stays, but it is no longer the only thing standing between a correct rejection and a
  blind dashboard.
- **Regression-proved, not just asserted.** `RejectedPrimaryHeartbeatStillRefreshesLiveness` was run
  against the old gate-then-record order and fails there (`stale` was `True`).

## Related

`Lumberjacks/src/Game.Gateway/Valheim/ValheimTelemetryHeartbeatService.cs` (`RecordAndAdmit`);
`Lumberjacks/src/Game.Gateway/Valheim/ValheimTelemetryHeartbeatEndpoints.cs`;
`Lumberjacks/tests/Game.Gateway.Tests/ValheimZdoAuthoritativeTelemetryTests.cs`;
`retro/SESSION-RETRO-2026-07-21.md` lesson `L-2026-07-21-2`; `../../DECISIONS-PENDING.md`.
