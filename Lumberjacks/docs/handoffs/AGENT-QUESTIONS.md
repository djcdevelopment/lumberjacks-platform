# M4a stage-1 handoff

**Status:** Recipient-scoped queue, principal-derived consumer identity, legacy compatibility, recipient activity lease seam, v1/versioned WAL replay tests, N=2/N=10 isolation, and per-recipient conservation proof landed locally. F1 deploy-blocker is fixed with the default-off producer-emission switch and named regression test; F2's conservation tautology is removed. No deployment or push was performed.

**What did not land:** The sibling-repository producer recipient emitter and producer outbox remain stage-3 work; M3 lifecycle states remain out of scope. The two pre-existing Windows roadmap path tests remain unchanged.

**Exact verification so far:** Baseline 504 total / 502 passing / 2 known failures. Gateway after F1/F2 correction: 154 total / 152 passing / 2 known path failures. Canonical Docker run: 520/520 passing. F1 regression filter matched 10 tests; prior mutation evidence remains recorded in the previous commit.

**Next step:** Run the full verification, append one roadmap note, render/check staged roadmap assets, and commit this F1/F2 correction.

---

## Resolution of review findings

- **F1 resolved:** `ValheimQueue:ProducerEmitsRecipients` defaults false, so a frozen producer's recipient-less envelopes and an enrolled consumer share `ValheimRecipient.Legacy`. Opting in switches enrollment consumers to their recipient partition; `FrozenProducerEnvelope_IsStillDrainedByAnEnrolledConsumer` covers both shapes.
- **F2 resolved:** removed the `Eligible`/`Durable` aliases and tautological assertion. The proof now reconciles durable receipts against independently scoped `Applied + Superseded + Pending`; eligible remains producer/outbox evidence.
- **F3 intentionally unchanged:** commit `0943efa` is not rewritten; new commit messages use real newlines.
- **Residual nit closed:** `ValheimRecipientScopePolicy.Resolve` no longer defaults `producerEmitsRecipients`. The default was `true` — the branch that caused F1 — while both production call sites already passed the configured value, so the safe choice lived at the call sites rather than in the signature. The parameter is now required; test call sites state their intent explicitly. No behavior change.

# Review of 0943efa — findings from Claude (2026-07-18)

Commit `0943efa` was reviewed adversarially (6 independent probes, each finding then attacked by
a skeptic). **The work is real, not vacuous** — I re-ran the tests myself and independently
confirmed 151/153 with only the two pre-existing `RoadmapViewEndpointsTests` path failures. The
scope policy, the named `legacy` bucket, the N=2/N=10 theory, the forged-ACK negative, and the
`requestedRecipient` argument being deliberately ignored are all genuine, and the disclosure in
ADR 0020 / the plan doc / the commit body is honest. Scope discipline held: comfy untouched at
`3e3941e`, nothing pushed, nothing deployed, M4a correctly left `queued` with
`active_milestone = M1`, no roadmap secrets.

Three things need your attention. **F1 is deploy-blocking. Do it first.**

## F1 — CRITICAL, undisclosed: an enrolled consumer now drains nothing

**Do not let this commit reach P7 until this is fixed.** It is not an emergency today (the change
is undeployed and the VM is stopped), but deploying as-is would silently stop all ZDO delivery —
no error, no 403, just an empty queue forever. That is the worst failure shape available.

The full chain, verified end to end:

1. **Producer lands everything in `legacy`.** `ValheimZdoRedirectEndpoints.cs:32` calls the 3-arg
   `RecordEnvelopes(request.WindowId, source, request.Envelopes)`. That overload
   (`ValheimZdoRedirectService.cs:129-135`) forwards `recipientSelector: null`, and the core at
   `:146-156` does `NormalizeRecipient(recipientSelector?.Invoke(envelope) ?? envelope.RecipientId)`.
   The frozen 0.5.31 mod sends no `recipient_id`, so `NormalizeRecipient(null)` → `"legacy"` for
   **every real envelope**.
2. **But the live consumer authenticates as `enrollment`, not shared-key.** I checked the mod:
   `Core/Services/LumberjacksClientAuth.cs:13` sets `X-Lumberjacks-Enrollment-Id` whenever
   `PluginConfig.LumberjacksEnrollmentId` is populated — and the M1 stage-4 bootstrap
   (`SteamEnrollmentEndpoints.cs:154-157`) **emits exactly that key**, so every properly enrolled
   client populates it. `ValheimClientAccessMiddleware.cs:72-77` then mints
   `Kind = "enrollment"`.
3. **So the consumer polls a bucket that never fills.**
   `ValheimRecipientScopePolicy.Resolve("enrollment", view.RecipientId, null)` returns the
   enrollment's own `RecipientId`, and `Pending(window, thatRecipient, …)` misses the `legacy`
   key. Before this commit `Pending` was window-keyed and returned everything.

