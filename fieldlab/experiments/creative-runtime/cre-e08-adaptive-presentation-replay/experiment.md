# CRE-E08 - Can relative transit variation choose a useful playout delay?

Status: analyzed; v2 earns a reversible live A/B candidate, no DLL built

## Goal

Test whether a bounded adaptive interpolation delay improves on both
chase-latest and fixed buffering without requiring synchronized client clocks.

## Objective

Replay the six existing motion shapes through stable, periodic-burst,
isolated-burst, deterministic-jitter, and periodic-loss schedules. Compare:

- current chase-latest;
- fixed 50, 100, 150, and 200 ms interpolation;
- one adaptive 100-200 ms fast-rise/slow-decay policy.

The adaptive input is relative transit variation:

```text
(arrival[n] - arrival[n-1]) - (sent[n] - sent[n-1])
```

plus sequence gaps. `ValheimMotionSnapshot.SentMilliseconds` already carries the
sender clock. A constant clock offset cancels from the delta, so this does not
assume synchronized machines.

## Hypothesis

The policy should stay near 100 ms under stable arrivals, rise immediately after
evidence of a burst or sequence gap, and decay at 25 ms per second. It cannot
hide the first unseen disturbance. It may reduce later stalls while spending less
average delay and current-time error than a permanent 200 ms buffer.

## Candidate gate

Safety invariants only prove the equation is bounded. A reversible live A/B is
earned only if the non-teleport disturbed profiles collectively show:

1. fewer stalled moving frames than chase-latest;
2. lower mean delay than fixed 200 ms;
3. lower current-time error than fixed 200 ms;
4. no more large-step frames than chase-latest.

If any condition fails, retain the receipt and revise or reject the equation.

## Predictions

| Profile | Prediction |
|---|---|
| stable | delay remains 100 ms, the smallest fixed control that retains a future bracket on the 40 ms synthetic path |
| periodic burst | first gap is visible; later gaps are partly hidden while delay remains elevated |
| isolated burst | delay rises once, then decays toward 50 ms |
| deterministic jitter | delay moves within bounds without reaching 200 ms for every variation |
| periodic loss | sequence gaps raise delay, but interpolation cannot recreate missing truth |

## Limits

- Pure deterministic replay; no Unity, physics, native transform writer, object
  binding, hardware scheduling, or human feel.
- Synthetic schedules are correlation shapes, not population claims.
- The model knows sender and receiver times. A future implementation must unwrap
  the existing 32-bit sender timestamp safely.
- The minimum, maximum, and decay rate are experiment values, not production
  configuration.
- A pass permits only a reversible client A/B. It does not authorize motion
  presentation ownership or suppress native Valheim movement.

## Setup and procedure

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e08-adaptive-presentation-replay `
  -Driver pure `
  -RunTwice
```

The normalized runs must match. Inspect all 180 result rows, the five safety
invariants, and `adaptive_candidate_gate`.

## Results

Both v2 runs normalized equal. All five safety invariants passed across 180 rows:
finite output, final-sequence consumption, interpolation bounds, declared adaptive
delay bounds, and teleport discontinuity handling.

Across the four disturbed profiles and five non-teleport trajectories:

| Policy | Stalled moving frames | Large steps | Mean delay | Mean current-time error | Mean step change |
|---|---:|---:|---:|---:|---:|
| chase-latest | 74 | 13 | 0 ms | 1.418 m | 0.083 m |
| fixed 100 ms | 187 | 43 | 100 ms | 1.215 m | 0.075 m |
| fixed 150 ms | 50 | 17 | 150 ms | 1.737 m | 0.040 m |
| adaptive 100-200 ms | 51 | 2 | 176.9 ms | 2.053 m | 0.035 m |
| fixed 200 ms | 0 | 0 | 200 ms | 2.265 m | 0.024 m |

The adaptive candidate passed the declared gate: fewer aggregate stalls and large
steps than chase, and lower mean delay and current-time error than fixed 200 ms.
It did not erase causality. Periodic and isolated first bursts each produced 21
stalled frames versus chase's 17 because the policy cannot budget a disturbance
before observing it. Deterministic jitter improved from 23 chase stalls to zero;
periodic loss improved from 17 to 9 but retained two large steps.

## Experiment log

1. The initial policy deliberately uses one simple state machine: immediate rise
   from relative transit/sequence disturbance and linear decay. More elaborate
   percentiles, histograms, and velocity extrapolation are out of scope.
2. `pure-20260725T044834Z` rejected the 50 ms floor before any live build. On the
   stable profile it exactly matched the fixed 50 ms control: 296 stalled moving
   frames and 109 large steps across five non-teleport paths. Fixed 100 ms had
   zero of both. The failure showed that an interpolation delay must budget one
   future source bracket in addition to baseline transit; v2 therefore raises
   the floor to 100 ms while retaining the 200 ms ceiling.
3. `pure-20260725T045003Z` and its repeat used that 100 ms bracket floor.
   All 180 normalized decisions matched. The adaptive policy averaged 176.9 ms
   across disturbed paths and passed all four candidate criteria, so it earns a
   reversible alpha A/B candidate—not production promotion.

## What changed in our understanding

An adaptive jitter buffer still needs a clean-path bracket floor. Relative transit
variation can choose when to spend additional delay without synchronized clocks,
but it cannot retroactively hide the first burst. The useful trade is narrower
than “adaptive is better”: v2 sits close to fixed 200 ms under repeated disruption,
then releases delay slowly; its main gains are lower correction size and recovery
toward 100 ms after isolated disturbance.

The next implementation, if authorized after the current mod work settles, should
be alpha-only and reversible. It must unwrap the existing 32-bit sender timestamp,
retain at least two delivered snapshots per remote entity, expose current/min/max
delay in the same JSONL contract, and fall back to current chase-latest on invalid
time or insufficient brackets. CRE-E06 role attribution remains the live verdict
path; this replay does not identify or suppress a competing transform writer.

## What this can change

If the candidate gate passes, add it behind an alpha-only switch and use the
role-aware CRE-E06 live receipt to correlate visual behavior with measured
APPLY-path displacement. If it fails, the retained profile rows identify whether
delay, stalls, or correction size rejected it before another login cycle.
