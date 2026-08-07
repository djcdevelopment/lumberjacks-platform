# World Photography → Gallery Pipeline

Find every structure in a world save, place a camera at each one from its own
geometry, and photograph them unattended into a ranked gallery.

This entry used to describe a four-segment *flythrough* pipeline that did not
run. That plan was **superseded rather than revived** — what exists now shoots
stills, not video, and it runs. The history is kept below because the archive
still holds the original briefs and someone may want them.

## What it is

Two halves in two public repos, joined only by files on disk:

```text
world .db  --[ComfyStewardView cache]-->  DuckDB
DuckDB     --[scan_clusters.py]--------->  clusters.json   (1,833 structures)
clusters   --[plan_shots.py]------------>  shotplan.tsv    (6 frames each)
shotplan   --[BepInEx plugin, in-game]-->  PNGs + receipts.jsonl
receipts   --[build_valheim_index.py]--->  index.json + webp
index      --[score_images.py]---------->  a ranked, filterable gallery
```

- **The planning, scoring and gallery half is in this repo**, at
  [`tools/selfie-stick/`](../../../../tools/selfie-stick/). It clusters building
  pieces in 3-D on a 16 m grid, ranks each structure as a camera subject, and
  derives a standoff distance, bearing, elevation and time of day from its
  bounding box. No terrain data is needed: a cluster's lowest piece *is* ground
  at that build.
- **The in-game half is a BepInEx plugin in the public `comfy` archive**, at
  `handoffs/valheim-camera-proof/`. It reads the shot plan as tab-separated text
  (it has no JSON parser and scrapes with regex), teleports, aims, forces the sun
  and weather, checks the view is not blocked, captures, and writes one JSON
  receipt per frame. It is kept out of this repo on purpose, so that claiming the
  in-game half stays a real, available piece of work.

The receipt is the load-bearing part: it records `planned` (what the planner
asked for), `placed` (where the engine actually put the player after its own
ground clamp) and `lens` (where the camera was, ~1.6 m above the player origin),
plus an occlusion raycast. That is what lets a frame be audited a week later
instead of trusted on faith.

## What actually runs

Verified on **2026-08-06**: 161 structures photographed across 54 unattended
sessions, **1,411 frames**, none of them framed by a human. Scanning a world and
producing a ranked shot list takes about two minutes; capture is unattended after
that and scales with how many structures you asked for.

The write-up, with the arithmetic and the real output of each stage:
**<https://djcdevelopment.github.io/baseline/selfie-stick/>**

## What it is NOT

**Not packaged, and not something a stranger can run tonight.** There is no
download. You need a ComfyStewardView analytics cache for the world, a Valheim +
BepInEx install with the plugin built from the archive, and — only if you want
frames scored — a local CLIP ViT-L/14 with a LAION aesthetic head. Nothing about
that is hard; it is simply not assembled for anyone else yet. That is the gap.

**Not a video pipeline.** There is no flythrough and no ffmpeg step. Segment 4's
`video_to_gallery.py` still exists in the archive and still dry-runs, but the
stills approach means it is unlikely to be built as specified.

**Not a taste engine.** The aesthetic head sorts competently and is confidently
wrong at the edges. It makes a 1,411-frame pile reviewable; it does not know
which photograph is good.

## Status

`local-only`. Runs end to end on the operator's machine; nothing is published.

Original prune: `d75ffb2` (2026-07-21); the last ref containing the whole
`handoffs/` tree is `57654fd`. Since 2026-07-29 the archive's raw material —
both scripts, the segment briefs, the two sample fixtures — sits unwired in this
repo at `recipes/camera-gallery/` with provenance in its `PROVENANCE.md`. The
camera plugin itself stays archive-only.

## Where the old segments went

| Segment | Then | Now |
| --- | --- | --- |
| 1 — extraction | 69-line script over ComfyStewardView's heatmap endpoints; produced a real top-15 once | **Superseded.** `scan_clusters.py` reads the DuckDB cache directly and finds 1,833 structures with true 3-D bounding boxes |
| 2 — get into the world | brief only, manual checklist | **Superseded.** `Invoke-OrbitCapture.ps1` arms the plugin and launches the game |
| 3 — the flight-path mod | brief only, *"the real gap"* | **Built.** The proof kit was 746 lines when this page was first written and is **1,787** today — camera boom, aim-at-target, an unattended orbit runner, per-frame receipts |
| 4 — cut the video | `video_to_gallery.py`, real code | **Not used.** Stills replaced the recording; the gallery is built from receipts instead |

## What's rough

- **Dense forest still eats the camera.** The occlusion raycast catches a wall
  between lens and subject. It does not catch a pine branch across a third of
  the frame, and no amount of standoff fixes a tree closer than the building.
- **Prefab names are hashes.** The cache stores `hash:538325542`, not
  `wood_wall`, so "dominant material" is a stable ID rather than a word. The
  offline `classification.json` does not help — it holds 617 *item* names and no
  building pieces. See CG-2.
- **Builder names are IDs.** Attribution works, but turning an ID into a name
  needs records that only exist in the running server.
- **Structure names come from a vision model** reading the photograph, because
  the metadata cannot supply them. It is a good trick and it is not authoritative.
- The related in-game submission → review inbox → guild-bot tool is a
  **different** thing, covered by the Quest Submission Bridge one-pager. Don't
  conflate them just because they share a parent folder and a retirement date.

## First tasks

- **CG-1 — Bring the camera plugin into this repo so the capture half runs
  here.** Done when the BepInEx plugin builds from a checkout of this repo and a
  capture run completes without reaching into the `comfy` archive. This is the
  claiming task for this tool. *It supersedes the old CG-1, which asked for a
  `waypoints.sample.json` from Segment 1 — `tools/selfie-stick/` already emits
  that.*
- **CG-2 — Resolve prefab hashes to names.** Done when a one-time in-game dump of
  `ZNetScene`'s prefab table lands as a committed lookup and the scanner reports
  a material name instead of a hash.

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

**The two halves are licensed differently.** The `comfy` archive is MIT
(`comfy/LICENSE`); `tools/selfie-stick/` in this repo is BUSL-1.1 like the rest
of Baseline. If you build the plugin and land it here, that copy falls under
BUSL-1.1; what you fork from the archive is MIT.

Privacy: this photographs other people's builds, and the scan attributes real
coordinates to real builders. Three things follow, and they are deliberate:

- `tools/selfie-stick/out/` is **gitignored**. Regenerating it takes two minutes;
  publishing someone's home coordinates to save a rerun is not a trade worth
  making.
- Every record the indexer emits is marked `"published": false` by default —
  ingest is automatic, exposure is not.
- The public write-up withholds coordinates and creator IDs everywhere, including
  inside its code samples.

Ask before publishing a gallery of a world you don't own.
