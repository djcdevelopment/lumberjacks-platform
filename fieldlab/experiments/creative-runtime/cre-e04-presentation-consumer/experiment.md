# CRE-E04 - What should consume accepted transient motion?

Status: supported for deterministic consumer-policy shape

## Goal

Discover whether a small latest-wins/expiry consumer reduces presentation apply work
without changing the final fresh state under clean cadence, bursts, gaps, reordering,
and delayed delivery.

## Objective

Feed the same deterministic 20-sample, two-source arrival vector to:

1. immediate direct apply;
2. latest-wins per source on a 50 ms drain;
3. latest-wins with the same drain and a 120 ms age limit.

## Hypothesis

Coalescing will remove redundant intermediate burst work while preserving the final
fresh sequence for each source. Expiry will additionally prevent delayed-but-newer
presentation samples from being applied, then allow the next fresh sample to recover.

## Predicted outcome

| Consumer | Applied | Coalesced | Expired | Stale | Projected deliveries at N=3 |
|---|---:|---:|---:|---:|---:|
| direct | 19 | 0 | 0 | 1 | 57 |
| latest wins | 14 | 5 | 0 | 1 | 42 |
| latest wins + expiry | 12 | 5 | 2 | 1 | 36 |

All three consumers finish at source A sequence `13` and source B sequence `9`.
The expiry consumer applies no sample older than 120 ms.

## Limits

Pure deterministic consumer policy only. No Gateway socket, Unity render loop,
interpolation, extrapolation, frame-time measurement, or human feel. Lower apply
count is not evidence of smoother movement; coalescing deliberately removes
intermediate trajectory samples.

## Assumptions

The Gateway freshness seam has already rejected duplicate and old transport frames.
The consumer repeats the half-range guard defensively, but it does not request
retransmission or repair missing transient samples.

## Known limitations and ADRs

This is presentation-only scaffolding. It cannot carry hit results, death, inventory,
builds, or other critical mutations. The expiry limit is a test input, not a live
tuning recommendation.

## Setup and procedure

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e04-presentation-consumer `
  -Driver pure `
  -RunTwice
```

## Results

Two 60-row runs produced identical normalized decisions.

| Consumer | Applied | Coalesced | Expired | Stale | N=3 pre-fanout projection |
|---|---:|---:|---:|---:|---:|
| direct | 19 | 0 | 0 | 1 | 57 |
| latest wins | 14 | 5 | 0 | 1 | 42 |
| latest wins + expiry | 12 | 5 | 2 | 1 | 36 |

Every consumer finished at source A sequence `13` and source B sequence `9`.
The expiry consumer applied no sample older than 75 ms against the configured
120 ms limit, discarded both deliberately delayed samples, and accepted the next
fresh sample from each source.

This is a 26% apply-count reduction for latest-wins and a 37% reduction with expiry
against this synthetic vector. Those are fixture results, not capacity forecasts.

## What changed in our understanding

Latest-wins is a useful bounded-work primitive, but its placement determines what it
saves:

| Placement | Saves | Does not save | Primary tradeoff |
|---|---|---|---|
| client before Unity apply | client apply work | Gateway/network fanout | safest opt-in seam; every frame still crosses transport |
| Gateway per recipient, after AoI | delivery and client apply work | source ingress | preserves recipient policy but adds keyed queue state |
| Gateway before recipient fanout | shared route work | recipient-specific cadence | broadest shedding and broadest fidelity decision |

The experiment proves final-state and work-count behavior only. It does not prove that
coalesced motion looks smoother; removing five trajectory samples could improve burst
catch-up or make visible stepping worse without interpolation.

## Next experiment

Replay a captured timing distribution through the same consumers, then choose one
opt-in placement for a bounded local Unity comparison.
