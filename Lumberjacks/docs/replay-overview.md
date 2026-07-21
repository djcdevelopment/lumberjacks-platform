# Replay Viewer Overview

A 3D replay viewer for World of Warcraft combat logs. Reads a contract-stable JSON file produced by the RaidUI parser, renders raid pulls as class-colored orbs in a Godot 4.6 scene with operator controls (free-cam, scrubber, click-to-select, profile card).

This document is the walk-up guide for someone seeing the Godot scene first or returning to the project after a gap.

## Architecture

```
┌──────────────────┐         ┌────────────────────────┐         ┌──────────────────┐
│   RaidUI parser  │ ──────▶ │   PullReplay JSON file │ ──────▶ │   Godot consumer │
│  (Node, RaidUI)  │         │  schema v1             │         │  (C#, game repo) │
└──────────────────┘         └────────────────────────┘         └──────────────────┘
        │                              │                                 │
   parses combat log         one file per pull at             loads, validates,
   into pulls, emits         {outDir}/pulls/                  feeds GameState
   per-pull replay JSON      {pullId}.replay.json             signals at 5Hz;
                                                              Godot lerps to 60Hz
```

Two repos. No runtime coupling between them. The JSON file is the contract.

**RaidUI repo** (parser side): `D:\work\RaidUI\` (this slice landed in worktree `.claude\worktrees\silly-bouman-0d0c0f`). The schema lives at `contracts/session/frame.schema.json`. The parser entrypoint is `src/parser/replayBuilder.js`. Run `npm run parse-latest` to ingest the most recent combat log and emit replay files to `out/pulls/`.

**game repo** (Godot consumer): `D:\work\game\clients\godot\`. The replay scene is `scenes/replay_main.tscn`. The replay code lives in `scripts/Replay/`. The OKLCH-to-sRGB-generated class color table lives at `scripts/Replay/WowClassColors.generated.cs` and is regenerated via the RaidUI-side `tools/export-class-colors.js` script.

## Pull Replay JSON contract (v1)

Schema-stable. Validator hard-fails on mismatch.

Top-level fields:

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | const `"v1"` | yes | Loud-failure on absence or mismatch |
| `pullId` | UUID v5 | yes | Deterministic from log path + start time |
| `frameStepMs` | integer | yes | 200 (5Hz grid) |
| `arenaYd` | `{width, height}` | yes | Arena bounding box in game yards |
| `bossName` | string \| null | yes | From ENCOUNTER_START; null if missing |
| `entities[]` | array | yes, non-empty | `{entityId, kind, displayName, participantId, class, role}` |
| `frames[]` | array | yes, non-empty | `{t, entityPositions, entityHp?}` |
| `events[]` | array | optional | Sparse: deaths, top-decile damage, defensive casts |
| `pullParticipantIds[]` | array | optional | Player participant IDs |

`entityPositions` is a flat array `[x0, y0, x1, y1, ...]` parallel to `entities[]`, normalized to `[0, 1]` over the arena bounding box (corner-origin: `(0, 0)` is the bbox-min corner, `(1, 1)` is the opposite corner). This is arena-relative world space, NOT UV/texture space. Renderers re-center for their own world frame.

Frame timestamps are monotonic, divisible by `frameStepMs`, and start at `t = 0`. The validator enforces all three.

## Repository topology

```
RaidUI/
├── src/parser/
│   ├── replayBuilder.js          # builds PullReplay object from buffered events
│   ├── resampler.js              # resample() + resampleLerp() (sibling functions)
│   ├── coordNormalizer.js        # bbox + normalize() to [0,1]
│   └── schemaWriter.js           # writeJsonCompact / writeJsonPretty
├── contracts/session/
│   └── frame.schema.json         # PullReplay schema, v1
├── tools/
│   └── export-class-colors.js    # OKLCH-to-sRGB-to-C# generator (one-shot)
└── out/pulls/
    └── {pullId}.replay.json      # generated fixtures (gitignored)

