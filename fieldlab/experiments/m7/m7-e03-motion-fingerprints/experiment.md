# M7-E03 - What fingerprint separates motion patterns?

Status: analyzed

## Goal

Create repeatable motion traces that can later separate send cadence, ordering, and
interpolation hypotheses before asking for a side-by-side human judgment.

## Objective

Generate straight, stutter, stop/start, turn, circle, and teleport trajectories at a
fixed 50 ms sample cadence.

## Hypothesis

The patterns produce distinguishable cadence, lag, correction, and predicted-motion
fingerprints, even though pure data cannot identify the live Valheim presentation
cause.

## Predicted outcome

All six patterns appear. Stutter has larger output intervals and sequence lag; the
teleport has the largest correction; the traces remain logically ordered.

## Limits

Pure and Gateway drivers use 20 samples per pattern and no Unity interpolation. The
Gateway runs exercise WebSocket fallback and bound UDP loopback, but make no claim
that velocity prediction is the live defect.

## Assumptions

The existing observed motion patterns are useful synthetic probes for the next
Gateway and local-client drivers.

## Known limitations and ADRs

The predictor is intentionally simple. E03 identifies observations needed to test
cadence versus interpolation; it does not choose the production equation.

## Setup and procedure

Run the E03 scenario, check the receipt, and inspect the raw event rows grouped by
trajectory.

## Results

The pure, Gateway WebSocket, and Gateway UDP runs are all supported. All six patterns
appeared in 120 rows in each run. Fingerprint correction totals were straight=1,
stutter=13, stop/start=2, turn=2.414, circle=9.204, and teleport=56.286. The Gateway
WebSocket fallback relayed all 120 frames in order, and the bound-UDP run delivered all
120 frames to the distinct target with the target token and envelope order intact.

## What changed in our understanding

The lab can produce a useful prediction grid for the live motion symptom, and both
Gateway transport paths preserve the same logical fingerprints. The live unknown
remains whether cadence, ordering, or client interpolation dominates.

## Next experiment

Replay the existing motion smoke through the same receipt contract, then capture the
native candidate fields on a disposable local Valheim server before using strict
authority or changing client equations.
