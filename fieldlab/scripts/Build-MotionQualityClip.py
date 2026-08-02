"""Compose the C9 side-by-side observer clip from two whole-run recordings.

Each client records its own screen for the whole run. This trims each recording
to the window where that client was the OBSERVER, burns in a telemetry readout
built from that client's own motion-authority receipts, and stacks the two
panels into one clip.

Deliberately no cross-machine clock sync: each panel is trimmed and annotated
against the machine that produced it, so the panels are two independent,
internally consistent views rather than a claim of simultaneity.

Usage:
  python Build-MotionQualityClip.py --run-dir <run> \
      --omen-clip <mp4> --omen-receipt <json> \
      --i5-clip <mp4> --i5-receipt <json> \
      --output <mp4>
"""
from __future__ import annotations

import argparse
import datetime
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

# Which action makes each client the observer of the other's motion.
OBSERVER_ACTIONS = {
    "omen": ("omen-c8-motion-observe-two", "omen-c6-observe-two"),
    "i5": ("i5-c8-motion-observe-one", "i5-c6-observe-one"),
}
DRIVER_LABEL = {
    "omen": "OMEN observing  -  i5 drives north",
    "i5": "i5 observing  -  OMEN drives east",
}
# Events worth flashing on the video, with how they should read to a reviewer.
EVENT_BANNER = {
    "hold_entered": "HOLD (frame aged out)",
    "motion_gap_observed": "GAP in sequence",
    "target_rejected": "TARGET REJECTED (>30m guard)",
    "reliable_resync_queued": "RESYNC queued",
    "reliable_resync_applied": "RESYNC applied",
    "reliable_resync_receipt": "RESYNC receipt",
    "teleport_resync_queued": "TELEPORT announced",
    "remote_motion_rejected": "REMOTE MOTION REJECTED",
}
BANNER_HOLD_SECONDS = 1.5


def parse_ts(value: str) -> datetime.datetime:
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))


