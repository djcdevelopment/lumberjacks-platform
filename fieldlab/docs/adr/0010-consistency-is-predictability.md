# ADR 0010 — Consistency means predictable, not invariant

- **Status:** Accepted (2026-07-21)
- **Rung:** cross-cutting; governs area-of-interest tuning, degradation policy, and what we optimise for

## Context

Stated by Derek while reviewing the AoI work:

> Consistent fidelity — even if it looks ugly or feels choppy, so long as it is CONSISTENT, it
> doesn't break the immersion of a player, and that's what we need to keep.

The obvious reading is "hold everything constant", and an earlier draft of this decision took it
that way — concluding that `Replication:AdaptiveDegrade` was harmful because it changes behaviour
under load. **That reading is wrong**, and Derek corrected it:

> Adaptive design is still consistent / predictive falloff.

The distinction matters enough to be the whole decision. Adaptive degradation is a **deterministic
function of an observable condition**. "When it gets crowded, it thins out" is a rule a player learns
almost immediately and then predicts correctly. That is not a break in immersion; it is the world
having physics. It is also strictly better than holding full fidelity until the system *collapses*,
because the collapse is the discontinuity.

So the property being protected is not sameness. It is **predictability**.

## Decision

**Optimise for predictability, not for peak quality and not for invariance.** Concretely:

1. **A worse constant beats a better variable.** Given a choice between a fidelity level we can hold
   always and a higher one we can hold usually, take the lower one. Tune to the floor that survives
   the worst density band, not the ceiling reachable in an empty field.
2. **Degradation must be proportional and caused.** Falling off as load rises is good — it is
   legible. What is forbidden is change the player cannot attribute to anything: chatter at a
   threshold, or a cliff where a slope was expected.
3. **Every threshold needs damping.** A boundary crossed repeatedly with no perceptible cause is
   indistinguishable from randomness at the player's end. Hysteresis is therefore a **fidelity
   requirement, not a performance optimisation** — which is how it was previously (mis)filed.
4. **Discontinuities are the enemy, at every scale.** Pop, snap, sudden collapse, a proxy appearing.
   A slightly wrong thing that is always there beats a perfect thing that arrives.
5. **Variance is only a defect when it is uncorrelated.** Spread that tracks density is the system
   telling the truth about load. Spread with no visible cause is the thing to hunt.

## What this changes

**Adaptive degrade is endorsed, its threshold behaviour is not.** `AdaptiveDegrade.cs:22-23` states
that "degrade lifts the instant the relevant broadcast fits inside budget again — no cooldown, no
hysteresis." The *mechanism* is right and matches ADR 0011's "reduce frequency before dropping". The
missing damping is the defect: sitting exactly at budget, `ShouldSuppressMidBand` can answer
differently tick to tick from a cause no player can perceive. Adding a cooldown or a
degrade/recover asymmetry is a small change and is the correct one — **not** disabling the feature.

**The spatial boundary has the same flaw.** `InterestManager.cs:113` and `:118` compare with a plain
`<=` against `nearRadiusSq` / `midRadiusSq`. An entity hovering at 100.0 units flips between 20 Hz
and 5 Hz every tick. That is chatter, not falloff.

**It redefines the knee.** See `Lumberjacks/docs/network/aoi-knee-experiment-brief.md`: the frontier
worth finding is where p99 pulls away from p50, not where the budget is breached — *and* the
divergence should be checked for correlation with density. Correlated spread is acceptable and
expected. Uncorrelated spread is the failure.

**It sets the tuning procedure.** Find the knee, then back off to what holds under the worst band
being designed for, and run that everywhere. Do not tune per-situation to extract peak fidelity.

## Consequences

- **We will ship numbers that look worse on a benchmark.** A flat 5 Hz that never varies will lose a
  throughput comparison against something that averages 15 Hz and stutters. That trade is the point,
  and anyone reading a benchmark of this system needs to know it was made deliberately.
- **Hysteresis constants become load-bearing** and need their own justification, since a dead-band
  too wide is itself a discontinuity (a lurch when it finally crosses).
- **It is not a licence for low fidelity.** The floor should be as high as can be held *always*. The
  decision is about which quantity to maximise, not about lowering ambition.
- **It gives the landmark proxy design a hard requirement.** The proxy/real swap in
  `landmark-reach-design.md` is a discontinuity by construction, and must be engineered as a
  crossfade or a distance the player is unlikely to be looking from — not left to pop.
- **It explains a scenario we already had.** "Lag into the coast, get stuck, jumped and die"
  (`area-of-interest-findings.md` §0) is a *variance* death, not a throughput death. The average was
  probably fine right up until it wasn't.

## Related

`Lumberjacks/src/Game.Simulation/Tick/AdaptiveDegrade.cs`;
`Lumberjacks/src/Game.Simulation/World/InterestManager.cs`;
`Lumberjacks/docs/adrs/0011-graceful-degradation-combat-zones.md` (this ADR supplies the *why*
behind its "reduce frequency before dropping");
`Lumberjacks/docs/network/aoi-knee-experiment-brief.md`;
`Lumberjacks/docs/network/landmark-reach-design.md`;
`network/telemetry-and-scores.md` (a variance-oriented schema throughout — `jitter_ms` beside
`rtt_ms`, `p95_frame_time_ms` beside `avg_fps`, `correction_magnitude_avg` — which is what a
consistency instrument looks like).
