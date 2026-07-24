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

## Assumptions that remain open

- Native Valheim may still omit or pre-filter candidates before the Lumberjacks policy sees them.
- The live smooth/glide symptom could be send cadence, packet ordering, interpolation, or a combination; E03 does not select the cause.
- Pure fan-out does not include real queue age, ACK, reconnect, or transport behavior.

## What is justified next

1. Add a Gateway driver for E02 and E03 with explicit transport-path labels.
2. Reuse the existing synthetic motion smoke as a second receipt-producing seam.
3. Capture native candidate observations on a disposable local Valheim server before changing relevance ownership.
4. Keep Unity automation out of the next slice until Gateway receipts and the native capture fields are named.

## What should not be built yet

No database, permanent observer service, production authority switch, formal percentile
engine, full OTel stack, or generic arbitrary-script bridge is justified by E00-E03.

## Human gate

No human input was required. The next human request should be one precomputed visual
comparison only after the Gateway/local shadow packet predicts exactly what it is
trying to distinguish.
