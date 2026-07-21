# Research-to-Lab Method

## 1. Refine the question

State the player-visible behavior, server authority requirement, and network budget
before choosing the model. Record what must be real, what may be approximate, and
which values a remote observer actually needs.

## 2. Record source provenance

Keep the source, citation, units, extracted rule, and interpretation together. When
sources disagree, preserve the disagreement instead of silently selecting a constant.

## 3. Build pure simulation

The core model should run without scene nodes where practical. This enables batch
runs, server promotion, deterministic tests, and comparison with source examples.

Existing examples:

- `TerrainSim` — pure terrain and erosion math;
- `TreeFellingSim` — pure polar trunk and felling math.

## 4. Build the lab surface

Godot provides visualization, controls, presets, force diagrams, cross-sections, and
debug overlays. The lab should expose assumptions that would otherwise become hidden
constants.

## 5. Validate behavior

Use scenario presets for known edge cases and parameter sweeps for broad spaces.
Capture the input parameters, observed result, and reason it is acceptable. Visual
plausibility and numerical correctness are different checks.

## 6. Define the network projection

Do not transmit the entire lab state. Identify the smallest state from which clients
can render the authoritative outcome. Record precision, update cadence, and recovery
behavior after packet loss.

## 7. Promote through explicit gates

| Gate | Required evidence |
|---|---|
| Researched | Sources and assumptions recorded |
| Simulated | Pure model runs independently |
| Visualized | Lab exposes state and edge cases |
| Validated | Presets, tests, or sweeps have recorded outcomes |
| Projected | Minimal network state and byte accounting defined |
| Serialized | Shared contract has round-trip tests |
| Transported | Gateway selects lane and fallback correctly |
| Integrated | Active client consumes the live state end to end |

No later label should be inferred from an earlier one. In particular, “24-byte
projection demonstrated” does not mean “24-byte live packet shipped.”

## 8. Keep the feedback loop

Live measurements may invalidate lab assumptions. Feed bandwidth, latency, visual
artifacts, and server cost back into the model and projection rather than patching the
client around a broken contract.
