# CRE-E05 - What work does the current motion apply loop request?

Status: supported as a source-derived model

## Goal

Make the existing client presentation loop's scaling and smoothing behavior explicit
before designing timing capture or changing the mod.

## Objective

Model the checked-in `LumberjacksMotionRunner` defaults across frame rates
`20,40,60,120` and remote-entity counts `1,10,100`.

## Source facts

The current runner:

- samples outbound motion at 20 Hz by default;
- drains all received frames during `Update` and keeps only the newest sequence per ZDO;
- iterates every fresh remote entity during every Unity `LateUpdate`;
- resolves its object and applies exponential position/rotation convergence;
- keeps applying the same last snapshot for up to 0.5 seconds;
- does not extrapolate velocity.

The model is tied to those source facts. It is not a profiler measurement.

## Hypothesis

Receive coalescing is already latest-wins, but render apply work scales with remote
entities times render FPS rather than accepted snapshot rate. Exponential convergence
over one 50 ms send interval remains approximately constant at frame rates that are
multiples of 20 Hz.

## Predicted outcome

For one remote entity:

| FPS | Inbound/s | LateUpdate applies/s | Applies/snapshot | Last-snapshot stale-tail upper bound |
|---:|---:|---:|---:|---:|
| 20 | 20 | 20 | 1 | 11 |
| 40 | 20 | 40 | 2 | 21 |
| 60 | 20 | 60 | 3 | 31 |
| 120 | 20 | 120 | 6 | 61 |

At 100 remote entities the same rows become `2,000/4,000/6,000/12,000`
render apply calls per second. With convergence rate 18, each frame-rate row reaches
about `0.59343` of the remaining error over one 50 ms send interval and has a
55.56 ms time constant.

## Limits

No Stopwatch, Unity, object lookup, native transform competition, GC, transport
latency, or hardware measurement. The stale-tail count is an inclusive upper bound
when a render application coincides with the final arrival.

High render apply count is not automatically waste: per-frame interpolation requires
per-frame evaluation. The model identifies where expensive lookup/binding and
interpolation concerns need separate measurement.

## Assumptions

All modeled entities remain fresh, resolve successfully, are not the local player,
and stay within the 30 meter correction guard.

## Known limitations and ADRs

This models presentation only. It neither recommends lowering render cadence nor
authorizes replacing native movement. Critical mutations remain outside the seam.

## Setup and procedure

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e05-current-apply-model `
  -Driver pure `
  -RunTwice
```

## Results

Two 12-row runs matched exactly and all four invariants passed.

| FPS | One remote: inbound/apply | 100 remotes: inbound/apply | Applies per snapshot | One-remote stale tail |
|---:|---:|---:|---:|---:|
| 20 | 20 / 20 | 2,000 / 2,000 | 1 | 11 |
| 40 | 20 / 40 | 2,000 / 4,000 | 2 | 21 |
| 60 | 20 / 60 | 2,000 / 6,000 | 3 | 31 |
| 120 | 20 / 120 | 2,000 / 12,000 | 6 | 61 |

Every frame-rate row retained `0.593430340` convergence over one 50 ms send
interval. The modeled convergence time constant is 55.56 ms.

These counts describe requested loop work under the declared assumptions. They do
not measure the cost of object lookup, Unity transform writes, native competition,
or hardware saturation.

## What changed in our understanding

The receive side already performs latest-wins coalescing per ZDO. Adding another
client queue with the same behavior would not address the current render loop.

The present visual algorithm is a one-snapshot exponential chase:

```text
receive burst -> retain newest snapshot -> every LateUpdate:
resolve object -> Lerp/Slerp toward last snapshot
```

It uses velocity only as carried data; it does not extrapolate from it. The observed
glide is therefore consistent with deliberate convergence toward a moving sequence
of targets, while burst coalescing can turn several intermediate targets into one
larger correction.

Per-frame interpolation itself is not the obvious waste. The first cost questions
are whether object binding is being repeated unnecessarily, how much time lookup and
transform application consume separately, and whether native Valheim writes the same
transform between Lumberjacks applications.

## Next experiment

Instrument receive, drain/coalesce, object binding, render apply, target error, and
possible native overwrite as separate bounded rollups. Then replay timing against
current chase-latest and a two-snapshot interpolation candidate.
