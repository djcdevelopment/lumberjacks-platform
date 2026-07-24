# Wave 0 non-human gates

These commands reduce live two-client testing to the observations only Derek can
provide. They do not replace the final apply/observe gate.

```powershell
# Gateway motion relay semantics, no Valheim clients required
tools\wave0\Test-Wave0SyntheticMotion.ps1 -OutputJson captures\wave0-synthetic-motion.json

# Runtime release/readiness alignment across P7, OMEN, and i5
tools\i5\Test-Wave0Readiness.ps1 -SummaryOnly -OutputJson captures\wave0-readiness.json

# Public roadmap freshness against live P7 release truth
tools\wave0\Test-Wave0RoadmapFreshness.ps1 -OutputJson captures\wave0-roadmap-freshness.json

# Full live-gate orchestrator; exits WAIT until both clients are joined
tools\wave0\Start-Wave0LiveGate.ps1 -OutputJson captures\wave0-live-gate\result.json

# Start before/during the join; waits for two peers, then runs the live gate
tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient omen -OutputJson captures\wave0-live-gate\result.json

# One-command pre-live audit; no movement, stops at the real-client boundary
tools\wave0\Test-Wave0Prelive.ps1 -OutputDirectory captures\wave0-prelive-current

# Attach Derek's visual observation without editing the machine receipt
tools\wave0\Add-Wave0VisualObservation.ps1 `
  -ReceiptJson captures\wave0-live-gate\result.json `
  -ApplyClient omen `
  -ObserveClient i5 `
  -VisualResult followed_role `
  -StraightMovement smooth `
  -StutterMovement mixed `
  -RoleReversalRun no

# Seal both annotated directions into one visual-evidence index
tools\wave0\Seal-Wave0VisualEvidence.ps1 `
  -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json `
  -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json `
  -OutputJson captures\wave0-live-seal\visual-seal.json

# If visual proof cannot be sealed, retain a named defect packet instead
tools\wave0\New-Wave0DefectPacket.ps1 `
  -DefectId wave0-role-reversal-failed `
  -DefectKind role_reversal_failed `
  -Summary "Role reversal did not follow the selected apply/observe split." `
  -FirstReceiptJson captures\wave0-live-gate\result.json `
  -ReversalReceiptJson captures\wave0-live-gate-reversal\result.json `
  -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json `
  -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json `
  -SealJson captures\wave0-live-seal\visual-seal.json

# Generate the concise return packet from current receipts
tools\wave0\New-Wave0ReturnPacket.ps1 `
  -OutputJson captures\wave0-return-packet.json `
  -OutputMarkdown captures\wave0-return-packet.md
```

Run order before asking for a live movement course:

0. `Test-Wave0Prelive.ps1`
1. `Test-Wave0SyntheticMotion.ps1`
2. `Test-Wave0Readiness.ps1`
3. `Test-Wave0RoadmapFreshness.ps1`
4. two-client idle capture
5. one bounded apply/observe course, or start `Wait-Wave0LiveGate.ps1` before/during the join so
   the command fires only after P7 reports the required peer window
6. role reversal
7. seal the two annotated visual projections

If either non-human gate reports `FAIL` or `WAIT`, stop and use its receipt
instead of repeating a live join/movement test. `WARN` is advisory: for example,
after a new release it is normal to have no retained capture with the newest
heartbeat-age fields until the next real two-client run creates one.

`Test-Wave0Prelive.ps1` is the preferred unattended check before Derek returns.
It runs readiness, public-roadmap freshness, fixture coverage, a no-client
live-gate smoke, a two-machine bundle-lane smoke, and return-packet generation
into one summary receipt.

`Start-Wave0LiveGate.ps1` is the preferred live command once both clients are
joined. It runs the two non-human gates, checks P7 peer count, starts the
two-machine capture, sends one bounded Companion motion command, and writes a
single receipt. If fewer than two peers are visible, it writes `wait_for_two_real_clients`
and does not move either character.

