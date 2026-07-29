#!/usr/bin/env python3
"""Turn recent roadmap-journal entries into a Discord announcement DRAFT skeleton.

DESIGN PRINCIPLE: this script assembles facts deterministically; judgment and prose
smoothing happen outside it. It must work identically when the local LLM fleet is
down -- it never makes a network call and never invokes a model. Every bullet is
built only from fields already present in the roadmap journal. The operator (Derek)
edits the draft by hand on his own batch rhythm and pastes it himself; this script
NEVER posts anywhere, and there is no code path in it that could.

Standard library only. Deterministic: the same journal + state + flags always
produce the same draft.

Reads Lumberjacks/docs/roadmap/commit-notes.jsonl (append-only, one JSON object per
line: schema_version, id, at, author, repository, milestones[], kind, summary,
impact, verification[], evidence[]) and writes a Markdown draft to
Lumberjacks/docs/workbench/discord/drafts/<UTC-date>-announcement.md, grouped by
milestone, with a verbatim verification-receipts section and an optional-smoothing
prompt the operator can hand to a local LLM afterward.

Selection of "new" entries:
  - If tools/workbench/announcement-state.json exists and its last_drafted_note_id
    is found in the journal, "new" means every entry strictly after that note
    (by its position in the append-only file).
  - Otherwise (first run, or a stale/missing id), "new" defaults to entries from
    the last 7 days.
  - --since <ISO-8601> always overrides the above and selects entries with
    at >= that timestamp.

The state file is updated ONLY after a successful draft write. --dry-run prints the
draft to stdout and touches neither the draft file nor the state file.

Usage:
  python new_announcement_draft.py                          # normal run
  python new_announcement_draft.py --milestone M12 --kind implementation
  python new_announcement_draft.py --since 2026-07-20T00:00:00Z
  python new_announcement_draft.py --dry-run
  python new_announcement_draft.py --self-test
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Optional

HERE = Path(__file__).resolve()
REPO_ROOT = HERE.parents[2]
DEFAULT_JOURNAL = REPO_ROOT / "Lumberjacks" / "docs" / "roadmap" / "commit-notes.jsonl"
DEFAULT_STATE = REPO_ROOT / "tools" / "workbench" / "announcement-state.json"
DEFAULT_DRAFTS_DIR = REPO_ROOT / "Lumberjacks" / "docs" / "workbench" / "discord" / "drafts"

DRAFT_HEADER = (
    "DRAFT — assembled from the public roadmap journal. Never auto-posted. "
    "Edit before pasting; delete freely."
)

# Tokens the self-test scans for in the *visible prose* (bullets, headers,
# verification lines, smoothing prompt) -- not inside the <!-- id --> traceability
# comments, whose HTML-comment syntax necessarily contains "!". The scaffold text
# this script writes must never contain any of these in prose; the operator/local
# LLM pass is where tone gets shaped.
BANNED_TOKENS = ["excited", "amazing", "!", "\U0001f680"]  # last one is the rocket emoji
_HTML_COMMENT_RE = re.compile(r"<!--.*?-->")

SMOOTHING_PROMPT = """Restate the bullets above in plain conversational language for a Discord post.

Rules:
- You may ONLY restate facts that are already present in the bullets and the
  verification receipts above. Do not add anything that is not already stated.
- No new claims, no invented numbers or dates, no superlatives, no marketing
  register. This is a status update, not a pitch.
- Keep any hedge words exactly as written -- for example "built, never run",
  "no DLL", or "not ready" mean something specific and must not be softened or
  dropped.
- Match how a solo operator talks to their own community: plain, direct, a little
  dry is fine.

