# Decisions pending — Lumberjacks platform

This is the platform's open decision queue. An item belongs here only when at
least two currently viable alternatives have materially different consequences,
an owner is named, and a deadline or trigger is explicit. Tasks, established
policy, historical evidence, and blocked prerequisites do not belong here.

## Open

- [ ] 2026-08-12 — **Choose C9's sufficient motion-artifact path.** Either
  repair and re-shoot the unattended driven-motion capture (repeatable evidence,
  more engineering), or book a live/manual two-client drive and capture (less
  tooling, requires Derek and both clients). Owner: Derek. Trigger: before
  recording C9's subjective verdict or claiming final cutover acceptance.
  (source: [final-cutover plan](fieldlab/plan-native-network-final-cutover.md),
  [motion-capture runbook](fieldlab/docs/runbook-motion-phase-capture.md))

- [ ] 2026-08-12 — **Choose the ordinary-play cutover arming posture.** Before
  the next ordinary (non-harness) two-client play window, choose either to make
  `Enable-LabSessionConfig` add and receipt missing cutover keys, or to ship an
  explicitly armed personalized pack. The first preserves safe defaults but every
  ordinary-play session needs an arming step; the second makes ordinary launch
  work but leaves activation persistent. Owner: Derek. Trigger: before the next
  non-harness two-client or alpha session. (source: [native client harness](fieldlab/scripts/Invoke-NativeValheimClient.ps1),
  [native-cutover runbook](fieldlab/docs/runbook-native-cutover-scenario.md),
  [ADR 0017](fieldlab/docs/adr/0017-prove-the-lane-users-ship-on.md))

- [ ] 2026-08-12 — **Choose when to spend the bounded P7 C10b finalization
  window.** Either authorize the next window for boot-determinism capture, paired
  candidate promotion/proof, fallback removal, and final proof, or keep P7 stopped
  and leave C10b/final cutover explicitly deferred. Owner: Derek. Trigger: before
  any P7 power/deployment action or any claim that final cutover is complete.
  (source: [final-cutover plan](fieldlab/plan-native-network-final-cutover.md),
  [boot verification](infra/gcp/p7/RUNBOOK-boot-determinism.md),
  [C10b tools](tools/p7/README.md))

- [ ] 2026-08-12 — **Choose the Phase 4 co-presence partition strategy if live
  measurements justify reopening it.** Retain per-observer fan-out (independent
  acknowledgements and band-shaping, N× WAL cost) or adopt region-shared
  partitions (less amplification, weaker per-observer semantics). Owner: Derek.
  Trigger: only after the default-config reproduction and Phase 2 live test
  produce measured WAL amplification. Until then, ADR 0013's default-off,
  reproduce-first path controls. (source: [ADR 0013](fieldlab/docs/adr/0013-ownership-visibility-split.md),
  [live-test runbook](fieldlab/docs/runbook-copresence-fanout-live-test.md))

## Classified out during transfer

The 2026-08-12 ownership transfer deliberately did not import the legacy
`fieldlab/DECISIONS-PENDING.md` wholesale. Its headless Companion route, P7 mode
restoration, Quest trigger, enrollment tooling, and backup/swarm items are defects,
execution steps, cross-repository work, or pinned backlog—not open decisions under
this register's policy. The legacy claim that `isolate` had no remote is stale:
`isolate` is now a sovereign repository, so it creates no platform decision.
Historical rationale and closed entries remain evidence in `baseline`.