def read_jsonl(path: Path, run_id: str | None = None) -> list[dict]:
    rows: list[dict] = []
    if not path.exists():
        return rows
    # Windows PowerShell 5.1 writes UTF-8 JSON with a BOM. Accept that at the
    # evidence boundary instead of requiring operators to rewrite a retained
    # capture receipt before it can be composed.
    with path.open(encoding="utf-8-sig", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            if run_id is None or row.get("run_id") == run_id:
                rows.append(row)
    return rows


def observer_window(run_dir: Path, client: str, run_id: str):
    """(start, end) UTC of this client's observer action, from its receipts."""
    receipts = read_jsonl(
        run_dir / client / "native-cutover-scenario-receipts.jsonl", run_id)
    for action_id in OBSERVER_ACTIONS[client]:
        start = end = None
        for row in sorted(receipts, key=lambda r: r["timestamp_utc"]):
            if row.get("action_id") != action_id:
                continue
            if row.get("state") == "action_started":
                start = parse_ts(row["timestamp_utc"])
            elif row.get("state") in ("completed", "failed") and start is not None:
                end = parse_ts(row["timestamp_utc"])
                break
        if start is not None and end is not None:
            return start, end
    raise SystemExit(
        f"{client}: could not bound any observer action "
        f"{OBSERVER_ACTIONS[client]} in {run_dir}")


def ass_time(seconds: float) -> str:
    seconds = max(0.0, seconds)
    hours, rem = divmod(seconds, 3600)
    minutes, secs = divmod(rem, 60)
    return f"{int(hours)}:{int(minutes):02d}:{secs:05.2f}"


def escape_ass(text: str) -> str:
    return text.replace("\\", "\\\\").replace("{", "(").replace("}", ")")


def build_ass(path: Path, client: str, events: list[dict],
              window_start: datetime.datetime, duration: float) -> None:
    """One subtitle track: a persistent counter line plus event banners."""
    header = f"""[Script Info]
ScriptType: v4.00+
PlayResX: 1280
PlayResY: 720
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Head,Consolas,26,&H00FFFFFF,&H00000000,&H96000000,1,3,0,0,8,16,16,12,1
Style: Meter,Consolas,22,&H00E0E0E0,&H00000000,&H96000000,0,3,0,0,1,16,16,12,1
Style: Flash,Consolas,26,&H0000E5FF,&H00000000,&HB4000000,1,3,0,0,2,16,16,54,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    lines = [header]

    def dialogue(style: str, start: float, end: float, text: str) -> None:
        lines.append(
            f"Dialogue: 0,{ass_time(start)},{ass_time(end)},{style},,0,0,0,,"
            f"{escape_ass(text)}\n")

    dialogue("Head", 0.0, duration, DRIVER_LABEL[client])

    # Cumulative counters resolved at 0.5s granularity; the receipts are
    # event-driven, so this is a running tally, not a sampled signal.
    counters = ["hold_entered", "motion_gap_observed", "target_rejected",
                "reliable_resync_applied", "teleport_resync_queued"]
    step = 0.5
    ticks = int(duration / step) + 1
    for i in range(ticks):
        t0 = i * step
        t1 = min(duration, t0 + step)
        cutoff = window_start + datetime.timedelta(seconds=t1)
        tally = {name: 0 for name in counters}
        last_error_m = None
        for ev in events:
            if parse_ts(ev["timestamp_utc"]) > cutoff:
                break
            state = ev.get("state")
            if state in tally:
                tally[state] += 1
            match = re.search(r"target_error_mm=(\d+)", ev.get("detail", ""))
            if match:
                last_error_m = int(match.group(1)) / 1000.0
        err = f"{last_error_m:7.1f}m" if last_error_m is not None else "      -"
        dialogue(
            "Meter", t0, t1,
            f"t{t0:05.1f}s  err{err}  holds {tally['hold_entered']}  "
            f"gaps {tally['motion_gap_observed']}  "
            f"rejects {tally['target_rejected']}  "
            f"resync {tally['reliable_resync_applied']}  "
            f"teleport {tally['teleport_resync_queued']}")

    for ev in events:
        banner = EVENT_BANNER.get(ev.get("state"))
        if not banner:
            continue
        offset = (parse_ts(ev["timestamp_utc"]) - window_start).total_seconds()
        if offset < 0 or offset > duration:
            continue
        seq = re.search(r"sequence=(\d+)", ev.get("detail", ""))
        suffix = f"  seq={seq.group(1)}" if seq else ""
        dialogue("Flash", offset, min(duration, offset + BANNER_HOLD_SECONDS),
                 banner + suffix)

    path.write_text("".join(lines), encoding="utf-8")


def ffmpeg_binary(name: str) -> str:
    found = shutil.which(name)
    if found:
        return found
    for root in (Path.home() / "AppData/Local/Microsoft/WinGet/Packages",):
        if root.exists():
            for candidate in root.rglob(name + ".exe"):
                return str(candidate)
    raise SystemExit(f"{name} not found on PATH")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-dir", required=True)
    ap.add_argument("--run-id")
    ap.add_argument("--omen-clip", required=True)
    ap.add_argument("--omen-receipt", required=True)
    ap.add_argument("--i5-clip", required=True)
    ap.add_argument("--i5-receipt", required=True)
    ap.add_argument("--output", required=True)
    ap.add_argument("--panel-width", type=int, default=1280)
    args = ap.parse_args()

    run_dir = Path(args.run_dir)
    run_id = args.run_id or run_dir.name
    work = Path(args.output).parent / "clip-build"
    work.mkdir(parents=True, exist_ok=True)

    panels = {}
    for client, clip, receipt_path in (
        ("omen", args.omen_clip, args.omen_receipt),
        ("i5", args.i5_clip, args.i5_receipt),
    ):
        receipt = json.loads(Path(receipt_path).read_text(encoding="utf-8-sig"))
        capture_start = parse_ts(receipt["capture_started_utc"])
        win_start, win_end = observer_window(run_dir, client, run_id)
        seek = (win_start - capture_start).total_seconds()
        duration = (win_end - win_start).total_seconds()
        if seek < 0:
            raise SystemExit(
                f"{client}: observer window starts {abs(seek):.1f}s before the "
                f"recording did; the capture was started too late")

        events = [e for e in read_jsonl(
            run_dir / client / "motion-authority-cutover.jsonl", run_id)
            if win_start <= parse_ts(e["timestamp_utc"]) <= win_end]
        events.sort(key=lambda e: e["timestamp_utc"])

        ass_path = work / f"{client}.ass"
        build_ass(ass_path, client, events, win_start, duration)
        panels[client] = {
            "clip": clip, "seek": seek, "duration": duration,
            "ass": ass_path, "events": len(events),
            "window_start_utc": win_start.isoformat().replace("+00:00", "Z"),
            "window_end_utc": win_end.isoformat().replace("+00:00", "Z"),
        }
        print(f"  {client}: seek {seek:.2f}s, window {duration:.1f}s, "
              f"{len(events)} motion events")

    ffmpeg = ffmpeg_binary("ffmpeg")
    height = int(args.panel_width * 9 / 16)
    # A Windows drive colon inside a filter argument is read as an option
    # separator, and escaping it portably is fiddly. Run ffmpeg from the
    # subtitle directory instead and reference the tracks by bare filename.
    filt = (
        f"[0:v]scale={args.panel_width}:{height}:force_original_aspect_ratio=decrease,"
        f"pad={args.panel_width}:{height}:(ow-iw)/2:(oh-ih)/2,"
        f"subtitles=omen.ass[l];"
        f"[1:v]scale={args.panel_width}:{height}:force_original_aspect_ratio=decrease,"
        f"pad={args.panel_width}:{height}:(ow-iw)/2:(oh-ih)/2,"
        f"subtitles=i5.ass[r];"
        f"[l][r]hstack=inputs=2[v]"
    )
    cmd = [
        ffmpeg, "-hide_banner", "-y",
        "-ss", f"{panels['omen']['seek']:.3f}", "-t",
        f"{panels['omen']['duration']:.3f}",
        "-i", str(Path(panels["omen"]["clip"]).resolve()),
        "-ss", f"{panels['i5']['seek']:.3f}", "-t",
        f"{panels['i5']['duration']:.3f}",
        "-i", str(Path(panels["i5"]["clip"]).resolve()),
        "-filter_complex", filt, "-map", "[v]",
        "-c:v", "libx264", "-preset", "medium", "-crf", "20",
        "-pix_fmt", "yuv420p", str(Path(args.output).resolve()),
    ]
    print("  composing...")
    result = subprocess.run(cmd, capture_output=True, text=True, cwd=str(work))
    if result.returncode != 0:
        sys.stderr.write(result.stderr[-4000:])
        raise SystemExit(f"ffmpeg failed with exit {result.returncode}")

    out = Path(args.output)
    receipt = {
        "schema_version": 1,
        "receipt_type": "motion_quality_clip",
        "run_id": run_id,
        "output": str(out),
        "output_bytes": out.stat().st_size if out.exists() else 0,
        "panels": {k: {kk: vv for kk, vv in v.items() if kk != "ass"}
                   for k, v in panels.items()},
        "sync_basis": ("each panel is trimmed and annotated against its own "
                       "machine's clock; no cross-machine simultaneity is claimed"),
        "overlay_basis": ("counters are a running tally of event-driven motion "
                          "receipts, not a sampled per-frame time series"),
    }
    receipt_path = out.with_suffix(".receipt.json")
    receipt_path.write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    print(f"  wrote {out} ({receipt['output_bytes']} bytes)")
    print(f"  wrote {receipt_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
