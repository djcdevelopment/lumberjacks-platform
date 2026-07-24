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

The pure driver uses one logical revision and synthetic distances. Gateway coverage
uses the real in-memory queue and the durable follow-up uses a temporary WAL; neither
is a claim about production load or Valheim client behavior.

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

The pure, Gateway, and durable Gateway runs are all supported. Each pure/Gateway run retained 112 decisions;
emissions were N=2:1, N=10:5, and N=100:65. The Gateway run drove the real
`ValheimZdoRedirectService`, replayed each accepted batch, and observed duplicates
without producing a second pending item or terminal ACK. Recipient isolation,
scaling direction, and local already-delivered behavior all passed.

The durable follow-up restarted the real redirect service twice against a temporary
WAL. Both recipient partitions recovered one pending item, retained duplicate counts,
and persisted one terminal ACK each after the reconnect.

## What changed in our understanding

The existing fan-out equation and the Gateway queue preserve recipient-local state
through the tested seam. This does not prove that the native candidate list is
complete, so it does not authorize authority promotion.

## Next experiment

Add higher-volume Gateway reconnect/lease pressure only if the native capture exposes
a queue-specific question; otherwise capture native candidate observations without
relabeling Gateway rows as native evidence.
