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

Pure driver, 20 samples per pattern, no Unity interpolation, no UDP/WebSocket path,
and no claim that velocity prediction is the live defect.

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

Supported. All six patterns appeared in 120 rows. Fingerprint correction totals were
straight=1, stutter=13, stop/start=2, turn=2.414, circle=9.204, and teleport=56.286.

## What changed in our understanding

The lab can produce a useful prediction grid for the live motion symptom. The next
seam must preserve logical ordering while adding Gateway transport observations.

## Next experiment

Run the existing motion smoke and a Gateway driver through the same scenario before
using local Valheim clients.
