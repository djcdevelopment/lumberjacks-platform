# M4a stage 1 — recipient-scoped durable delivery

2026-07-18. This is a Gateway-only, undeployed implementation plan for the first
M4a proof: authenticated consumers cannot observe or mutate another recipient's
durable queue, and legacy shared-key delivery remains usable while the producer
mod still emits no recipient.

## 1. Fixed decisions and boundary

- `ValheimRecipient.Legacy` is the explicit bucket for absent producer identity,
  private-plane callers, and shared-client-key callers.
- Enrollment callers use the server-derived `Enrollment.RecipientId` only. A
  blank enrollment recipient is a fail-closed inconsistency (403); it is not
  treated as legacy. Query/body labels never select a recipient.
- The producer-side recipient field is additive. This commit builds the
  partition, but the frozen 0.5.31 producer does not populate it; the per-peer
  mod adapter belongs to stage 3 in `C:\work\comfy` and is untouched here.
- `ValheimQueue:ProducerEmitsRecipients` defaults off. Until the producer cut is
  enabled, enrollment consumers intentionally resolve to `Legacy`; enabling
  recipient scoping before producer emission would otherwise create an empty
  enrollment partition.
- M3's OPEN → CLOSING → SEALED lifecycle and the producer outbox remain outside
  this stage. M4a criterion 3 therefore remains open.
- M4a stays `queued`; `active_milestone` remains `M1`. This work is recorded as
  evidence, not as a milestone-status claim.

## 2. Implementation stages

1. Capture the dotnet9 build/test baseline and confirm the worktree before edits.
2. Add this plan and ADR 0020 before changing queue code.
3. Add the pure recipient scope policy and direct tests, including the shared-key
   `LegacyUnscopedConsumer_StillDrainsItsOwnWindow` regression.
4. Add the optional producer recipient field and partition redirect state,
   pending, inspect/status, ACK, reset, sequence tracking, and counters by
   `(window_id, recipient_id)`, retaining compatible aggregate projections.
5. Derive injection and redirect consumer scope from `ValheimPrincipal`; do not
   introduce WAL to the injection service.
6. Key activity/readiness by recipient, inject `Func<DateTime>`, and configure a
   recipient lease beside `SeatLeaseSeconds`. Prove reconnect, expiry, takeover,
   reset, and legacy compatibility.
7. Add the N=2/N=10 service-level isolation theory covering poll, inspect,
   acknowledge, block, and close, plus conservation counters.
8. Add a hand-built recipient-less v1 WAL fixture test before changing the WAL;
   then add the optional schema version, legacy replay, restart/compaction
   convergence, and complete-record JSON corruption test.
9. Run the three named mutations and record literal failing test names/counts.
10. Run the Gateway, solution, filtered, repeated-WAL, and Docker verification;
    update roadmap evidence, ideas, and handoff status; commit with the roadmap
    ritual.

## 3. Proof matrix

| Criterion | Proof | Required result |
| --- | --- | --- |
| Recipient isolation | `ValheimRecipientIsolation` theory at N=2 and N=10 | Five verbs pass with consumer principals only; no cross-recipient sequence saturation or terminal close |
| Durable idempotence | Duplicate POST, poll, terminal ACK, reconnect, takeover, restart, replay | Same recipient state after the second run; no duplicate application |
| WAL compatibility | Hand-built v1 fixture, versioned v2 replay, full-length invalid JSON | v1 maps to `Legacy` without `InvalidDataException`; invalid complete records fail explicitly; physical tails retain repair behavior |
| Conservation | Per-recipient durable receipts plus independently scoped terminal telemetry | `durable receipts == applied + superseded + pending`, with zero leakage; the eligible denominator remains producer/outbox evidence and is not claimed by this Gateway-only stage |
| Mutation proof | Scope predicate, lease expiry, WAL version branch | Each mutation fails a non-zero, named test set; restored code returns to baseline green except the two known failures |

## 4. Risks and cut line

- Do not default a missing recipient to `window_id`; that reproduces the current
  bug while making the tests pass.
- Do not make `private-plane` or `shared-client-key` fail closed for missing
  enrollment; their lack of enrollment is intentional.
- Do not silently truncate a complete-length corrupt WAL record.
- Do not fix the two pre-existing roadmap path tests.

The preferred result is one coherent commit. If the session must split, commit
one contains recipient policy, scoped state, endpoint identity, lease, isolation,
conservation, and the scope/lease mutation evidence. Commit two contains the
versioned WAL and convergence matrix. Each commit appends exactly one roadmap
journal note and regenerates/checks the public roadmap HTML.

## 5. Evidence placeholders

- Baseline: 504 total / 502 passing / 2 known failures.
- Scope mutation: 4 failures — `ValheimRecipientScopePolicyTests.EnrollmentUsesServerRecipientAndIgnoresRequestedLabel`, `ValheimRecipientScopePolicyTests.EnrollmentWithoutRecipientFailsClosed`, and both N=2/N=10 `ValheimRecipientIsolationTests.ValheimRecipientIsolation` cases.
- Lease mutation: 2 failures — `ValheimRecipientLeaseTests.LeaseIsScopedAndExpiresWithoutSleeping` and `ValheimRecipientLeaseTests.RecipientReconnectRefreshesOnlyItsOwnLeaseAndTakeoverFollowsExpiry`.
- WAL-version mutation: 1 failure — `ValheimZdoAuthoritativeTelemetryTests.RecipientLessV1WalFixtureReplaysIntoLegacyBucket`.
- Canonical Docker result: `520/520` passing in the `mcr.microsoft.com/dotnet/sdk:9.0` container; Windows Gateway run is `154 total / 152 passing / 2 known path failures`.
- F1 regression: `FrozenProducerEnvelope_IsStillDrainedByAnEnrolledConsumer`
  proves frozen no-recipient delivery in legacy mode and recipient-emitting
  delivery in opt-in mode.
