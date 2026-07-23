# Wave 0 non-human gates

These commands reduce live two-client testing to the observations only Derek can
provide. They do not replace the final apply/observe gate.

```powershell
# Gateway motion relay semantics, no Valheim clients required
tools\wave0\Test-Wave0SyntheticMotion.ps1 -OutputJson captures\wave0-synthetic-motion.json

# Runtime release/readiness alignment across P7, OMEN, and i5
tools\i5\Test-Wave0Readiness.ps1 -SummaryOnly -OutputJson captures\wave0-readiness.json

# Full live-gate orchestrator; exits WAIT until both clients are joined
tools\wave0\Start-Wave0LiveGate.ps1 -OutputJson captures\wave0-live-gate\result.json

# Attach Derek's visual observation without editing the machine receipt
tools\wave0\Add-Wave0VisualObservation.ps1 `
  -ReceiptJson captures\wave0-live-gate\result.json `
  -ApplyClient omen `
  -ObserveClient i5 `
  -VisualResult followed_role `
  -StraightMovement smooth `
  -StutterMovement mixed `
  -RoleReversalRun no

# Generate the concise return packet from current receipts
tools\wave0\New-Wave0ReturnPacket.ps1 `
  -OutputJson captures\wave0-return-packet.json `
  -OutputMarkdown captures\wave0-return-packet.md
```

Run order before asking for a live movement course:

1. `Test-Wave0SyntheticMotion.ps1`
2. `Test-Wave0Readiness.ps1`
3. two-client idle capture
4. one bounded apply/observe course
5. role reversal

If either non-human gate reports `FAIL` or `WAIT`, stop and use its receipt
instead of repeating a live join/movement test. `WARN` is advisory: for example,
after a new release it is normal to have no retained capture with the newest
heartbeat-age fields until the next real two-client run creates one.

`Start-Wave0LiveGate.ps1` is the preferred live command once both clients are
joined. It runs the two non-human gates, checks P7 peer count, starts the
two-machine capture, sends one bounded Companion motion command, and writes a
single receipt. If fewer than two peers are visible, it writes `wait_for_two_real_clients`
and does not move either character.

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

`New-Wave0ReturnPacket.ps1` is the handoff generator. It summarizes the current
non-human receipts, emits the commands to run when both clients are back, and
lists the stop conditions without copying raw private Companion receipt bodies
into the Markdown.
