# M7 synthesis 001 - synthetic authority lab

Date: 2026-07-24

## Decision

E00-E03 are `supported` for the pure driver. The lab is deterministic enough to
continue into Gateway and replay work, but none of these runs authorize P7 authority
promotion.

## Established correlations

- E00: the normalized decision stream is stable for a fixed scenario and seed; malformed input and bounded timeout are visible outcomes.
- E01: the linked tiered policy has the predicted near/mid/far boundary shape and emitted decisions increase with fixture density.
- E02: the pure fan-out seam keeps recipient decisions independent and scales in the expected direction across N=2/N=10/N=100.
- E03: motion patterns produce repeatable fingerprints, including a distinct stutter cadence and large teleport correction.
- Gateway E02: the real `ValheimZdoRedirectService` keeps pending and ACK state partitioned by recipient and makes duplicate batches observable without double application.
- Gateway durable E02: WAL replay reconstructs both recipient partitions after a service restart, and ACK state remains terminal after a second restart.
- Gateway E03: the real `UdpTransport` WebSocket fallback relays all 120 frames and preserves envelope ordering without changing the synthetic fingerprints.
- Gateway UDP E03: the real bound UDP listener delivers all 120 frames to the distinct target with token and ordering invariants intact; the synthetic fingerprints remain unchanged.

## Assumptions that remain open

- Native Valheim may still omit or pre-filter candidates before the Lumberjacks policy sees them.
- The live smooth/glide symptom could be send cadence, packet ordering, interpolation, or a combination; E03 does not select the cause.
- Pure fan-out does not include real queue age, ACK, reconnect, or transport behavior.

## What is justified next

1. Reuse the existing synthetic motion smoke as a second receipt-producing seam.
2. Capture native candidate observations on a disposable local Valheim server before changing relevance ownership.
3. Normalize and replay the native candidate trace through the existing receipt contract.
4. Keep Unity automation out of the next slice until the native capture fields are named.

## What should not be built yet

No database, permanent observer service, production authority switch, formal percentile
engine, full OTel stack, or generic arbitrary-script bridge is justified by E00-E03.

## Human gate

No human input was required. The next human request should be one precomputed visual
comparison only after the Gateway/local shadow packet predicts exactly what it is
trying to distinguish.
