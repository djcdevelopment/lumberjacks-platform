# ComfyStewardView

Reads a Valheim world save file and answers the question that stops being
walkable once a server gets big: where is everyone actually building, and
who owns it.

## What it is

A standalone Java tool — separate repository, separate build, nothing to do
with the mod or the Gateway. It's a single fat JAR that parses a Valheim
world `.db` file and runs as a small local web app: a REST API plus a
browser dashboard, both on `localhost:7080` by default.

Main dashboard tabs: Map, Portals, Players, Economy, Tombstones, Signs,
Dropped, Alerts, Structures, Creatures, Coin Caches, Server Issuers, Guild
Gear, Selection.

REST surface (partial — the README lists the full set): `/api/v1/status`
(parse progress — `{"done": true}` when ready), `/api/v1/heatmap?type=...`
(per-500m-cell density, e.g. `BUILDING`), `/api/v1/points?cat=...` (owned
points by category — `PORTAL`, `CONTAINER`, `BED`, and more — filterable by
bounding box), plus summaries, economy, tombstones, signs, beds, dropped
items, sectors, structures, alerts, and entity-contract routes.

There's also a batch analytics mode for very large worlds: it builds a
DuckDB cache and pre-rendered map-layer tiles instead of sending raw point
clouds to the browser (`--rebuild-cache`, `--render-layers`, `--batch-only`
flags), with its own DB-backed drilldown routes under `/api/v1/db/...` and
`/api/v1/rendered/...`.

Around 19 standalone Java command-line probes also sit at the repo root
(`DecodeInv`, `DeepProbe`, `GeoAnalysis`, `ItemHashResolve`,
`LocationProbe`, `MetricsExtractor`, `NearbyProbe`, and more) for one-off
forensic digs — coin-cache tracing, item-hash resolution, signs/geo
analysis. They work; see "What's rough" for why they're hard to pick up
today.

## What it is NOT

Not a live/in-game view. It reads a `.db` snapshot after the fact — it has
no idea what's happening on a server right now, and it doesn't write
anything back into a world.

Not part of this repo. It's a fully separate GitHub repository
(`ComfyStewardView`) with its own build, its own issues, and — importantly
— its own license, which is not the same license that covers Baseline. See
"License & privacy."

## Status

The public repo builds a fat JAR that parses a real world `.db`, serves a
localhost API, and draws a browser heatmap — it has been used to steward
real high-player-count worlds. What's missing is a quickstart for the ~19
GM probe utilities: they work, but today you have to read the Java source
to know what each one answers.

## Run it in about 15 minutes

From a clone of `ComfyStewardView` (a separate checkout, not inside
`baseline`):

1. Get a **copy** of a Valheim world save (`.db` file) — never point this
   at a live/in-use save.
2. `powershell -ExecutionPolicy Bypass -File .\Start-Viewer.ps1` — this is
   the tested path for non-developers. It asks for your world `.db` path,
   checks for Java 17+, downloads a local Maven into `.tools/` if you don't
   have one on `PATH`, installs the bundled
   `valheim-save-tools-fixed.jar` into your local Maven cache if needed,
   builds the jar, starts the viewer, waits for it to finish loading, and
   opens your browser automatically.
3. If it doesn't open automatically, or you'd rather run it by hand:
   `cd viewer && mvn package -DskipTests`, then
   `java -Xmx3g -jar viewer\target\world-viewer-1.0.0.jar path\to\world.db --port 7080 --no-browser`,
   then open `http://localhost:7080/`.

If startup fails with `NoClassDefFoundError: kotlin/jvm/internal/Intrinsics`,
the jar was built without the Kotlin stdlib — pull the latest
`viewer/pom.xml` and rebuild.

## What you'll see

The dashboard loads once parsing finishes (`/api/v1/status` reports
`"done": true` — expect roughly 15–30 seconds for a ~1.3 GB save). The Map
tab shows a heatmap of the world; the other tabs are sortable/filterable
tables for portals, players, economy items, tombstones, signs, dropped
items, structures, alerts, and a few forensic views (coin caches, server
issuers, guild gear). Everything is read from the `.db` you pointed it at —
nothing here talks to a live server.

## What's rough

- **The ~19 root-level probe utilities have no quickstart.** Each one is a
  real, working, standalone Java tool, but there's no single page saying
  what each answers or how to invoke it — you have to read the `.java`
  source. This is SV-2 below.
- **A long, honest backlog lives in the README's "Still to build"
  section** — a cached ZDO explorer UI, semantic location masks, build
  leaderboards by creator/prefab/zone, container wealth reports,
  creator-ID inference, portal hub analysis, a local 3D prefab viewer,
  per-container inventory drill-down in the live UI, alert noise reduction
  for orphaned portals, and a handful of unresolved prefab/hash IDs for
  specific item variants. None of that is secretly done — the README's own
  list is accurate.
- **Batch analytics mode needs real memory** (`-Xmx6g` in the documented
  examples) — it's built for million-ZDO investigations, not a quick look.

## First tasks

- **SV-1 — Run it against a copy of any world `.db` and post one heatmap
  screenshot.** Done when: a heatmap image from your own world is in the
  thread, with the exact command you used and anything that tripped you up.
- **SV-2 — Write the missing probe-utilities quickstart.** Done when: each
  of the ~19 probe utilities has one line saying what it answers and one
  copy-pasteable invocation, and someone who has never opened the Java
  source can pick the right probe from that page alone.

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

**Not BSL 1.1 — this is a separate repository with its own, different
terms.** `ComfyStewardView`'s own `LICENSE.md` states: "Copyright (c) 2026
DJC Development. All rights reserved... No permission is granted to copy,
scrape, train on, distribute, modify, or commercialize any part of it
without explicit written permission and a paid license from the copyright
holder," and explicitly rules out automated harvesting, dataset extraction,
and model-training use. The repo is public to read on GitHub; that is not
the same as being free to reuse. Read `LICENSE.md` in that repo before you
build on it, and don't assume Baseline's BSL 1.1 terms apply here.

Privacy: run it against **copies** of world files, never a live save —
nothing in the tool enforces that for you, it's on you. Output can contain
real player names (portal owners, sign authors, tombstone owners, chest
contents by inferred owner). Share aggregates and screenshots in the
thread, not raw dumps or the `.db` itself.
