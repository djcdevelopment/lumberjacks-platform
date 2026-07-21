# Slice 0 + 1 Retrospective: Godot 3D Replay Viewer

**Dates:** 2026-05-05 (slice 0 plan, parser-side build, slice 0 commit), 2026-05-06 (slice 1 plan, build, commit, slice 1 acceptance walk-through), 2026-05-07 (this retro).

**Repos touched:** `D:\work\RaidUI\.claude\worktrees\silly-bouman-0d0c0f` (parser side, feature worktree), `D:\work\game\clients\godot` (Godot consumer, master branch).

## What shipped

| Slice | Commit | Repo | Files | Insertions / Deletions |
|---|---|---|---|---|
| 0 (parser side) | `685a187` | RaidUI feature branch | 5 | +370 / -20 |
| 0 (Godot side) | `c1fd248` | game master | 6 | +572 / 0 |
| 1 (Godot side) | `e9c9638` | game master | 12 | +773 / -3 |

**Slice 0 acceptance:** 21 orbs gliding on a flat ground plane through 3:45 of a Vorasius pull, schema mutation produces red REPLAY FAILED panel with diagnostic message and zero spawned orbs. Both confirmed.

**Slice 1 acceptance:** free-cam (RMB-look + WASD + Space + Ctrl+Space + Shift boost), scrubber HUD (↑ play/pause, ←/→ speed cycling, drag-to-seek), hover tooltips, single + shift-click multi-select, profile card with `[Highlight]` checkbox toggling per-entity ring visibility, slice 0.5 legacy HUD cleanup. All six checks confirmed first try.

## Decisions that paid off

**Additive `IngestReplay*` methods on `GameState`.** Three new public methods (`IngestReplayEntity`, `IngestReplayPosition`, `IngestReplayRemove`) that emit the same signals the live-game flow already used. World.cs subscribers required no changes. The live-game path is untouched by replay code. This is the load-bearing decision; without it the slice would have required forking World.cs or building a parallel signal path.

**Replay-specific coordinate mapper, written inline.** The existing `CoordinateMapper.cs` was for a different game's server-coord convention (+Z=North). Generalizing it for both was tempting and would have been wrong. ReplayLoader's `NormalizedToWorld` is a private 4-line method. Total complexity: low. Cross-coupling with the live-game's mapper: zero.

**Parser-side interpolation as a sibling function.** `resampleLerp` lives next to the original `resample` in `src/parser/resampler.js`. Existing carry-forward tests stay green. The two callers in `replayBuilder.js` switched to the new function in one line each. Reversible if needed. Six new unit tests cover lerp behavior including the grid-anchored alpha (no-drift) case.

**Schema version as a `const "v1"` in JSON Schema.** Required field. Validator rejects on absence or mismatch. The mutation test (change `v1` to `v123231`) verified loud failure end to end before slice 0 closed.

**OKLCH-to-sRGB at port time.** A 100-line one-shot Node script at `tools/export-class-colors.js` (RaidUI repo) consumes the canonical OKLCH source from `src/overlay/builtins/player-dots.overlay.jsx` and emits a generated C# file with `Color(r, g, b)` literals. Both 2D canvas dots and 3D Godot orbs trace to the same OKLCH source. Conversion is reproducible, deterministic, and runs once. The generated file (`scripts/Replay/WowClassColors.generated.cs`) is committed.

**`SelectionManager` as a single autoload bus.** Visual layer (`HighlightRing`) and UI layer (`ProfileCard`) are independent subscribers. Future visualization toggles (defensive timelines, trinkets, target-switching) slot into ProfileCard's per-entity sections without touching selection logic.

**Closest-point-to-ray hit-test instead of physics raycasting.** Live-game `player.tscn` does not include collision shapes. Adding them would have required scene file changes (touching live-game state). The ray-to-point distance algorithm in `ReplayRaycast.cs` is twenty lines, brute-force across 21 entities, runs at 60Hz with no measurable cost. Same interface as physics raycasting, swappable later if entity counts grow.

