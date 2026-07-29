#!/usr/bin/env python3
"""Generate the synthetic sample guild tracker for the quest-picker zip.

Every name here is invented. The workbook exercises the sheet-xlsx adapter's
features on purpose: image slots, the video/link/group emoji grammar, an
auto-checked row, a deliberate credited-name mismatch, and a name-only row —
so the anomalies report demonstrates its value on first run.

Usage: python make_sample_tracker.py [out.xlsx]   (default: sample-guild-tracker.xlsx beside this file)
Requires: openpyxl
"""
import os
import sys

try:
    from openpyxl import Workbook
except ImportError:
    raise SystemExit("needs openpyxl: pip install openpyxl")

HERE = os.path.dirname(os.path.abspath(__file__))

ROWS = [
    # Name | Coopable? | Category | Turn-in Requirements | Bot Template
    ("Greydwarf Cull", True, "Hunting",
     "Slay 20 greydwarves in one outing. \U0001F4F8 one screenshot of the final tally.",
     "/summons summons_type: Greydwarf Cull image: summons_notes:"),
    ("Bronze Bell", False, "Crafting",
     "Forge a full bronze set. \U0001F4F8\U0001F4F8 before and after at the forge.",
     "/summons summons_type: Bronze Bell image: image2:"),
    ("Sail the Serpent Sea", True, "Exploration",
     "Cross open water at night with a serpent sighting. \U0001F39E video counts instead of "
     "screenshots. \U0001F517 clip link.",
     "/summons summons_type: Sail the Serpent Sea summons_url: summons_notes:"),
    ("Longhouse Raising", True, "Building",
     "Raise a longhouse with at least two clanmates. \U0001F91C\U0001F91B group turn-in.",
     "/summons summons_type: Longhouse Raising image: participants:"),
    ("First Winter", True, "Milestones",
     "Survive to day 30. No submission, auto-checked.",
     "No submission, auto-checked"),
    # deliberate mismatch: command credits "Troll Scuffle", quest is "Troll Tussle"
    ("Troll Tussle", False, "Hunting",
     "Defeat a troll without armor. \U0001F4F8 proof at the corpse.",
     "/summons summons_type: Troll Scuffle image:"),
    # name-only row: demonstrates the skipped-row anomaly
    ("Retired: Boar Parade", None, None, None, None),
]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "sample-guild-tracker.xlsx")
    wb = Workbook()
    ws = wb.active
    ws.title = "Summons list for bot"
    ws.append(["Name", "Coopable?", "Category", "Turn-in Requirements", "Bot Template"])
    for row in ROWS:
        ws.append(list(row))
    wb.save(out)
    print(f"wrote {out} ({len(ROWS)} rows)")


if __name__ == "__main__":
    main()