`Wait-Wave0LiveGate.ps1` wraps the same command for low-touch runs. Start it before
or while the two clients are joining; it polls P7 Valheim telemetry until the
heartbeat is fresh/ready and `peer_count >= 2`, then delegates to
`Start-Wave0LiveGate.ps1`. If the peer window never appears, it writes a
`wait_for_two_real_clients_timeout` receipt and exits 0, so "not joined yet" is
kept separate from a failed gate.

When the live course runs, capture bundles from OMEN and i5 are collected by
default under `<receipt-dir>\bundles`. Pass `-BundleDirectory` only to override
that location.

After two peers are visible, the live gate sets the requested apply/observe
split through the bounded Companion command lane, then performs a five-second
role preflight before sending motion. By default OMEN is APPLY and i5 is
OBSERVE ONLY; pass `-DesiredApplyClient i5` for the reversal, or
`-DesiredApplyClient preserve` to verify the existing manual state without
changing it. The preflight reads each client's
`final_local_motion.apply_enabled` from Companion capture evidence and blocks
with `blocked_by_ambiguous_apply_roles` unless exactly one client is
apply-enabled.

Every live-gate run also writes an observation worksheet next to the receipt
(`*.observation.md` by default). That file is the operator-facing checklist:
apply client, observe client, visual result, straight/stutter movement quality,
role-reversal state, and ready-to-run annotation commands. Use it during the
live pass instead of reconstructing expected fields from chat history.

The role-preflight branch can be smoke-tested without live clients:

```powershell
# One-command fixture gate; writes receipts under captures/wave0-live-gate-fixtures
tools\wave0\Test-Wave0LiveGateFixtures.ps1

# Expected: blocked_by_ambiguous_apply_roles, no movement command
tools\wave0\Start-Wave0LiveGate.ps1 `
  -SkipSynthetic -SkipReadiness -DesiredApplyClient preserve `
  -MockValheimTelemetryJson tools\wave0\fixtures\valheim-two-peers.json `
  -MockRolePreflightJson tools\wave0\fixtures\role-preflight-both-apply.json `
  -OutputJson captures\wave0-mock-ambiguous-roles\result.json

# Expected: role_preflight_passed_stopped_before_motion, no movement command
tools\wave0\Start-Wave0LiveGate.ps1 `
  -SkipSynthetic -SkipReadiness -StopAfterRolePreflight -DesiredApplyClient preserve `
  -MockValheimTelemetryJson tools\wave0\fixtures\valheim-two-peers.json `
  -MockRolePreflightJson tools\wave0\fixtures\role-preflight-omen-apply.json `
  -OutputJson captures\wave0-mock-valid-roles\result.json
```

After the live course, use `Add-Wave0VisualObservation.ps1` instead of editing
the receipt. It writes a sidecar `*.visual-observation.json` and a derived
`*.annotated.json` projection while preserving the original machine receipt.
After both directions are annotated, use `Seal-Wave0VisualEvidence.ps1` to
verify that the visual evidence is complete, roles reversed, source receipts are
distinct, and both annotations point at allowed live-gate verdicts. The seal is
a derived index, not a replacement for the two immutable machine receipts.
If the seal fails or the visual result is inconclusive, use
`New-Wave0DefectPacket.ps1` to retain a named defect packet. That packet is the
allowed alternative Wave 0 handoff: it explains why visual proof could not be
sealed and indexes the failed receipts/annotations by SHA-256 without rewriting
them.
The seal verifier has fixture coverage:

```powershell
tools\wave0\Test-Wave0SealFixtures.ps1
tools\wave0\Test-Wave0DefectPacketFixtures.ps1
```

`New-Wave0ReturnPacket.ps1` is the handoff generator. It summarizes the current
non-human receipts, emits the commands to run when both clients are back, and
lists the stop conditions without copying raw private Companion receipt bodies
into the Markdown.
