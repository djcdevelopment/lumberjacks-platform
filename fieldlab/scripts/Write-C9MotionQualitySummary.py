"""Machine summary for C9 motion quality, derived from retained receipts.

Answers C9's acceptance questions against the retained C8 acceptance pair:
  - is every hard correction attributable to a commanded action?
  - is target divergence transient or persistent?
  - is recovery from injected loss bounded?
  - is any wall-clock hitch attributable to the Lumberjacks apply path?

Reads only retained run receipts. Emits verified/inferred/unverified explicitly
rather than collapsing them into a single pass/fail.

Usage:
  python Write-C9MotionQualitySummary.py --output <path.json> [--run <id> ...]
"""
from __future__ import annotations

import argparse
import datetime
import json
import re
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RUNS_ROOT = REPO / "fieldlab/runs/native-valheim"
DEFAULT_RUNS = ["native-20260731-c8-full44", "native-20260731-c8-full45"]
CLIENTS = ["omen", "i5"]

CORRECTIONS = {
    "reliable_resync_queued", "reliable_resync_applied", "reliable_resync_receipt",
    "reliable_resync_rejected", "teleport_resync_queued",
}
# Action kinds that legitimately relocate a player or interrupt delivery, so a
# correction inside one is explained by construction rather than unexplained.
EXPLAINING_KINDS = {
    "teleport_to", "portal_roundtrip", "zone_cross", "zone_membership_resume",
    "gateway_restart_resume", "motion_drive_gap", "motion_observe_gap",
    "disconnect_resume",
}
BURST_GAP_SECONDS = 2.0


def parse_ts(value: str) -> datetime.datetime:
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))


def read_jsonl(path: Path, run_id: str | None = None) -> list[dict]:
    rows: list[dict] = []
    if not path.exists():
        return rows
    with path.open(encoding="utf-8", errors="replace") as fh:
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
    return sorted(rows, key=lambda r: r.get("timestamp_utc", ""))


def action_spans(run_dir: Path, client: str, run_id: str):
    rows = read_jsonl(run_dir / client / "native-cutover-scenario-receipts.jsonl", run_id)
    open_at, spans = {}, []
    for row in rows:
        aid = row.get("action_id") or ""
        state = row.get("state")
        if state == "action_started":
            kind = ""
            match = re.search(r"kind=([a-z_]+)", row.get("detail", ""))
            if match:
                kind = match.group(1)
            open_at[aid] = (parse_ts(row["timestamp_utc"]), kind)
        elif state in ("completed", "failed") and aid in open_at:
            start, kind = open_at.pop(aid)
            spans.append((start, parse_ts(row["timestamp_utc"]), aid, kind))
    return spans


def locate(spans, when):
    for start, end, aid, kind in spans:
        if start <= when <= end:
            return aid, kind, "inside"
    prior = [s for s in spans if s[1] <= when]
    if prior:
        start, end, aid, kind = max(prior, key=lambda s: s[1])
        if (when - end).total_seconds() <= 5.0:
            return aid, kind, "just_after"
    return "", "", "outside"