Paste the finished text back for Derek to review. He edits and posts it himself;
nothing here posts on its own."""


def parse_iso(value: str) -> datetime:
    """Parse an ISO-8601 timestamp, tolerating a trailing 'Z'."""
    text = value.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    dt = datetime.fromisoformat(text)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt


def read_journal(path: Path) -> list[dict]:
    """Read the append-only journal, preserving file order. Skips unparseable lines
    with a warning rather than aborting -- roadmap:check is the authority on journal
    integrity, this tool is only a consumer."""
    entries = []
    if not path.exists():
        raise FileNotFoundError(f"journal not found: {path}")
    with path.open("r", encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                entries.append(json.loads(line))
            except json.JSONDecodeError as exc:
                print(f"warning: skipping unparseable journal line {lineno}: {exc}", file=sys.stderr)
    return entries


def load_state(path: Path) -> Optional[dict]:
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        print(f"warning: state file at {path} is not valid JSON; treating as first run", file=sys.stderr)
        return None


def select_new_entries(entries: list[dict], state: Optional[dict], since_arg: Optional[str]) -> list[dict]:
    """Decide which entries are "new". See the module docstring for the precedence
    rules: --since always wins; otherwise the state cursor; otherwise a 7-day
    default window."""
    if since_arg:
        cutoff = parse_iso(since_arg)
        return [e for e in entries if "at" in e and parse_iso(e["at"]) >= cutoff]

    if state and state.get("last_drafted_note_id"):
        target_id = state["last_drafted_note_id"]
        for idx, entry in enumerate(entries):
            if entry.get("id") == target_id:
                return entries[idx + 1 :]
        # Stale/missing id (state points at a note no longer in the journal).
        # Fall back to the recorded timestamp if we have one.
        if state.get("last_drafted_at"):
            cutoff = parse_iso(state["last_drafted_at"])
            return [e for e in entries if "at" in e and parse_iso(e["at"]) > cutoff]

    cutoff = datetime.now(timezone.utc) - timedelta(days=7)
    return [e for e in entries if "at" in e and parse_iso(e["at"]) >= cutoff]


def filter_entries(entries: list[dict], milestones: list[str], kinds: list[str]) -> list[dict]:
    if milestones:
        wanted = set(milestones)
        entries = [e for e in entries if wanted & set(e.get("milestones") or [])]
    if kinds:
        wanted = set(kinds)
        entries = [e for e in entries if e.get("kind") in wanted]
    return entries


def first_sentence(text: Optional[str]) -> str:
    """Return the first sentence of text, punctuation included. No text is added;
    if there is no sentence-ending punctuation, the whole (trimmed) string is
    returned as-is."""
    text = (text or "").strip()
    if not text:
        return ""
    match = re.search(r"[.!?](?:\s|$)", text)
    if not match:
        return text
    return text[: match.end()].strip()


def build_bullet(entry: dict) -> str:
    summary = (entry.get("summary") or "").strip()
    impact_sentence = first_sentence(entry.get("impact"))
    pieces = [p for p in (summary, impact_sentence) if p]
    text = " — ".join(pieces)
    return f"- **{summary}** — {impact_sentence} <!-- {entry.get('id', '')} -->" if summary and impact_sentence else f"- {text} <!-- {entry.get('id', '')} -->"


_MILESTONE_KEY_RE = re.compile(r"^([A-Za-z]*)(\d*)([A-Za-z]*)$")


def milestone_sort_key(label: str):
    match = _MILESTONE_KEY_RE.match(label)
    if not match:
        return (label, 0, "")
    prefix, number, suffix = match.groups()
    return (prefix, int(number) if number else -1, suffix)


def group_by_milestone(entries: list[dict]) -> tuple[list[str], dict[str, list[dict]]]:
    """Group entries by their first-listed milestone, preserving first-appearance
    order. Callers are expected to pass entries already sorted chronologically, so
    both the group order and each group's contents come out chronological."""
    order: list[str] = []
    groups: dict[str, list[dict]] = {}
    for entry in entries:
        milestones = entry.get("milestones") or []
        key = milestones[0] if milestones else "Unspecified"
        if key not in groups:
            groups[key] = []
            order.append(key)
        groups[key].append(entry)
    return order, groups


def relative_display_path(path: Path) -> str:
    try:
        return path.resolve().relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return str(path)


def render_draft(entries: list[dict], generated_at: datetime, journal_path: Path) -> str:
    lines: list[str] = []
    lines.append(f"> **{DRAFT_HEADER}**")
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append(f"Assembled: {generated_at.strftime('%Y-%m-%dT%H:%M:%SZ')}")
    lines.append(f"Source: {relative_display_path(journal_path)}")

    all_milestones = sorted(
        {m for e in entries for m in (e.get("milestones") or [])},
        key=milestone_sort_key,
    )
    kinds_present = sorted({e.get("kind") for e in entries if e.get("kind")})
    lines.append(
        f"Entries: {len(entries)} "
        f"(milestones: {', '.join(all_milestones) or 'none'}; "
        f"kinds: {', '.join(kinds_present) or 'none'})"
    )
    lines.append("")

    order, groups = group_by_milestone(entries)
    for milestone in order:
        lines.append(f"## {milestone}")
        lines.append("")
        for entry in groups[milestone]:
            lines.append(build_bullet(entry))
        lines.append("")

    lines.append("## Verification receipts")
    lines.append("")
    for entry in entries:
        summary = (entry.get("summary") or "").strip()
        lines.append(f"### {entry.get('id', '')} — {summary}")
        verification = entry.get("verification") or []
        if not verification:
            lines.append("- (none recorded)")
        else:
            for line in verification:
                lines.append(f"- {line}")
        lines.append("")

    lines.append("## OPTIONAL SMOOTHING PROMPT")
    lines.append("")
    lines.append("```text")
    lines.append(SMOOTHING_PROMPT)
    lines.append("```")
    lines.append("")

    return "\n".join(lines)