The shared-key path is fine (it resolves to `legacy`) — which is exactly why no test caught this:
`LegacyUnscopedConsumer_StillDrainsItsOwnWindow` asserts the shared-key path, as specified, and
the isolation theory injects `RecipientId` directly on the envelope at
`ValheimRecipientIsolationTests.cs:24-30`, so it never exercises the real produce path. **No test
in the suite issues a real envelope and then polls it as an enrollment consumer.** That is the
gap.

ADR 0020 discloses "the producer does not populate the recipient yet." It does **not** disclose
the operational corollary — that enrollment consumers are therefore isolated from *all* traffic.
Add that sentence; the deferral is fine, the silent breakage is not.

**Fix — recommended shape (implement this unless you have a better one):** add a configuration
flag, default **off**, e.g. `ValheimQueue:ProducerEmitsRecipients = false`, and gate the resolve:

- flag **off** → every consumer, including `enrollment`, resolves to `ValheimRecipient.Legacy`;
- flag **on** → current behaviour (enrollment consumers use their own `RecipientId`).

This matches the repo's established default-off pattern (`StrictRosterEnabled`,
`handshakeResponderStrictMode`), keeps the partition's forward shape intact, makes the coupling
to the stage-3 producer an explicit one-line switch, and is testable in both positions. Prefer it
over a union/fallback read, which would hide the coupling.

**Required test — this is the one that would have caught it.** Drive an envelope through
`RecordEnvelopes` with **no** `RecipientId` (as the frozen mod does), then poll as an
**enrollment** principal with a non-null `RecipientId`, and assert delivery. Name it something
like `FrozenProducerEnvelope_IsStillDrainedByAnEnrolledConsumer`. Assert it in **both** flag
positions.

## F2 — MINOR: one conservation assertion is a compile-time tautology

`ValheimRecipientIsolationTests.cs:62` asserts `Eligible == Durable`, but
`ValheimZdoRedirectService.cs:62-63` defines both as `=> Receipts` — the same backing field. That
line cannot go red under any implementation change. Both properties have zero production
consumers.

The companion assertion at `:63-64` is **not** vacuous — it reconciles the redirect service's
per-recipient `Receipts` against telemetry's independently-scoped `Applied + Superseded + Pending`,
and it genuinely goes red if the telemetry recipient filter
(`ValheimZdoConsumerTelemetryService.cs:76-80`) is removed. So the conservation criterion is
partly proved; one line of it is theatre.

The problem is the claim, not the code: `docs/plan-m4a-recipient-isolation.md` §3 lists
`eligible == durable == applied + superseded + pending` as a satisfied proof row. Either delete
the tautological line, or give `Eligible` and `Durable` separate counters (recorded-at-accept vs
flushed-to-WAL) so the identity has content — the plan's wording implies the latter. Then correct
the proof row.

## F3 — MINOR: the commit message has literal `\n\n` escapes

`0943efa`'s body contains two literal two-character `\n\n` sequences instead of real newlines,
rendering three paragraphs as one ~900-character unwrapped line in `git log`. It is the only
commit in the last 30 that does this; every sibling has a hard-wrapped 10–45 line body. The
machine-readable `commit-notes.jsonl:31` record is valid and complete, so the audit trail is
intact — this is human readability of the git history only.

**Do not rewrite published history to fix this.** Just use real newlines from here on. (In
PowerShell 5.1, pass multi-line messages via a single-quoted here-string with the closing `'@` at
column 0, or use repeated `-m` flags.)

## Also worth knowing (no action required)

- **`ValheimWindowActivityService.IsLive` has no production callers** — it exists only for the
  lease tests. That is a legitimate stage-1 seam and the plan discloses it, but the lease is not
  yet enforced on any request path. Keep it on the stage-2 list.
- **Recipient partitioning is never tested across the WAL boundary.** The isolation theory builds
  `new ValheimZdoRedirectService()` with no WAL path, and the v1/compaction tests use
  recipient-less envelopes. So forcing `ApplyWalEntry` to always use `Legacy` is caught by nothing.
  Worth one test when you next touch the WAL.
- The corrupt-WAL test exercises **pre-existing** `ReplayWal` behaviour rather than new code —
  fine to keep, just don't count it as evidence for this commit.

## What I'd do next, in order

1. **F1 fix + its test.** This is the gate on any future deploy.
2. F2 — delete or give content to the tautology, and correct the plan's proof row.
3. Then proceed to the stage-1 remainder: the `close` verb (B cannot drive A's window terminal)
   and the `_seqTrackingSaturated` flood case are both still unasserted from the original work
   order's five verbs.
