# Runbook — Harmony patch-load A/B benchmark (lab client)

Purpose: put a measured number on what the mod's Harmony detour chain costs per call and per
frame, instead of assuming the folklore "5x hot-path overhead". Companion policy:
`fieldlab/docs/harmony-patch-policy.md`.

## What it measures

- `perf-patchload.jsonl` (new, `perfPatchLoadRollupEnabled=true`): per-interval rollups —
  `section`, `calls`, `total_ms`, `max_ms`, `mean_us` — for every wrapped Harmony patch body
  (`Patch.ZDOMan.*` sections).
- `benchmark-results.jsonl`: the existing 60s-window `avg_fps` / `p95_frame_time_ms` for
  whole-frame deltas.

Known blind spot: `MeasurePatchLoad` times the *body inside* the detour; the Harmony
trampoline/argument-marshaling cost around it is not separately visible. It shows up only in
the whole-frame comparison, bounded above by `calls × (trampoline + body)`.

## Preconditions

- Lab client volume seeded (one-time manual Steam login — see
  `runbook-headless-valheim-lab.md`; preflight receipt must show zero blockers).
  As of 2026-07-24 client01/client02 are **not seeded**; this is the human step.
- `valheim-server` + `comfy-gateway` lab containers up (they usually are).

## Procedure (single session, two windows — best control)

1. Stage payload + overlay cfg (builds the mod first):

   ```powershell
   .\fieldlab\scripts\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action refresh `
       -ConfigPath .\fieldlab\experiments\patchload-ab\patchload-lab.cfg
   .\fieldlab\scripts\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action preflight
   .\fieldlab\scripts\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action start
   ```

2. Open the client console via noVNC (`http://127.0.0.1:8081`, F5 in-game). Wait until the
   character is in-world and settled (no loading churn).

3. **Window A — inert bodies** (the standing cost every player pays): with the netcode probe
   stopped, run `network_sense_benchmark` and let the window complete.

4. **Window B — armed observer**: `network_sense_lumberjacks_netcode_probe start`, then
   `network_sense_benchmark` again at the same spot. Optionally
   `network_sense_lumberjacks_netcode_probe stop` after.

5. Collect (from the operator seat or via MCP `valheim_tail_swarm_client(client, file_name=...)`):
   `benchmark-results.jsonl`, `perf-patchload.jsonl`, `perf-hitches.jsonl`.

6. Analysis (offloadable to mechnet — it is self-contained JSONL crunching): per section,
   report `mean_us`, `calls/s`, worst `max_ms`; diff the two windows' `p95_frame_time_ms`;
   compute total patch-body ms per frame = Σ(section total_ms) / frames in window.

7. Land the findings note in `fieldlab/evidence/patchload-ab-<date>/` with the verdict on
   the detour-overhead question.

## Interpretation guardrails

- Lab clients render on a virtual X display (software Mesa) — absolute FPS is meaningless;
  only A-vs-B deltas under identical conditions count.
- Sections are also visible live in the Unity profiler as
  `ComfyNetworkSense.Patch.*` markers if a deeper look is ever needed.
- Redirect-postfix cost under real load is a **server-side** question (the redirect arms on
  the server). The same rollup key on the server cfg measures it there; this runbook's client
  lane covers the client-side detour chain.
