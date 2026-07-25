# Runbook - bounded motion phase capture

Use this lane to measure the current client motion overlay before changing
interpolation, binding, cadence, or authority.

## Human boundary

The two Valheim clients must be joined and fully loaded. After that, the agent can
select APPLY/observe roles, run an allow-listed movement pattern, collect both
Companion streams, and analyze them. The human only watches the two screens and
records `smooth`, `rough`, or `mixed`, whether the visible effect followed the APPLY
role, and the first noticeable correction.

## Preflight

```powershell
.\tools\i5\Test-Wave0Readiness.ps1 -SummaryOnly
```

Stop if either Companion is unreadable, the releases disagree, the motion lane is
not ready, or both players are not present. Do not spend a human movement window
discovering a stale DLL or missing telemetry contract.

## Preferred run

```powershell
.\tools\i5\Start-TwoClientFeelWindow.ps1 `
  -Pattern straight_north `
  -MotionDurationSeconds 10 `
  -ApplyClient omen `
  -RoleReversal `
  -Label cre-e06-straight `
  -CollectPhaseSummaries
```

The command starts capture before movement, runs one bounded window per role, stops
the motion command in `finally`, downloads both evidence bundles, and writes an OMEN
and i5 motion-phase summary into each window's `bundles\motion-phase` directory.

The formal Wave 0 lane uses the same analyzer automatically:

```powershell
.\tools\wave0\Wait-Wave0LiveGate.ps1 `
  -DesiredApplyClient omen `
  -OutputJson .\captures\wave0-live-gate\result.json
```

Its receipt embeds `capture.receipt.motion_phase`; a missing contract or unreadable
summary fails the capture rather than consuming the result as visual evidence.

Run `stutter_north` only after the straight baseline is complete:

```powershell
.\tools\i5\Start-TwoClientFeelWindow.ps1 `
  -Pattern stutter_north `
  -MotionDurationSeconds 10 `
  -ApplyClient omen `
  -RoleReversal `
  -Label cre-e06-stutter `
  -CollectPhaseSummaries
```

## Interpretation order

1. `phase_measurements_enabled_at_end` must be true.
2. Received counters must advance on the observing client.
3. Compare receive spacing and drain/coalescing before blaming rendering.
4. Compare bind mean with whole-`LateUpdate` mean before caching lookups.
5. Compare applies/received with the measured render-to-send amplification.
6. Treat interframe displacement over 50 mm as evidence of another transform
   writer or physics step. It does not identify native Valheim by itself.
7. Correlate the phase summary with the human note; neither alone is a visual
   authority verdict.

The `lifetime_maxima` section is deliberately conservative. A maximum may predate
the capture. Only `changed_during_capture=true` places a new lifetime high inside
the window.

## Quick OMEN-only collection

For telemetry plumbing or idle-baseline checks that do not need i5:

```powershell
.\fieldlab\scripts\Invoke-MotionPhaseCapture.ps1 `
  -DurationSeconds 30 `
  -Label cre-e06-idle
```

## Re-analysis

```powershell
.\fieldlab\scripts\Summarize-MotionPhaseCapture.ps1 `
  -SamplesPath <capture>\samples.jsonl `
  -OutputPath <capture>\motion-phase-summary.json
```

The analyzer is restart-aware for cumulative totals. It refuses captures from mods
that do not expose the CRE-E06 phase contract.

To prove the two-client bundle adapter without live games, zip a retained or
synthetic `samples.jsonl` at the archive root and run:

```powershell
.\fieldlab\scripts\Summarize-TwoClientMotionPhaseBundles.ps1 `
  -OmenBundlePath <omen-bundle.zip> `
  -I5BundlePath <i5-bundle.zip> `
  -OutputDirectory <capture>\motion-phase
```

The adapter exits nonzero unless both summaries succeed and always writes
`motion-phase-receipt.json`, including per-machine rejection reasons.
