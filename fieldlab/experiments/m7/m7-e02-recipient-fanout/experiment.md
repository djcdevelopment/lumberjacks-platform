# M7-E02 - Does recipient fan-out remain independent as N grows?

Status: analyzed

## Goal

Prove that one observer's delivered revision, duplication, or distance does not
suppress another observer's read-copy decision.

## Objective

Evaluate one revision against N=2, N=10, and N=100 observers using the linked
`ZdoFanoutPlan` seam.

## Hypothesis

Fan-out decisions remain recipient-local and emissions grow with in-band observers;
the result will expose shape, not prove 100-player capacity.

## Predicted outcome

No duplicate terminal emit occurs within a case. Emission totals increase from N=2
to N=10 to N=100, while an already-delivered observer remains local.

## Limits

Pure driver, one logical revision, synthetic distances, 112 event rows, and no real
queue, ACK, reconnect, or Gateway socket.

## Assumptions

The pure fan-out seam is the correct first representation of the existing
co-presence behavior.

## Known limitations and ADRs

Observer IDs are fixture-local. The same IDs intentionally recur across N cases;
isolation is therefore evaluated within each case, not across the whole receipt.

## Setup and procedure

Run the E02 scenario, check the receipt, and inspect the per-observer raw rows for
the N=2/N=10/N=100 cases.

## Results

Supported. The run retained 112 decisions; emissions were N=2:1, N=10:5, and
N=100:65. Recipient isolation, scaling direction, and local already-delivered
behavior all passed.

## What changed in our understanding

The existing fan-out equation is suitable for a Gateway driver. This does not prove
that the native candidate list is complete, so it does not authorize authority
promotion.

## Next experiment

Add Gateway protocol load and ACK/reconnect evidence without relabeling pure rows as
Gateway evidence.
