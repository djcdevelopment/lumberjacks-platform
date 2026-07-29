# Quest Picker + Absorption Engine

Turns a guild's real quest tracker into one offline page where a player
checks the quests they're going after — and the game mod reads what they
picked.

## What it is

A three-step recipe under `recipes/quest-catalogs/`:

1. `harvest.py` reads `sources.json` (which guild, which source file, which
   adapter) and pulls a guild's tracker into one canonical catalog JSON plus
   an anomalies report. It has four adapters: `sheet-xlsx` and `ranger-xlsx`
   (read a guild's tracker workbook; need `openpyxl`), `gm-template` (read a
   hand-filled JSON already in catalog shape; standard library only), and
   `discord-export` (a deliberate stub — see "What's rough").
2. `render_quest_picker.py` folds every enabled catalog into one
   self-contained HTML page — standard library only, and the page itself
   has no dependencies, so it works from `file://` with no server and no
   network.
3. A player opens that page, filters/searches, checks the quests they care
   about, types their character name, and saves a personal
   `quest-view.json`. The game mod (`ComfyNetworkSense`) loads that file at
   runtime and can auto-capture evidence for kill-quests that have a
   trigger defined.

`validate.py` is the guardrail in between: point it at a catalog JSON and
it prints exactly what's broken (`X`) or worth a second look (`!`), and
exits non-zero on anything broken.

The contracts are `recipes/quest-catalogs/schema.md` (the catalog) and
`recipes/quest-catalogs/quest-view-schema.md` (the personal view).

## What it is NOT

Not a quest editor. There's no in-app way to create or edit a quest — you
can only harvest what a guild's tracker already contains, or hand-write a
catalog JSON in the `gm-template` shape. That authoring seam exists in code
(`adapt_gm_template` in `harvest.py`, and a disabled `rangers-example` slot
already sitting in `sources.json`) but nobody has ever exercised it — see
"What's rough."

It's also not a submission or turn-in tool. The picker only decides what a
player tracks. The guild's Discord bot, and the mod's own evidence-capture
flow, still do the actual turn-in.

## Status

Runs today against real guild tracker exports: the harvest, the validator,
and the picker all work end to end, and the mod reads the `quest-view.json`
the picker saves — from the folder the mod actually watches, which is not
the folder the picker currently tells you to use (see "What's rough").

## Run it in about 10 minutes

Run everything below from the repo root.

1. `pip install openpyxl` — only needed once; both real sources below are
   `.xlsx`.
2. `python recipes/quest-catalogs/harvest.py slayers-summons` — harvests one
   source by id. Drop the id to harvest every enabled source in
   `sources.json` at once (currently `slayers-summons` and
   `rangers-tracker`).
3. `python recipes/quest-catalogs/validate.py data/processed/quest-catalog-slayers.json`
   — optional, but it's your guardrail; it should print `PASS`.
4. `python recipes/quest-catalogs/render_quest_picker.py` — rebuilds
   `data/processed/quest-picker.html` from every enabled catalog.
5. Open `data/processed/quest-picker.html` directly in a browser (double-click
   it, or File > Open). No server, no network call, ever.

## What you'll see

A dark, Valheim-flavored page titled "Comfy Quest Picker": a search box and
filter rail on the left (Guild, Category, Proof type, Status), a quest list
on the right with evidence pills (📸 screenshot count, 🎞️ video alternative,
🔗 link, 🤜🤛 group turn-in, ⚡ auto-capture, 🌍 IRL, "auto-checked"), and a
footer with a Character-name field, an optional Discord field, and a "Save
quest-view.json" button that stays disabled until you've typed a name and
checked at least one quest. Clicking a row expands its full requirements
text and turn-in command. Your selection is remembered in the browser
(`localStorage`) between visits.

`harvest.py` also prints, per source, how many quests it found and how many
anomalies it flagged, and writes `<catalog>-anomalies.md` next to the
catalog JSON — read it. Nothing in there was "fixed" for you; it's a list
of questions for the guild to rule on (duplicate turn-in commands,
mismatched evidence counts between the requirements text and the bot
command, rows the harvester couldn't parse).

## What's rough

- **`gm-template-example.json` doesn't exist.** `sources.json` already has
  a slot for it (`rangers-example`, currently `enabled: false`, note: "Fork
  test: enable once Mistral fills a template. gm-template validates and
  passes through.") — but the file has never been written, so the
  GM-authoring path has never actually been exercised end to end. This is
  QP-1 below.
- **The picker's own save hint sends you to the wrong folder.** Both the
  picker page's footer text and `quest-view-schema.md` say to drop the
  saved file at `Valheim/BepInEx/config/comfy-control/quest-view.json`.
  The live mod actually reads it from
  `Valheim/BepInEx/config/comfy-network-sense/quest-view.json` (verified in
  `ComfyNetworkSense.cs`) — `comfy-control` was the folder name of an older,
  retired mod. A missing or misplaced file is not treated as an error by
  the mod, it just means no quests are tracked, so this fails silently.
  Use the `comfy-network-sense` path until the doc and the picker hint are
  fixed.
- **`discord-export` is a stub on purpose.** Running it raises immediately
  with a message saying so — it marks the seam where a future
  "harvest straight from a Discord channel export" adapter would plug in.
  Not a bug, just not built.
- **No catalog JSON ships committed.** `data/processed/` only carries the
  pre-rendered `quest-picker.html`; the catalog JSON and anomalies report
  are regenerated locally every time you run `harvest.py`.

## First tasks

- **QP-1 — Write `gm-template-example.json`.** Done when: `validate.py`
  accepts it, and a GM can author a quest through the `gm-template` seam
  without touching `harvest.py`. The target path and adapter are already
  wired in `sources.json` (`rangers-example`) — you're writing the missing
  input file, not new code.
- **QP-2 — Harvest your own guild's tracker export.** Done when: a new
  catalog JSON renders correctly in the picker, and every anomaly the
  harvest reports has a real explanation — none left unexplained.

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

BSL 1.1 public-source posture — this code lives in `recipes/` in this
repo, covered by the root `LICENSE` / `LICENSING.md` (converts to
AGPL-3.0-only at the stated change date; see `LICENSING.md` for the
community-steward safe harbor).

Privacy: harvested catalogs carry real content, verbatim. `sources.json`
points `slayers-summons` and `rangers-tracker` at real guild Google Sheets,
and the committed `data/raw/*.xlsx` trackers and the committed
`data/processed/quest-picker.html` are real harvested data, not samples —
member names and turn-in details pass through the harvester unchanged, by
design ("content passes through verbatim... never silently fixed"). There
is no synthetic-only version to point people at today. Treat any catalog
JSON, anomalies report, or `quest-view.json` this recipe produces as
carrying real people's names.