**`HideLegacyHud` as a `_Ready`-time switch.** Slice 0.5 cleanup. Hides world.tscn's CanvasLayer named "HUD" once replay drives. Single line, one direction (replay hides live HUD), no live-game scene mutation.

## Decisions that got lucky

**The resampler was a pure function.** If `resample` had been inlined inside `replayBuilder.js`'s frame loop, switching to lerp interpolation would have been a refactor. It was already a pure function with one job, so the swap was three lines. This was not foresight; it was someone (past me) writing a clean module that paid off later.

**`world.tscn` had a clean `Ground` node and a `HUD` CanvasLayer.** Slice 0 floor color tweak was a single `material_override` on the `Ground` node from `replay_main.tscn`. Slice 0.5 cleanup was a single `Visible = false` on the `HUD` CanvasLayer. Both inheritance overrides, neither touched live-game files.

**The existing follow-cam was a Camera3D with a simple pivot offset.** Replacing the pivot-relative cam with a free-flying Camera3D in slice 1 was straightforward (attach FreeCamController script to the Camera3D node directly). If the test game had built a complex camera rig with multiple constraints, slice 1's free-cam would have been more invasive.

**`GameState` already used signal-based entity lifecycle for live updates.** The replay path emits the same signals the live path already used. Zero subscribers needed changes. If the live game had used direct method calls on World.cs instead, the replay would have needed a parallel API surface or a refactor.

## Friction surfaces

**Wrong UID guess in `replay_main.tscn`.** I inferred `uid="uid://world"` for `world.tscn` but the actual UID was `uid://world_scene`. Godot would have fallen back to path resolution with a warning, but I caught the mismatch on a recon read and removed the UID hint. Cost: zero. Lesson: do not guess UIDs.

**Camera framing multipliers were undersized in slice 0.** Initial `(0, diag * 0.7, diag * 0.5)` clipped roughly 20% of an 80yd arena's edges. Bumped to `(0, diag * 1.0, diag * 0.7)` for ~13% margin coverage. Cost: 1 line plus a comment block deriving the FOV math. Lesson: include the framing math in the comment so future tuning has the formula.

**`StructurePlacer.cs` from live-game runs in replay scene.** The `world.tscn` includes a `StructurePlacer` node that initializes regardless of replay mode. Output panel shows "StructurePlacer: Ready." Cosmetic, no functional impact, but worth tracking. Slice 0.5 could have addressed it; deferred.

**Down arrow is unbound.** Originally allocated to "set marker of interest" in the slice 1 plan. Markers descoped before code; the keybinding stayed unbound. No functional impact, but a small loose end.

**Hardcoded absolute fixture path in `replay_main.tscn`.** ReplayLoader's `ReplayPath` is set to a worktree-specific absolute path. Works for the current dev setup but breaks for any other developer or environment. Slice 2 should add a runtime file picker or a relative `res://` resource path (which would require copying the fixture into the Godot project tree).

## Patterns worth promoting

1. **Bus + decoupled consumers.** SelectionManager pattern. Anywhere there is "X drives N visualizations," put X behind a signal bus and let consumers subscribe. Adding a new visualization is additive only.

2. **Generated constants from canonical source.** OKLCH-to-sRGB script. Anywhere there is a single-source-of-truth for visual identity (colors, fonts, sizing constants), have a generator that emits per-platform constants files. Avoids retyping, avoids drift, makes the generation step visible in commits.

3. **Sibling functions, not refactor-the-original.** When extending behavior of a tested pure function, prefer a sibling with the new behavior over modifying the original. Existing tests stay green. Caller swap is a one-line decision. Reversible.

4. **Schema version as required `const`.** JSON Schema's `"const": "v1"` on a required field. Hard-fail on absence or mismatch. Cheaper than version negotiation, more honest than silent fallback.

