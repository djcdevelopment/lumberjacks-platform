# Quest Picker + Absorption Engine — run it in about 10 minutes

You need: **Python 3.9+** and one package: `pip install openpyxl`. Nothing else. No server,
no network, no account.

This zip ships already-built output (`quest-picker.html` — open it in any browser right
now), plus everything to rebuild it yourself from the synthetic sample tracker:

```
quest-picker.html          the picker, pre-rendered from the sample catalog
quest-catalogs/            the real tools, verbatim from the project
  harvest.py               reads a guild tracker workbook -> catalog JSON + anomalies report
  render_quest_picker.py   catalogs -> one self-contained HTML picker
  validate.py              manual guardrail checks for a catalog
  sources.json             which guild / which file / which adapter (the config seam)
  schema.md                what a catalog looks like and why
  quest-view-schema.md     what the picker saves for the game mod
sample/
  make_sample_tracker.py   regenerates the synthetic workbook
  sample-guild-tracker.xlsx  the fake guild tracker (every name invented)
  quest-catalog-sample.json  harvested from it
  quest-catalog-sample-anomalies.md  what the harvester flagged — read this one
```

## Rebuild everything yourself

From this folder:

```
python quest-catalogs/harvest.py sample-guild
python quest-catalogs/render_quest_picker.py quest-picker.html sample/quest-catalog-sample.json
```

Then open `quest-picker.html`. Pick quests, enter a character name, save — you get a
`quest-view.json`, which is exactly what the game mod reads to know what to track.

## The part worth your attention

Open `sample/quest-catalog-sample-anomalies.md`. The sample data contains **deliberate
mistakes** (a bot command crediting the wrong quest name, a row with a name and nothing
else). The harvester never silently fixes anything — it copies your content verbatim and
writes questions for a human. That report is the tool's whole philosophy.

## Use your own guild's tracker (first task QP-2)

1. Export your tracker as `.xlsx` with columns `Name | Coopable? | Category |
   Turn-in Requirements | Bot Template` (or look at `harvest.py` — adapters are ~60
   lines; writing one for your format is a normal contribution).
2. Add a source entry in `quest-catalogs/sources.json` pointing at your file.
3. `python quest-catalogs/harvest.py <your-source-id>` and read YOUR anomalies report.

Post what happened — including errors, verbatim — in the quest-picker thread.