game/clients/godot/
├── scenes/
│   ├── replay_main.tscn          # entrypoint scene (F6 to run)
│   └── world.tscn                # shared with live game (plane + lighting + HUD)
├── scripts/Core/
│   ├── GameState.cs              # autoload, signal layer; IngestReplay* methods
│   ├── World.cs                  # entity spawner, class_color metadata branch
│   └── CoordinateMapper.cs       # live-game server-to-Godot (NOT used by replay)
└── scripts/Replay/
    ├── ReplayLoader.cs           # main entry; load + validate + driver
    ├── WowClassColors.generated.cs  # OKLCH-derived sRGB Color constants
    ├── SelectionManager.cs       # autoload, selection bus
    ├── SelectionInput.cs         # LMB click + shift-click multi
    ├── HighlightRing.cs          # pulsing torus rings under selected orbs
    ├── ProfileCard.cs            # bottom-right card UI
    ├── HoverTooltip.cs           # cursor hover entity name/class/role
    ├── ScrubberHud.cs            # bottom-bar play/pause/speed/scrub
    ├── FreeCamController.cs      # WASD + Space/Ctrl+Space + RMB-look
    ├── DebugHud.cs               # top-left pull/time/speed/schema readout
    └── ReplayRaycast.cs          # static cursor-to-entity helper
```

## Running locally

**1. Generate a replay fixture (RaidUI side):**
```bash
cd D:\work\RaidUI
npm run parse-latest
```
Output: `out/pulls/{pullId}.replay.json` per pull. The console prints session counts and the latest pullId. Copy the absolute path of one .replay.json file.

**2. Set the replay path (Godot side):**
Open `D:\work\game\clients\godot\project.godot` in Godot 4.6 (mono build). Open `scenes/replay_main.tscn`. In the Inspector, find the `ReplayLoader` node and set `Replay Path` to the absolute path of the JSON file from step 1.

**3. Run the scene:**
Press F6 (Run Current Scene) in the Godot editor. Do NOT use F5 (which runs the live game's connect screen).

**4. Operator controls:**

| Action | Binding |
|---|---|
| Look | hold RMB + drag |
| Move horizontal | WASD |
| Move up | Space |
| Move down | Ctrl+Space |
| Boost (5x) | hold Shift |
| Play/pause | ↑ |
| Speed up | → (cycles 0.25x → 0.5x → 1x → 2x → 4x) |
| Speed down | ← |
| Seek | drag the bottom scrub bar (auto-pauses, restores prior pause state on release) |
| Hover info | mouseover an orb (tooltip shows name + class + role) |
| Select | LMB on an orb (replaces existing selection) |
| Multi-select | Shift+LMB (toggles entity in/out of selection) |
| Toggle highlight | `[Highlight]` checkbox in the bottom-right profile card |

## Slice timeline

| Slice | Date | Status | Commits |
|---|---|---|---|
| 0: solo replay + validation | 2026-05-05 | Shipped | RaidUI `685a187`, game `c1fd248` |
| 0.5: legacy HUD cleanup | 2026-05-06 | Shipped | folded into game `e9c9638` |
| 1: free-cam + scrubber + tooltips + selection + card (merged) | 2026-05-06 | Shipped | game `e9c9638` |
| 2: drag-rect multi + first real visualization (defensive timeline) | TBD | Pending | n/a |
| 3: trinkets + target-switching (parser whitelist work) | TBD | Pending | n/a |
| 4: command palette overlays | TBD | Pending | n/a |
| 5: live RaidUI to Godot scrubber DJ via WS | TBD | Pending | n/a |
| Multiplayer (Lumberjacks integration) | TBD | Pending | n/a |
| VR/AR (Quest 3 passthrough) | Stretch | Pending | n/a |

## What is NOT in scope today

- No multiplayer co-presence (peers seeing each other's cameras, pings, scrubber state)
- No live RaidUI to Godot bridge (replay is loaded from disk; web viewer drives nothing)
- No drag-rect or class-filter selection
- No defensive / trinket / target-switching visualizations (data hooks ready, viz not built)
- No VR/AR rendering
- No event markers on the scrubber bar
- No runtime file picker for replay path (currently hardcoded absolute)

These are sequenced in the slice timeline above. The architecture (selection bus, replay-as-event-source, schema-disciplined data layer) is shaped to absorb them additively.

## Reference

- Article: [The Tight Loop](https://steppeintegrations.com/articles/the-tight-loop/). Field test for the Mech Suit Methodology, written from this build.
- Retrospective: `clients/godot/docs/retro/2026-05-06-godot-replay-slices-0-1.md` in this repo.
- Parent methodology: [The Mech Suit Methodology](https://steppeintegrations.com/article/mech-suit-methodology-final.html).
