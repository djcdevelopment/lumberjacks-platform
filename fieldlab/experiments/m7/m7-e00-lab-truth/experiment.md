# M7-E00 - Can the lab tell the truth?

Status: analyzed

## Goal

Prove that the lab can preserve deterministic decisions and bounded failure before
any Gateway or Valheim process is introduced.

## Objective

Run one seeded scenario twice, then exercise malformed input and a forced timeout.

## Hypothesis

Identical normalized inputs produce identical normalized decisions; malformed input
is rejected before execution; a timeout leaves a named, checkable receipt.

## Predicted outcome

Two valid runs have equal normalized input and decision hashes. The malformed
scenario exits with code 2 and writes a rejection record. The forced run is retained
as `inconclusive` with `stop_result=timeout`.

## Limits

Pure driver, 12 fixture objects, one observer, five-second scenario, no network,
Unity, Steam, Gateway, or capacity claim.

## Assumptions

The linked policy seams are the same source used by the mod and the container can
run the .NET 9 SDK image.

## Known limitations and ADRs

The first scenario files use JSON syntax as YAML to avoid adding a parser dependency.
This is a lab input convenience, not a final authoring contract. Timing is excluded
from normalized evidence.

## Setup and procedure

Run `Invoke-AuthorityExperiment.ps1 -Experiment m7-e00-lab-truth -RunTwice`, run the
forced-timeout variant, and run `Test-AuthorityMalformedScenario.ps1`. Check every
retained run and compare the valid pair.

## Results

Supported. The valid pair produced 12 decisions each and compared equal. The
malformed driver was rejected before execution. The forced run retained 12 events
with `inconclusive/timeout`, proving the stop path is visible without claiming a
successful experiment.

## What changed in our understanding

The lab loop is trustworthy enough for synthetic shape experiments. A timeout is a
result class, not a hidden process failure.

## Next experiment

Use the same receipt contract for radius, density, and boundary correlations in
M7-E01.