def draft_out_path(out_dir: Path, generated_at: datetime) -> Path:
    date_str = generated_at.strftime("%Y-%m-%d")
    return out_dir / f"{date_str}-announcement.md"


def write_text_deterministic(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content.encode("utf-8"))


def write_state(state_path: Path, last_entry: dict) -> None:
    payload = {
        "last_drafted_note_id": last_entry.get("id"),
        "last_drafted_at": last_entry.get("at"),
    }
    write_text_deterministic(state_path, json.dumps(payload, indent=2, ensure_ascii=False) + "\n")


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Assemble a Discord announcement DRAFT from the roadmap journal. Never posts anything.",
    )
    parser.add_argument("--since", help="ISO-8601 timestamp; overrides the state cursor and the 7-day default.")
    parser.add_argument(
        "--milestone",
        action="append",
        default=[],
        help="Limit to this milestone id. Repeatable. Default: all milestones.",
    )
    parser.add_argument(
        "--kind",
        action="append",
        default=[],
        help="Limit to this journal 'kind'. Repeatable. Default: all kinds.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print the draft to stdout; write neither the draft file nor the state file.",
    )
    parser.add_argument("--self-test", action="store_true", help="Run the built-in self-test suite and exit.")
    parser.add_argument("--journal", type=Path, default=DEFAULT_JOURNAL, help=argparse.SUPPRESS)
    parser.add_argument("--out-dir", type=Path, default=DEFAULT_DRAFTS_DIR, help=argparse.SUPPRESS)
    parser.add_argument("--state-file", type=Path, default=DEFAULT_STATE, help=argparse.SUPPRESS)
    return parser


def run(args: argparse.Namespace) -> int:
    entries = read_journal(args.journal)
    state = load_state(args.state_file)
    selected = select_new_entries(entries, state, args.since)
    selected = filter_entries(selected, args.milestone, args.kind)
    selected.sort(key=lambda e: e.get("at") or "")

    if not selected:
        print("No new entries to draft.")
        return 0

    generated_at = datetime.now(timezone.utc)
    content = render_draft(selected, generated_at, args.journal)
    out_path = draft_out_path(args.out_dir, generated_at)

    if args.dry_run:
        print(content)
        print(f"[dry-run] would write {len(selected)} entries to {relative_display_path(out_path)}", file=sys.stderr)
        print("[dry-run] state file left untouched", file=sys.stderr)
        return 0

    write_text_deterministic(out_path, content)
    write_state(args.state_file, selected[-1])
    print(f"Wrote {relative_display_path(out_path)} ({len(selected)} entries)")
    print(f"Updated state: {relative_display_path(args.state_file)}")
    return 0


# --------------------------------------------------------------------------- #
# Self-test
# --------------------------------------------------------------------------- #

def _fixture_entry(idx: int, note_id: str, at: str, milestone: str, kind: str) -> dict:
    return {
        "schema_version": 1,
        "id": note_id,
        "at": at,
        "author": "Claude",
        "repository": "Lumberjacks",
        "milestones": [milestone],
        "kind": kind,
        "summary": f"Fixture summary {idx}",
        "impact": f"Fixture impact sentence {idx} lands cleanly. A second sentence should not appear.",
        "verification": [f"Fixture verification line {idx}a", f"Fixture verification line {idx}b"],
        "evidence": [f"tools/workbench/fixture-{idx}.md"],
    }


