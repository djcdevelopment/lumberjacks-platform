# CRE-E02 - Does the real Gateway route only work selected by the envelope?

Status: supported for selected presentation routing

## Goal

Connect the deterministic runtime-envelope policy to the real Gateway presentation
transport without Unity, Steam, P7, or a gameplay-authority change.

## Objective

Run CRE-E01's green, amber, and red decisions through
`UdpTransport.HandleValheimMotionFrameAsync` in WebSocket-fallback mode and through the
bound UDP listener in UDP mode.

## Hypothesis

Only presentation work selected as `full` or `reduced` reaches transport. Deferred and
dropped work produces no frame. Both Gateway paths preserve delivery and logical
sequence for the nine selected presentation decisions.

## Predicted outcome

- 38 gate rows and nine route-observation rows per driver;
- nine WebSocket-fallback deliveries in the `gateway` run;
- nine distinct-target UDP deliveries plus one UDP bind frame in the `gateway_udp`
  run;
- no route row for the 23 deferred or dropped presentation decisions;
- monotonically increasing binary-envelope sequence;
- no claim that the motion transport proves critical world-mutation durability.

## Limits

In-memory Gateway sessions, loopback UDP, two synthetic participants, no packet-loss
injection, no frame-time measurement, no Unity apply, and no critical-state carriage.

## Assumptions

The production `UdpTransport` seams are representative of routing and fallback
behavior even though the synthetic payload is a Valheim player-motion frame.

## Known limitations and ADRs

CRE-E01 declares critical work as reliable. CRE-E02 deliberately does not map death,
hit, build, or inventory semantics onto a motion frame. Those require their own
reliable ordered-carriage experiment.

## Setup and procedure

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e02-gateway-pressure-route `
  -Driver gateway

.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e02-gateway-pressure-route `
  -Driver gateway_udp
```

Check both receipts and compare selected work IDs against route-observation work IDs.

## Results

Both Gateway drivers completed with supported, receipt-checked results. Each run
retained 38 `performance.gate_decision` rows and nine
`transport.route_observed` rows.

| Driver | Selected | Delivered | Suppressed from transport | Receive telemetry | Relay telemetry |
|---|---:|---:|---:|---:|---:|
| WebSocket fallback | 9 | 9 | 23 | 9 | 9 |
| Bound UDP | 9 | 9 | 23 | 10 | 9 |

The bound-UDP receive count includes the one target endpoint-bind frame. Both paths
delivered pressure-band selections `green=4`, `amber=4`, and `red=1`. Binary envelope
sequences were exactly `1..9` in both runs.

All four invariants passed in both drivers:

- observed route work IDs exactly matched selected presentation work IDs;
- deferred and dropped work IDs did not appear in transport;
- delivery telemetry matched the expected route count;
- sequence remained monotonic;
- the result did not label critical mutation carriage as proven.

Evidence:

- `runs/gateway-20260724T141237Z/receipt.json`
- `runs/gateway-20260724T141237Z/raw/events.jsonl`
- `runs/gateway_udp-20260724T141258Z/receipt.json`
- `runs/gateway_udp-20260724T141258Z/raw/events.jsonl`

## What changed in our understanding

The policy-to-transport seam is now concrete. A gate decision can become a real
Gateway motion relay without Unity, and suppressed work can be proven absent by
comparing work IDs rather than inferring from aggregate packet counts.

The two transport paths preserve the same selected work set and sequence under this
clean fixture. The remaining transport unknown is behavior under duplicate, reorder,
loss, and reconnect pressure. The reliable-state unknown is deliberately separate.

## Next experiment

Add duplicate, reorder, bounded-loss, and reconnect fixtures. In parallel, replace
synthetic costs with CRE-0 patch-load measurements before selecting a live Valheim
presentation seam.