5. **Closest-point-to-ray for cursor-to-entity.** When entity counts are small (under ~100), brute-force ray-distance is simpler than physics setup. Twenty lines, no scene changes, identical interface to physics raycasting.

6. **`HideLegacyHud`-style sub-scene overrides.** When sharing a sub-scene between two parents (live game vs replay), have the consumer hide pieces that do not apply rather than fork the sub-scene. Single line, single direction, reversible.

7. **Additive ingest methods on existing autoloads.** When two distinct event sources should drive the same downstream consumers (live network + replay), expose additional public methods on the existing autoload that emit its existing signals. Subscribers do not change. The live-game flow stays untouched.

## What slice 2 inherits

**Data already in `events[]`:** `SpellCastSuccess` events for whitelisted defensive/external/pot spells (per `DEFENSIVE_SPELL_IDS` in the parser), `SpellInterrupt`, `SpellDispel`, top-decile `SpellDamage`, `UnitDied`. Each event carries `position: {x, y, facingRadians, mapId}` where the source unit had a populated advanced-block. **Defensive timeline visualization can read directly with no parser changes.**

**Data NOT in `events[]`:**
- Trinket usage. Would require parser whitelist extension; trinkets are `SpellCastSuccess` with item-bound spell IDs not currently in the defensive list.
- Target-switching detection. Would require boss-target tracking logic on top of the existing damage stream.

**Deferred slice 1 items:**
- Drag-rect multi-select (shift-click is in)
- Class-filter macros ("select all healers")
- Down-arrow rebinding (markers descoped)
- Mouse capture during RMB-look (cursor stays visible)
- Selection persistence across pull boundary (resets on scene reload)
- Runtime file picker for replay path (currently hardcoded absolute)

**Architectural shape inherited:**
- `ProfileCard`'s per-entity section is the slot for visualization toggles. Defensive timeline, healing range circle, trinket usage timeline all add as new toggles in the same section.
- `HighlightRing`'s `_Process` loop tracking selected orbs is the template for any "render N decorations following N orbs" pattern.
- `ReplayLoader.SeekToMs` is the single seek point. Future timeline-based features (event markers in the scrubber bar, defensive cast pins, scrub-to-event) just call `SeekToMs(targetTime)`.

## Open architectural questions for slice 3+

**Lumberjacks integration.** Slice 1 closed without integrating the multiplayer network. Three options remain on the table from earlier planning:
1. Point existing SimulationClient at Lumberjacks
2. Add a sibling LumberjacksClient autoload alongside SimulationClient
3. Use Lumberjacks only for presence/coordination (cameras, scrubber DJ, highlights) while replay data stays file-loaded

Recommendation from earlier was option 3 (cleanest separation between replay-as-event-source and presence-as-coordination).

**Live RaidUI ↔ Godot WebSocket.** The original slice 5. RaidUI's `bin/serve.js` is a bare Node `http` server today; adding a WS upgrade handler is the precondition for the web tab driving the Godot scene's scrubber. Probably depends on a `ws` dependency.

**Encounter geometry.** Combat logs do not carry boss room geometry. Current renderer uses a flat plane bounded by the observed positional bounding box. A future "command palette" stylization could add procedural arena floor markers, AoE pre-fight indicators, etc. Out of scope for slice 2 and 3.

**VR/AR rendering.** Stretch from the original plan. Godot XR Tools + Quest 3 passthrough would slot in as another renderer subscribing to the same JSON contract. The selection bus, the profile card, and the scrubber all need spatial-UI versions. Genuinely a different scope conversation.

## Closing notes

The pace of this slice was load-bearing on three things that had nothing to do with the AI side:
1. The schema was already contract-stable.
2. The Godot project already used signal-based entity lifecycle.
3. The 2D overlay already had a canonical OKLCH palette.

Without all three, the same slice would have been weeks not days. The discipline that produced those upstream artifacts is the slow part. Once they exist, the loop closes fast.
