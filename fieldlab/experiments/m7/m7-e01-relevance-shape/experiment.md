# M7-E01 - Does the relevance equation have the expected shape?

Status: analyzed

## Goal

Make the current tiered relevance behavior measurable at its boundaries before
comparing it with native Valheim candidates.

## Objective

Evaluate distances 29.9, 30.0, 30.1, 63.9, 64.0, and 64.1 at 1x, 2x, and 4x
density using the linked `ZdoBandPolicy` source.

## Hypothesis

Near and due-mid objects emit, far objects drop, and emitted decisions increase
monotonically as fixture density increases.

## Predicted outcome

The clean boundary sequence is `EmitFull, EmitFull, EmitThinned, EmitThinned,
EmitThinned, Drop`; density totals should be 5, 10, and 20.

## Limits

Pure driver, one observer, 42 events, fixed policy, no timing capacity gate, and no
claim about native candidate enumeration or visual quality.

## Assumptions

Distance-band behavior is the useful first oracle even though native Valheim still
chooses the candidate list upstream.

## Known limitations and ADRs

Boundary chatter is represented by the fixture shape but not yet driven by a noisy
trajectory. Decision duration is not a promotion gate.

## Setup and procedure

Run the E01 scenario in the SDK container, check the receipt, and inspect the raw
JSONL event stream and summary.

## Results

Supported. The run retained 42 decisions. Density response was 1x=5, 2x=10, 4x=20,
and the exact predicted boundary sequence passed.

## What changed in our understanding

The current pure policy has the expected monotonic shape. The remaining unknown is
not this boundary math; it is the native candidate set that feeds it.

## Next experiment

Run E02 against independent observers, then capture native candidates before trying
to replace selection.
