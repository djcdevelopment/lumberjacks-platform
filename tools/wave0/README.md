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

If either non-human gate fails, stop and use its receipt instead of repeating a
live join/movement test.

`Start-Wave0LiveGate.ps1` is the preferred live command once both clients are
joined. It runs the two non-human gates, checks P7 peer count, starts the
two-machine capture, sends one bounded Companion motion command, and writes a
single receipt. If fewer than two peers are visible, it writes `wait_for_two_real_clients`
and does not move either character.

After the live course, use `Add-Wave0VisualObservation.ps1` instead of editing
the receipt. It writes a sidecar `*.visual-observation.json` and a derived
`*.annotated.json` projection while preserving the original machine receipt.

`New-Wave0ReturnPacket.ps1` is the handoff generator. It summarizes the current
non-human receipts, emits the commands to run when both clients are back, and
lists the stop conditions without copying raw private Companion receipt bodies
into the Markdown.
