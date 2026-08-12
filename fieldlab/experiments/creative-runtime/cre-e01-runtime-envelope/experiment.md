# CRE-E01 - Can a runtime envelope degrade presentation without losing truth?

Status: supported by deterministic pure-driver evidence

## Goal

Prove the smallest useful congestion gate before changing Harmony patches, Valheim
authority, or a live transport.

## Objective

Run the same protected gameplay mutations and increasing projectile-presentation
bursts through green, amber, and red synthetic budgets.

## Hypothesis

An explicit budget policy can preserve critical world mutations, degrade only
presentation work, keep deferred work bounded, and choose transport by semantics while
leaving an append-only explanation for every decision.

## Predicted outcome

- `player_death` and `projectile_hit` remain full on binary WebSocket in all bands;
- presentation work moves from full toward reduced, deferred, and dropped as pressure
  rises;
- selected cost never exceeds the declared tick budget;
- emitted presentation work uses session UDP with binary WebSocket fallback;
- deferred and dropped work has no transport;
- a repeated run produces the same normalized decision hash.

## Limits

Pure deterministic cost units, one synthetic tick per pressure band, no elapsed-CPU
capacity claim, no Unity, no live mod chain, and no P7 behavior change.

## Assumptions

The first useful contract is the decision boundary and evidence shape. Real CPU cost
can replace synthetic cost units after the patch-load A/B run produces measurements.

## Known limitations and ADRs

This does not authorize a generic `BRFALSE` transpiler or arbitrary cancellation of
other mods. The current Harmony policy still limits transpilers to verified surgical
call-site swaps. Critical state remains on a reliable ordered route.

## Setup and procedure

Run:

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 `
  -Experiment cre-e01-runtime-envelope `
  -RunTwice
```

Retain both receipts, the normalized comparison, and all
`performance.gate_decision` rows.

## Results

Two pure-driver runs completed with 38 `performance.gate_decision` rows each. Both
receipts passed the complete-row and hash checks, and their normalized decision hashes
matched:

```text
96ce78f631a74f06beccfbd660ba8953ec91eb7c1df9ecc07ec9cc14aff930b1
```

| Pressure | Full | Reduced | Deferred | Dropped |
|---|---:|---:|---:|---:|
| green | 6 | 0 | 0 | 0 |
| amber | 5 | 1 | 4 | 2 |
| red | 2 | 1 | 4 | 13 |

The two full decisions retained under red pressure were the protected
`player_death` and `projectile_hit` mutations. Presentation-only full decisions moved
`4 -> 3 -> 0` as pressure rose, while degraded presentation decisions moved
`0 -> 7 -> 18`.

All six declared invariants passed:

- critical work remained full on binary WebSocket;
- remaining budget never fell below one unit;
- emitted presentation work used session UDP with binary WebSocket fallback;
- full, reduced, deferred, and dropped modes all appeared;
- degradation moved monotonically with pressure;
- deferred depth never exceeded four.

Evidence:

- `runs/pure-20260724T133947Z/receipt.json`
- `runs/pure-20260724T133947Z/raw/events.jsonl`
- `runs/pure-20260724T133947Z-repeat/receipt.json`
- `runs/pure-20260724T133947Z-repeat/comparison/comparison.json`

## What changed in our understanding

The decision and evidence contract is small enough to run as part of the existing lab
without Unity, Steam, a new observability stack, or a generic IL scheduler. Semantics
can be kept separate from transport: pressure changes presentation fidelity while
protected state retains reliable carriage.

The experiment does not show that the synthetic cost units correspond to frame time.
That missing conversion is now isolated: CRE-0 patch-load evidence can replace the
placeholder costs without redesigning the gate or receipt.

## Next experiment

Complete CRE-0, replace one synthetic presentation cost with measured patch-load data,
then run the same policy through a Gateway burst before selecting a Valheim call site.