def _write_jsonl(path: Path, entries: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = [json.dumps(e, ensure_ascii=False) for e in entries]
    path.write_bytes(("\n".join(lines) + "\n").encode("utf-8"))


def run_self_test() -> bool:
    results: list[tuple[bool, str]] = []

    def check(condition: bool, description: str, detail: str = "") -> None:
        results.append((condition, description if condition else f"{description}: {detail}"))

    with tempfile.TemporaryDirectory(prefix="announcement-draft-selftest-") as tmp:
        tmp_path = Path(tmp)

        fixture_entries = [
            _fixture_entry(1, "20260701T000000-fixture-one", "2026-07-01T00:00:00Z", "M1", "implementation"),
            _fixture_entry(2, "20260702T000000-fixture-two", "2026-07-02T00:00:00Z", "M1", "documentation"),
            _fixture_entry(3, "20260703T000000-fixture-three", "2026-07-03T00:00:00Z", "M2", "implementation"),
            _fixture_entry(4, "20260704T000000-fixture-four", "2026-07-04T00:00:00Z", "M2", "verification"),
            _fixture_entry(5, "20260705T000000-fixture-five", "2026-07-05T00:00:00Z", "M2", "decision"),
        ]

        # --- Scenario A: normal run, one entry already drafted -----------------
        a_dir = tmp_path / "a"
        journal_a = a_dir / "commit-notes.jsonl"
        state_a = a_dir / "announcement-state.json"
        out_dir_a = a_dir / "drafts"
        _write_jsonl(journal_a, fixture_entries)
        write_text_deterministic(
            state_a,
            json.dumps({"last_drafted_note_id": "20260701T000000-fixture-one", "last_drafted_at": "2026-07-01T00:00:00Z"}, indent=2) + "\n",
        )

        parser = build_arg_parser()
        args_a = parser.parse_args(
            ["--journal", str(journal_a), "--out-dir", str(out_dir_a), "--state-file", str(state_a)]
        )
        rc = run(args_a)
        check(rc == 0, "normal run exits 0", f"exit code was {rc}")

        written = sorted(out_dir_a.glob("*.md"))
        check(len(written) == 1, "exactly one draft file written", f"found {[p.name for p in written]}")
        draft_text = written[0].read_text(encoding="utf-8") if written else ""

        expected_new_ids = {
            "20260702T000000-fixture-two",
            "20260703T000000-fixture-three",
            "20260704T000000-fixture-four",
            "20260705T000000-fixture-five",
        }
        found_ids = set(re.findall(r"<!-- (\S+) -->", draft_text))
        check(
            found_ids == expected_new_ids,
            "correct entry selection (already-drafted entry excluded, other 4 included)",
            f"found {found_ids}",
        )
        check(
            "20260701T000000-fixture-one" not in draft_text,
            "already-drafted entry text absent from draft",
        )

        visible_prose = _HTML_COMMENT_RE.sub("", draft_text)
        lowered = visible_prose.lower()
        banned_hits = [tok for tok in BANNED_TOKENS if tok.lower() in lowered]
        check(not banned_hits, "no banned tokens in visible output prose", f"found {banned_hits}")

        verification_ok = all(
            f"Fixture verification line {i}a" in draft_text and f"Fixture verification line {i}b" in draft_text
            for i in (2, 3, 4, 5)
        )
        check(verification_ok, "verification lines present verbatim for every new entry")

        state_after = json.loads(state_a.read_text(encoding="utf-8"))
        check(
            state_after.get("last_drafted_note_id") == "20260705T000000-fixture-five",
            "state updated to the last drafted entry's id",
            f"got {state_after.get('last_drafted_note_id')}",
        )
        check(
            state_after.get("last_drafted_at") == "2026-07-05T00:00:00Z",
            "state updated to the last drafted entry's timestamp",
            f"got {state_after.get('last_drafted_at')}",
        )

        # --- Scenario B: --dry-run must touch neither file ----------------------
        b_dir = tmp_path / "b"
        journal_b = b_dir / "commit-notes.jsonl"
        state_b = b_dir / "announcement-state.json"
        out_dir_b = b_dir / "drafts"
        _write_jsonl(journal_b, fixture_entries)
        state_b_payload = json.dumps(
            {"last_drafted_note_id": "20260701T000000-fixture-one", "last_drafted_at": "2026-07-01T00:00:00Z"},
            indent=2,
        ) + "\n"
        write_text_deterministic(state_b, state_b_payload)
        state_b_bytes_before = state_b.read_bytes()

        args_b = parser.parse_args(
            [
                "--journal", str(journal_b),
                "--out-dir", str(out_dir_b),
                "--state-file", str(state_b),
                "--dry-run",
            ]
        )
        rc_b = run(args_b)
        check(rc_b == 0, "--dry-run run exits 0", f"exit code was {rc_b}")
        check(not out_dir_b.exists() or not list(out_dir_b.glob("*.md")), "--dry-run writes no draft file")
        check(
            state_b.read_bytes() == state_b_bytes_before,
            "--dry-run leaves state file byte-for-byte untouched",
        )

    all_pass = True
    for passed, description in results:
        status = "PASS" if passed else "FAIL"
        if not passed:
            all_pass = False
        print(f"[{status}] {description}")

    print(f"\n{sum(1 for p, _ in results if p)}/{len(results)} assertions passed")
    return all_pass


def _make_console_utf8_safe() -> None:
    """Best-effort: avoid UnicodeEncodeError on a non-UTF-8 Windows console when
    printing draft content (which may contain an em dash) in --dry-run mode. File
    writes are unaffected -- they already encode UTF-8 explicitly."""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError, OSError):
            pass


def main(argv: Optional[list[str]] = None) -> int:
    _make_console_utf8_safe()
    parser = build_arg_parser()
    args = parser.parse_args(argv)
    if args.self_test:
        return 0 if run_self_test() else 1
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
