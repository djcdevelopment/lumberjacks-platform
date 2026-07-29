# Camera Flythrough → Gallery Pipeline

Fly a camera along waypoints pulled from a real world save, then cut the
recording into a gallery of what people actually built.

Source of truth: the public archive
[`github.com/djcdevelopment/comfy/tree/main/handoffs`](https://github.com/djcdevelopment/comfy/tree/main/handoffs).
Nothing described here runs in `baseline` today — see "Status."

## What it is

A four-segment pipeline, each segment written to be handed to a different
builder with none of the surrounding context — the only thing segments
share is the file format at the handoff point:

```text
world .db --[Segment 1: extraction]--> waypoints.json
waypoints.json + a running modded Valheim --[Segments 2 & 3: fly it]--> timeline.json (+ a screen recording)
recording + timeline.json --[Segment 4: cut it]--> gallery/ (clips + stills + gallery.json)
```

Recording itself isn't a segment — any screen recorder (OBS, Discord "Go
Live") works; the pipeline only needs the resulting video file and a
`timeline.json` saying which build was on screen at which second.

What's actually real, verified by reading it:

- **Segment 1 (extraction) is built.** `segment-1-emit-waypoints.py` (a
  69-line, standard-library-only script) ranks locations by build density
  via ComfyStewardView's `GET /api/v1/heatmap?type=BUILDING`, attributes
  each dense cell to a builder via `GET /api/v1/points?cat=PORTAL|CONTAINER|BED`,
  and writes `waypoints.json`. No parser change — pure composition of
  endpoints ComfyStewardView already exposes. It produced a real result
  once: the top 15 build clusters of a real world, each attributed to a
  real player.
- **`valheim-camera-proof/` is a working, separate proof-of-concept kit** —
  not Segment 3 itself, but the thing you're told to run *before*
  attempting it. It's a real 746-line BepInEx plugin (`Plugin.cs`) with
  console commands (`comfyproof_status`, `comfyproof_move`,
  `comfyproof_stills`, `comfyproof_variantstills`, `comfyproof_envs`, and
  more) and F8/F9 hotkeys that prove a given machine can load the target
  world, load BepInEx, find `Player.m_localPlayer`, teleport the camera,
  and write proof files and screenshots to disk.
- **Segment 2 (get into the world) is a brief only** — a checklist of
  manual human steps (copy the save, enable `-console`, install BepInEx,
  load the world, verify god/fly mode, confirm a throwaway plugin loads).
  No code beyond that throwaway test plugin.
- **Segment 3 (the actual flight-path mod that reads every waypoint, flies
  the camera, and writes `timeline.json`) is a brief only, and it is the
  real gap.** It has a precise C#/BepInEx spec (hotkey, ground-height
  resolution, teleport-first-then-glide, the exact `timeline.json` shape)
  but no code — the working proof kit above is not a substitute for it.
- **Segment 4 has real code, not just a brief.** `video_to_gallery.py`
  consumes a recording and `timeline.json` and writes one clip, one still,
  and a `gallery.json` manifest per timeline event. Its dry-run needs no
  media tools at all.

## What it is NOT

Not running in `baseline`, in any form, today. `baseline`'s repo root does
carry one static leftover — a real, committed `waypoints.json` from Era 16
(real coordinates, real builder names) — but nothing in this repo can
regenerate it, read it, or fly it. Treat it as a fossil, not a working
input.

Not a finished mod. There is no BepInEx plugin today that takes
`waypoints.json` in and flies a full route out — see Segment 3 above.

## Status

Not running. It was pruned from `baseline` on 2026-07-21 (the prune audit
lists the whole `handoffs/` tree, commit `cc322ee` is the last point it
existed here) and nothing here executes in this repo today. Every piece is
preserved in the public `comfy` repo, so this is a revival, not a rewrite.

## Run it today (about 10 minutes) — the one thing that actually runs

Nothing end-to-end runs yet. The one piece you can exercise right now,
with no Valheim, no Java, and no ffmpeg, is Segment 4's dry run, against
its own fixture:

1. Clone the public archive:
   `git clone https://github.com/djcdevelopment/comfy` (or just fetch the
   `handoffs/` folder).
2. From that checkout:
   `python .\handoffs\video_to_gallery.py flythrough.mp4 .\handoffs\timeline.sample.json --dry-run --duration 60`
   — this needs a placeholder `flythrough.mp4` path but no real video and
   no ffmpeg for a dry run.
3. Read `handoffs/segment-1-waypoints-from-world.md` and
   `handoffs/valheim-camera-proof/README.md` before touching CG-1 below —
   they're short and specific about what "done" looks like for each piece.

## What you'll see

The Segment 4 dry run prints what it *would* cut, without touching ffmpeg.
A real run (`--offset <seconds> --out gallery`, ffmpeg + ffprobe on `PATH`)
writes a `gallery/` folder with one clip and one still per timeline event
plus a `gallery.json` manifest. The camera proof kit, once built and
installed into a disposable Valheim + BepInEx install, writes JSON proof
files under `BepInEx/config/comfy-camera-proof-status.json` and
`...-move.json`, and screenshot folders under
`BepInEx/config/comfy-gallery-proof/` or `comfy-manual-captures/`.

## What's rough

- **Segment 3 — the actual flythrough mod — doesn't exist.** This is the
  real blocker for the whole pipeline; everything downstream depends on it.
- The camera proof kit's own README notes its dev checkout "currently does
  not have a compiler on `PATH`" — building it needs a C# compiler
  (Visual Studio Build Tools, Rider, or the .NET SDK) that isn't assumed
  to be there.
- Segment 2 is entirely manual human setup — nothing to automate there
  beyond what's already written down.
- The related, separately-built "control surface" (in-game submission →
  local review inbox → guild-bot command) is a **different** tool, covered
  by the Quest Submission Bridge one-pager — don't conflate the two just
  because they share a retirement date and a parent folder.

## First tasks

- **CG-1 — Revive Segment 1 against ComfyStewardView and produce a
  committed `waypoints.sample.json`.** Done when:
  `segment-1-emit-waypoints.py` runs against a current ComfyStewardView
  build and writes a `waypoints.sample.json` that Segment 3 (once it
  exists) can consume. This is the claiming task for this tool.

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

**Not BSL 1.1.** Every file in this pipeline lives in the public `comfy`
repository, which is MIT-licensed (`comfy/LICENSE`) — a separate, more
permissive license than Baseline's own BSL 1.1. If you revive Segment 1 or
build Segment 3 and later land it inside `baseline` itself, the copy that
lives here would fall under `baseline`'s BSL 1.1 instead; until then, what
you're forking is MIT.

Privacy: `waypoints.json` attributes real coordinates to real builder
names, and a flythrough shows other people's builds up close. Ask before
publishing a gallery of a world you don't own, and don't assume "it's just
a heatmap" — the whole point of this pipeline is to make specific builds,
tied to specific players, visible to people who were never on the server.