def analyse_client(run_dir: Path, client: str, run_id: str) -> dict:
    motion = read_jsonl(run_dir / client / "motion-authority-cutover.jsonl", run_id)
    spans = action_spans(run_dir, client, run_id)
    # A correction lands on the REMOTE player, so what explains it is often the
    # peer's action, not this client's. Attributing against the local timeline
    # alone makes an observer sitting in its own 'wait' look unexplained.
    peer = "i5" if client == "omen" else "omen"
    peer_spans = action_spans(run_dir, peer, run_id)

    corrections, unexplained = [], []
    for row in motion:
        if row.get("state") not in CORRECTIONS:
            continue
        when = parse_ts(row["timestamp_utc"])
        aid, kind, rel = locate(spans, when)
        peer_aid, peer_kind, peer_rel = locate(peer_spans, when)
        entry = {
            "timestamp_utc": row["timestamp_utc"], "state": row["state"],
            "action_id": aid, "kind": kind, "relation": rel,
            "peer_action_id": peer_aid, "peer_kind": peer_kind,
            "peer_relation": peer_rel,
        }
        corrections.append(entry)
        if kind not in EXPLAINING_KINDS and peer_kind not in EXPLAINING_KINDS:
            unexplained.append(entry)

    rejects = [r for r in motion if r.get("state") == "target_rejected"]
    bursts = []
    for row in rejects:
        when = parse_ts(row["timestamp_utc"])
        if bursts and (when - parse_ts(bursts[-1][-1]["timestamp_utc"])
                       ).total_seconds() <= BURST_GAP_SECONDS:
            bursts[-1].append(row)
        else:
            bursts.append([row])

    burst_rows = []
    for group in bursts:
        errors = [int(m.group(1)) for m in
                  (re.search(r"target_error_mm=(\d+)", r.get("detail", "")) for r in group)
                  if m]
        seqs = sorted({m.group(1) for m in
                       (re.search(r"sequence=(\d+)", r.get("detail", "")) for r in group)
                       if m})
        aid, kind, _ = locate(spans, parse_ts(group[0]["timestamp_utc"]))
        burst_rows.append({
            "start_utc": group[0]["timestamp_utc"],
            "span_seconds": round(
                (parse_ts(group[-1]["timestamp_utc"])
                 - parse_ts(group[0]["timestamp_utc"])).total_seconds(), 3),
            "rejections": len(group),
            "distinct_sequences": seqs,
            "single_sequence": len(seqs) == 1,
            "target_error_max_m": round(max(errors) / 1000, 1) if errors else None,
            "action_id": aid, "kind": kind,
        })

    holds = []
    for row in [r for r in motion if r.get("state") == "hold_entered"]:
        when = parse_ts(row["timestamp_utc"])
        later = [r for r in motion if parse_ts(r["timestamp_utc"]) > when
                 and r.get("state") in ("reliable_resync_applied",
                                        "reliable_resync_receipt", "motion_frame_sent")]
        aid, kind, _ = locate(spans, when)
        holds.append({
            "timestamp_utc": row["timestamp_utc"], "action_id": aid, "kind": kind,
            "recovered_after_seconds": round(
                (parse_ts(later[0]["timestamp_utc"]) - when).total_seconds(), 3)
            if later else None,
            "recovered_by": later[0]["state"] if later else None,
        })

    hitches = read_jsonl(run_dir / client / "perf-hitches.jsonl")
    sections = read_jsonl(run_dir / client / "perf-sections.jsonl")
    motion_sections = [s for s in sections if "MotionRunner" in s.get("section", "")]

    # Containment alone proves nothing: the probe calls UpdateFrame at the top of
    # ComfyNetworkSense.Update, so every hitch row is written inside that section
    # by construction, and it describes the PREVIOUS frame. Attribution therefore
    # has to be by magnitude - a Lumberjacks section only explains a long frame if
    # it accounts for a real share of it.
    ATTRIBUTION_SHARE = 0.5
    attributable = []
    for hitch in hitches:
        when = parse_ts(hitch["timestamp_utc"])
        frame_ms = hitch.get("frame_ms") or 0
        for section in sections:
            end = parse_ts(section["timestamp_utc"])
            # the frame being reported ended when this hitch row was written
            if not (when - datetime.timedelta(milliseconds=frame_ms + 250)
                    <= end <= when + datetime.timedelta(milliseconds=50)):
                continue
            if section["elapsed_ms"] >= ATTRIBUTION_SHARE * frame_ms:
                attributable.append({
                    "timestamp_utc": hitch["timestamp_utc"],
                    "frame_ms": frame_ms,
                    "section": section["section"],
                    "section_ms": round(section["elapsed_ms"], 1),
                })
                break

    return {
        "motion_events": len(motion),
        "actions_observed": len(spans),
        "native_fallback_true": sum(
            1 for r in motion if "native_fallback=true" in r.get("detail", "")),
        "state_counts": dict(Counter(r.get("state") for r in motion).most_common()),
        "hard_corrections": {
            "total": len(corrections),
            "unexplained": len(unexplained),
            "by_kind": dict(Counter(c["kind"] or "(none)" for c in corrections)),
            "unexplained_detail": unexplained,
        },
        "divergence_bursts": {
            "count": len(burst_rows),
            "all_single_sequence": all(b["single_sequence"] for b in burst_rows),
            "max_span_seconds": max([b["span_seconds"] for b in burst_rows], default=0),
            "max_target_error_m": max(
                [b["target_error_max_m"] or 0 for b in burst_rows], default=0),
            "bursts": burst_rows,
        },
        "holds": {
            "count": len(holds),
            "recovered_seconds": sorted(
                h["recovered_after_seconds"] for h in holds
                if h["recovered_after_seconds"] is not None),
            "unrecovered_in_log": sum(
                1 for h in holds if h["recovered_after_seconds"] is None),
            "detail": holds,
        },
        "perf": {
            "measured": bool(hitches) or bool(sections),
            "hitch_rows": len(hitches),
            "apply_attributable_hitches": attributable,
            "max_frame_ms": max([h.get("frame_ms", 0) for h in hitches], default=0),
            "hitch_causes": dict(Counter(
                (h.get("latest_engine_log_message") or "")[20:60].strip()
                for h in hitches).most_common(8)),
            "motion_runner_sections": [
                {"timestamp_utc": s["timestamp_utc"],
                 "elapsed_ms": round(s["elapsed_ms"], 1),
                 "session_id": s.get("session_id")}
                for s in motion_sections],
        },
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--output", required=True)
    ap.add_argument("--run", action="append", dest="runs")
    args = ap.parse_args()
    runs = args.runs or DEFAULT_RUNS

    per_run = {}
    for run_id in runs:
        run_dir = RUNS_ROOT / run_id
        if not run_dir.is_dir():
            raise SystemExit(f"missing run directory: {run_dir}")
        per_run[run_id] = {c: analyse_client(run_dir, c, run_id) for c in CLIENTS}

    every = [c for r in per_run.values() for c in r.values()]
    summary = {
        "schema_version": 1,
        "receipt_type": "c9_motion_quality_summary",
        "source_runs": runs,
        "source_commit": "c0db122f2a11bb50dfe6ffbf7db1a87152822f6a",
        "mod_sha256": "765090d17981235209deec2d9718221eda4230aa27b2a99998f99ffeac08c28f",
        "verified": {
            "no_native_fallback": all(c["native_fallback_true"] == 0 for c in every),
            "every_hard_correction_attributed": all(
                c["hard_corrections"]["unexplained"] == 0 for c in every),
            "divergence_transient_not_persistent": all(
                c["divergence_bursts"]["all_single_sequence"] for c in every),
            "max_divergence_burst_seconds": max(
                c["divergence_bursts"]["max_span_seconds"] for c in every),
            "max_hold_recovery_seconds": max(
                (max(c["holds"]["recovered_seconds"], default=0) for c in every)),
            "no_apply_attributable_frame_hitch": all(
                not c["perf"]["apply_attributable_hitches"]
                for c in every if c["perf"]["measured"]),
            "perf_measured_on": sorted({
                client for run in per_run.values()
                for client, c in run.items() if c["perf"]["measured"]}),
        },
        "inferred": {
            "teleport_freeze_then_snap": (
                "each divergence burst is one far target repeatedly refused by the "
                "30m correction guard for about half a second until the reliable "
                "teleport announcement lands; the observer therefore holds, then "
                "snaps. Bounded and explained, but visible."),
        },
        "unverified": {
            "motion_runner_startup_stall": (
                "LumberjacksMotionRunner.Update blocks once per game session and "
                "never during steady motion. Reproduced 8/8: four sessions on OMEN "
                "at 1861-1878ms and four on the i5 at 2241-2460ms. The pattern is "
                "identical on both machines - two ordinary frame hitches logging "
                "'ZNET START' immediately before the section opens, zero frame "
                "hitches anywhere inside it, then Valheim's 'Starting respawn' "
                "severe hitch 4.8-7.1s later. Two discriminators now hold: it fires "
                "on the first Update after ZNet initialises, and its duration tracks "
                "machine class rather than staying fixed, which argues for CPU-bound "
                "one-shot work over a network timeout. Root cause NOT resolved, and "
                "the absence of any frame hitch inside a multi-second main-thread "
                "section is itself unexplained."),
            "rendered_motion_quality": (
                "no observer clip exists yet; the two-client capture run is still "
                "outstanding, so no subjective verdict has been taken."),
        },
        "per_run": per_run,
    }

    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(summary, indent=2), encoding="utf-8")

    print(json.dumps(summary["verified"], indent=2))
    print(f"\nwrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
