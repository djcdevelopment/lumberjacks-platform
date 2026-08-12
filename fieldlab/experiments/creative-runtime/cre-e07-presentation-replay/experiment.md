# CRE-E07 - Does buffered interpolation earn a live client build?

Status: analyzed; no fixed buffer promoted to a live build

## Goal

Compare the current one-snapshot exponential chase with bounded 50, 100, 150,
and 200 ms buffered interpolation candidates before changing the Valheim mod or
spending another two-client join window.

## Objective

Replay the six existing M7 motion trajectories through identical stable and
three-sample-burst arrival schedules at 60 render frames per second. Emit one
small result row per pattern, arrival profile, policy, and buffer delay.

## Hypothesis

Longer buffers should absorb more of the 140 ms worst-case synthetic arrival age,
but increase wall-clock error. The 50 ms candidate should be responsive but expose
bursts, while 150 ms should minimize burst stalls. Straight and turn trajectories
should benefit more consistently than stutter or stop/start. Teleports must cross a
discontinuity guard rather than being smeared through ordinary interpolation.

## Predicted outcome

| Measure | Current chase-latest | Buffered interpolation |
|---|---|---|
| burst target changes | exponentially chases the newest coalesced target | consumes source-time brackets behind a 50/100/150/200 ms buffer |
| current-time error | lower expected | higher by the intentional delay |
| delayed-timeline error | target-dependent | lower when the buffer covers the arrival burst |
| frame step change | reacts to each new target | lower for ordinary continuous segments |
| teleport | converges toward the new point | holds the old segment, then crosses a discontinuity |

These are predictions, not pass conditions.

## Limits

- Pure deterministic replay; no Unity, physics, object binding, native transform
  writer, packet loss, clock skew, or hardware timing.
- The motion fixtures are correlation shapes, not representative player traces.
- “Lower step change” is not a substitute for human smoothness.
- The buffered candidate does not extrapolate velocity.

## Assumptions

- Source samples are ordered and use the current 20 Hz default.
- The stable profile adds 40 ms latency.
- The burst profile makes each final three samples of a six-sample block arrive
  together without reordering.
- Five metres is an experiment-only discontinuity threshold, not a production value.

## Known limitations and ADRs

This experiment chooses whether an equation deserves a reversible alpha switch. It
does not authorize presentation ownership, suppress native Valheim movement, or move
critical mutations to the transient channel.

## Setup and procedure

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e07-presentation-replay `
  -Driver pure `
  -RunTwice
```

The two normalized runs must match. Inspect the 60
`presentation.replay_result` rows and the four invariants before considering a DLL.

## Results

The final 60-row run and its repeat matched exactly. All four safety invariants
passed: every output remained finite, every policy consumed sequence 40, buffered
interpolation stayed inside its source brackets, and teleport discontinuities were
held or snapped rather than ordinarily interpolated.

For the five non-teleport patterns under the three-sample burst:

| Policy | Stalled moving frames | Mean current-time error | Mean step change | Large steps |
|---|---:|---:|---:|---:|
| chase-latest | 17 | 1.494 m | 0.083 m | 11 |
| 50 ms buffer | 342 | 1.006 m | 0.379 m | 85 |
| 100 ms buffer | 114 | 1.265 m | 0.131 m | 27 |
| 150 ms buffer | 44 | 1.703 m | 0.078 m | 15 |
| 200 ms buffer | 0 | 2.196 m | 0.024 m | 0 |

The 200 ms buffer is the only fixed candidate that fully covers this synthetic burst,
but its current-time error is roughly 47% higher than chase-latest. The 150 ms
candidate reduces mean step change only slightly while retaining more stalls and
large steps than chase. Neither earns a live DLL.

### Experiment log

1. `pure-20260725T040311Z` isolated the initial 100 ms candidate and exposed its
   burst stalls.
2. `pure-20260725T040515Z` expanded to 50/100/150 ms and was correctly retained as
   refuted: the first teleport invariant required a guard counter even when the
   policy directly snapped across the discontinuity.
3. `pure-20260725T040658Z` corrected that invariant and added 200 ms.
4. `pure-20260725T040847Z` removed an unfair extra convergence tail for larger
   buffers. Its repeat normalized equal and is the result table above.

## What changed in our understanding

A fixed interpolation delay can trade corrections for latency, but it does not
solve burst arrivals for free. In this shape, completely hiding a 140 ms arrival-age
burst requires 200 ms of source-time buffer. The apparent “smoothness” comes from
being substantially behind current truth.

The next useful input is the actual receive-interval distribution from a bounded
two-client CRE-E06 capture, not another guessed delay. The current rollup provides
mean and lifetime max but not enough distribution shape to choose an adaptive buffer.
Native/Lumberjacks transform competition also remains a separate hypothesis.

## Path branch

- Do not build the fixed-delay candidate into the mod.
- Add a bounded receive-interval histogram or equivalent retained arrival shape,
  then replay the real capture through an adaptive buffer before considering a DLL.
- If neither policy explains the live symptom, prioritize the CRE-E06
  interframe-displacement evidence and native-writer attribution.
