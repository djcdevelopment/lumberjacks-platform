# CRE-E03 - What does the Gateway do with motion sequence faults?

Status: supported after a topology-accounting refutation and refinement

## Goal

Discover the actual duplicate, reorder, loss-gap, wrap, and reconnect behavior of the
production Gateway motion seam before designing reliability or interpolation policy.

## Objective

Send bounded fault vectors through WebSocket fallback and bound UDP using the real
`GameSession.TryAcceptValheimMotion` and `UdpTransport` paths.

## Hypothesis

The per-session half-range sequence guard rejects duplicate and old motion, accepts a
fresh sequence after a missing transient sample, accepts `65535 -> 0` wrap, and resets
for a newly authenticated resumed session. A detached UDP token no longer maps to a
session.

## Predicted outcome

- sequences `100,101,102,104` relay while duplicate `101` and reordered `99` drop;
- the absent `103` does not block `104`;
- `65534,65535,0,1` relay while an old `65535` after wrap drops;
- sequence `1` relays after authenticated session resume;
- the detached UDP token produces no route or motion-counter change;
- each driver relays ten frames and reports three stale drops.

## Limits

Loopback/in-memory Gateway, deterministic ordering at the sender, no stochastic
network emulator, no latency distribution, no Unity apply, and no claim about visual
quality. Loss is represented by intentionally omitting one transient frame.

## Assumptions

Gateway receive/relay counters and captured target frames are independent enough to
classify each bounded attempt.

## Known limitations and ADRs

UDP does not become reliable because stale packets are rejected. The test verifies
freshness and sequence behavior only; state repair and critical mutations remain
separate reliable-carriage concerns.

## Setup and procedure

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e03-transport-faults `
  -Driver gateway

.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e03-transport-faults `
  -Driver gateway_udp
```

## Results

The frozen sequence prediction was supported by all four runs:

- duplicate `101`, reordered `99`, and old-after-wrap `65535` were stale;
- missing `103` did not block fresh `104`;
- `65534,65535,0,1` crossed the ushort boundary;
- a resumed authenticated session accepted sequence `1`;
- the detached UDP token was unknown and produced no motion relay.

The first runs correctly refuted the prediction that ten accepted frames produce ten
aggregate relay deliveries:

| Run | Classification | Accepted/setup receive | Primary target | Aggregate relay |
|---|---|---:|---:|---:|
| `gateway-20260724T141921Z` | refuted | 10 | not separated | 18 |
| `gateway_udp-20260724T142029Z` | refuted | 11, including endpoint-bind setup | not separated | 18 |

The Gateway fans each accepted frame to every other eligible session in the region.
As source sessions accumulated, the ten accepted source frames produced
`4x1 + 4x2 + 1x3 + 1x3 = 18` deliveries. The UDP setup packet also legitimately
incremented the receive counter before the experiment window.

The follow-up retained the refuted receipts, established a post-setup measurement
baseline, and separated source acceptance, one observer's deliveries, and aggregate
regional fanout:

| Run | Classification | Accepted source | Primary target | Aggregate relay | Stale |
|---|---|---:|---:|---:|---:|
| `gateway-20260725T024205Z` | supported | 10 | 10 | 18 | 3 |
| `gateway_udp-20260725T024355Z` | supported | 10 | 10 | 18 | 3 |

All receipt checks passed. This is loopback/in-memory Gateway evidence, not a visual
quality or live-authority claim.

## What changed in our understanding

An accepted source frame and a relay delivery are different cost units. Runtime
pressure cannot be budgeted only by patch calls or selected source events: route cost
also grows with the eligible recipient topology. Setup/control traffic must remain
visible but outside the measured gameplay window.

The existing half-range freshness guard is suitable for transient presentation motion
under the bounded cases tested. It intentionally does not repair loss, retain state
across authenticated session replacement, or make critical mutations reliable.

## Next experiment

Use a fanout-aware burst fixture to compare direct apply, latest-wins coalescing, and
a small expiry window before selecting any live Valheim presentation consumer.
