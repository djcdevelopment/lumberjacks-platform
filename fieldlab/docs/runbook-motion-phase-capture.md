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
and i5 motion-phase summary into each window's `motion-phase` directory.

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
