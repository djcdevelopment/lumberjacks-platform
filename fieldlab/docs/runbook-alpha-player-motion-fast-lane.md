# Alpha runbook — player motion fast lane

Date: 2026-07-22

## Purpose

The two-client alpha pass proved real co-presence but exposed rough remote-player
motion: held straight-line running looks extrapolated/glidey, while stutter-step
movement appears with lower perceived latency. Transport counters were healthy, so
the next test is the Valheim ZDO production/apply cadence for player-character ZDOs.

This runbook validates the clean-room Comfy-era performance ideas now implemented in
`ComfyNetworkSense`:

- player-character ZDOs bypass static-world band shaping;
- the send-loop cadence override exists as an explicit, off-by-default A/B lever;
- telemetry tells us whether the cadence override is actually installed for the
  loaded Valheim assembly.

## Flags

Set on the dedicated server config:

```ini
[Netcode]
zdoPlayerFastLaneEnabled = true
zdoSendCadenceOverrideEnabled = false
zdoSendCadenceOverrideIntervalSeconds = 0.05
```

`zdoPlayerFastLaneEnabled=true` is the normal alpha posture. It only changes the
existing Lumberjacks redirect path: when a candidate ZDO is one of the connected
players' character ZDOs, it is emitted every native sync pass instead of being
mid-band thinned or far-band dropped.

`zdoSendCadenceOverrideEnabled=false` is deliberate. The override replaces the
server-side send scheduler if the loaded Valheim assembly exposes a compatible
helper call. Leave it off until the player fast-lane result is measured.

## A/B workflow

1. Deploy the new `ComfyNetworkSense.dll` to the P7 Valheim server.
2. Restart the Valheim server so BepInEx loads the new DLL and config keys.
3. Keep `zdoSendCadenceOverrideEnabled=false`.
4. Join with two clients in the same area.
5. Run the visual test:
   - player A holds straight-line run for 10 seconds;
   - player A repeats with stutter-step/run-stop-run for 10 seconds;
   - player B watches motion quality directly.
6. Record dashboard/API values:
   - `player_fast_lane_candidates`;
   - `player_fast_lane_emitted`;
   - `band_held`;
   - `band_dropped`;
   - `pending`;
   - `active_consumers`;
   - `coverage_native_only`.
7. If motion is still rough and queue health is good, flip:

```ini
zdoSendCadenceOverrideEnabled = true
```

8. Restart the server and repeat the same visual test.
9. Treat the override as viable only if:
   - `zdo_send_cadence_patch_installed=true`;
   - `zdo_send_cadence_reflection_ok=true`;
   - `zdo_send_cadence_last_error` stays empty;
   - remote-player motion visibly improves;
   - pending remains bounded and drains;
   - native-only does not climb persistently.

## Rollback

Fast rollback for motion regressions:

```ini
zdoSendCadenceOverrideEnabled = false
zdoPlayerFastLaneEnabled = false
```

Restart the Valheim server after changing the cadence override. The player fast-lane
flag is read at runtime by the redirect path, but restart anyway during alpha runs so
the log and config state are unambiguous.

## Interpretation

If fast-lane improves held-run motion, the bug was static-world AoI shaping applied
to player-character updates.

If fast-lane does not improve motion but the cadence override does, the bug is native
send scheduling/fairness under this high-density world.

If neither helps and queue health remains good, inspect client apply/smoothing around
`RPC_ZDOData` and Unity-side remote character interpolation.
