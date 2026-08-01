"""Extract the run-scoped slice of the client perf receipts into a run bundle.

The frozen build already writes perf-hitches.jsonl and perf-sections.jsonl into
the client telemetry directory, but the cutover orchestrator never collected
them, so motion-quality hitch evidence only exists in the live (appended,
eventually rotated) client directory. This lifts the window belonging to one
run id into that run's retained directory.

Window bounds come from the run's own scenario receipts, so the slice is
defined by the run rather than by a hand-picked clock range.

Read-only against the client directory; writes only into the run directory.

Usage:
  python Export-MotionPerfWindow.py <run-id> [<run-id> ...] [--client omen]
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
from pathlib import Path

DEFAULT_TELEMETRY = Path(
    r"C:\Program Files (x86)\Steam\steamapps\common\Valheim"
    r"\BepInEx\config\comfy-network-sense")
RUNS_ROOT = Path(r"C:\work\baseline\fieldlab\runs\native-valheim")
PERF_FILES = ("perf-hitches.jsonl", "perf-sections.jsonl")
# Valheim's own session lifecycle (respawn, menu load, PlayFab auth) brackets
# the scenario; widen slightly so those bounding stalls stay in the slice and
# can be named rather than silently excluded.
PAD = datetime.timedelta(seconds=45)


def parse_ts(value: str) -> datetime.datetime:
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))


def read_jsonl(path: Path) -> list[dict]:
    rows = []
    if not path.exists():
        return rows
    with path.open(encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def run_window(run_dir: Path, client: str, run_id: str):
    """Bound the run from its own scenario receipts."""
    receipts = read_jsonl(run_dir / client / "native-cutover-scenario-receipts.jsonl")
    stamps = [parse_ts(r["timestamp_utc"]) for r in receipts
              if r.get("run_id") == run_id and r.get("timestamp_utc")]
    if not stamps:
        return None
    return min(stamps) - PAD, max(stamps) + PAD


def export(run_id: str, client: str, telemetry: Path) -> dict:
    run_dir = RUNS_ROOT / run_id
    if not run_dir.is_dir():
        raise SystemExit(f"no such run directory: {run_dir}")

    window = run_window(run_dir, client, run_id)
    if window is None:
        raise SystemExit(
            f"{run_id}/{client}: no scenario receipts, cannot bound the window")
    start, end = window

    out_dir = run_dir / client
    out_dir.mkdir(parents=True, exist_ok=True)
    receipt = {
        "schema_version": 1,
        "event_type": "motion_perf.window_exported",
        "run_id": run_id,
        "client": client,
        "source_directory": str(telemetry),
        "window_start_utc": start.isoformat().replace("+00:00", "Z"),
        "window_end_utc": end.isoformat().replace("+00:00", "Z"),
        "files": {},
    }

    for name in PERF_FILES:
        src = telemetry / name
        rows = [r for r in read_jsonl(src)
                if r.get("timestamp_utc")
                and start <= parse_ts(r["timestamp_utc"]) <= end]
        dst = out_dir / name
        with dst.open("w", encoding="utf-8", newline="\n") as fh:
            for r in rows:
                fh.write(json.dumps(r, separators=(",", ":")) + "\n")
        receipt["files"][name] = {
            "source_exists": src.exists(),
            "rows_in_window": len(rows),
            "written_to": str(dst),
        }
        print(f"  {run_id}/{client}: {len(rows):5d} rows -> {name}")

    (out_dir / "motion-perf-window.json").write_text(
        json.dumps(receipt, indent=2), encoding="utf-8")
    return receipt


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("run_ids", nargs="+")
    ap.add_argument("--client", default="omen")
    ap.add_argument("--telemetry", default=str(DEFAULT_TELEMETRY))
    args = ap.parse_args()

    telemetry = Path(args.telemetry)
    if not telemetry.is_dir():
        raise SystemExit(f"telemetry directory not found: {telemetry}")

    for run_id in args.run_ids:
        export(run_id, args.client, telemetry)
    return 0


if __name__ == "__main__":
    sys.exit(main())
