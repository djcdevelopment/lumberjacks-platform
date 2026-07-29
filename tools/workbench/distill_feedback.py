#!/usr/bin/env python3
"""Batch-distill DiscordChatExporter exports into an append-only candidate-issues journal.

DESIGN PRINCIPLE: this script assembles facts deterministically; judgment and
re-classification happen outside it. It must work identically when the local LLM
fleet is down -- it never makes a network call and never invokes a model.
Classification is pure keyword/pattern heuristics over message text; confidence is
always literally "heuristic" so nobody mistakes a guess for a verified triage.
Nothing is auto-filed, auto-promoted, or auto-dismissed anywhere -- the operator
(Derek) skims the journal (~10 min/wk) and promotes or dismisses by hand.

Standard library only.

Input: --export-dir <dir> of DiscordChatExporter (https://github.com/Tyrrrz/
DiscordChatExporter) JSON export files, one per channel or thread (as produced by
its `export`/`exportguild --include-threads` commands with `-f Json`). Verified
against the exporter's own JsonMessageWriter.cs source (see
Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md for citations):

  { "guild": {...}, "channel": {"id","type","categoryId","category","name","topic"},
    "dateRange": {...}, "exportedAt": "...", "messageCount": N,
    "messages": [ { "id", "type", "timestamp", "timestampEdited", "content",
                     "author": {"id","name","discriminator","nickname","isBot",...},
                     "attachments": [...], "embeds": [...], "reactions": [...],
                     "mentions": [...] }, ... ] }

For a forum-thread export, channel.name is the thread title and channel.category is
the parent forum -- that title is used verbatim as this tool's "thread" field.

Only messages of type "Default", "Reply", or "ThreadStarterMessage" are considered
(DCE's other message types -- pins, joins, channel renames, calls, thread-created
markers -- are system notifications, not community content, so they are skipped
before classification ever runs).

Classification (deterministic, case-insensitive, whole-phrase matching; first
matching category wins in this priority order -- bug, then feature, then question):
  bug      crash / error / exception / broke / fails / doesn't work / stack trace / silently
  feature  could you add / would be nice / feature request / wish / suggestion / support for
  question how do I / where is / what does / why does / ? (a literal question mark)
  (none of the above -> skipped as chit-chat)

Messages authored by the operator (--operator-name, default "Derek", matched
case-insensitively against the author's username OR guild nickname) are always
skipped -- his replies are answers, not candidates.

Output: appends to Lumberjacks/docs/workbench/candidate-issues.jsonl, one compact
JSON object per candidate:
  {schema_version, id:"discord-<message-id>", at, thread, author, source, kind,
   confidence:"heuristic", excerpt, status:"candidate"}
DEDUPE: a message id already present anywhere in that file (any status -- see
tools/workbench/candidate-issues-README.md for the append-only status convention)
is never appended again, so re-running over overlapping exports is idempotent.

--prompt-out [PATH] additionally writes a batch-review prompt file listing this
run's new candidates, for a local LLM second pass (re-classify / flag false
positives). The script itself never calls that LLM -- the file is just text.

Usage:
  python distill_feedback.py --export-dir ./exports
  python distill_feedback.py --export-dir ./exports --operator-name Derek --prompt-out
  python distill_feedback.py --self-test
"""
from __future__ import annotations

import argparse
import json
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

HERE = Path(__file__).resolve()
REPO_ROOT = HERE.parents[2]
DEFAULT_OUTPUT = REPO_ROOT / "Lumberjacks" / "docs" / "workbench" / "candidate-issues.jsonl"
DEFAULT_PROMPT_DIR = REPO_ROOT / "Lumberjacks" / "docs" / "workbench" / "discord" / "drafts"

EXCERPT_MAX_CHARS = 280

# DiscordChatExporter MessageKind values that carry real community content, per
# DiscordChatExporter.Core/Discord/Data/MessageKind.cs (Default=0, Reply=19,
# ThreadStarterMessage=21). Everything else (RecipientAdd, RecipientRemove, Call,
# ChannelNameChange, ChannelIconChange, ChannelPinnedMessage, GuildMemberJoin,
# ThreadCreated) is a system notification, not a candidate.
CONTENT_MESSAGE_TYPES = {"Default", "Reply", "ThreadStarterMessage"}

# Signal phrases, verbatim from the task spec. Priority order when a message
# matches more than one category: bug > feature > question. This priority is a
# deliberate choice (documented here, not in the spec): a bug report phrased as a
# question ("why does it crash?") should surface as a bug, the more actionable
# framing.
#
# Matching is plain case-insensitive substring containment, not whole-word
# boundary matching. Deliberate: real chat text inflects these words constantly
# ("crashes", "crashed", "errors", "broken") and a strict \bcrash\b would miss all
# of them. Substring containment catches those naturally at the cost of a few rare
# false positives (e.g. "failsafe" contains "fails"); acceptable because this tool
# only ever produces a "candidate" with confidence "heuristic" for a human to skim
# and dismiss, never an automatic action.
BUG_PHRASES = ["crash", "error", "exception", "broke", "fails", "doesn't work", "stack trace", "silently"]
FEATURE_PHRASES = ["could you add", "would be nice", "feature request", "wish", "suggestion", "support for"]
QUESTION_PHRASES = ["how do i", "where is", "what does", "why does"]


def classify(content: str) -> Optional[str]:
    """Return "bug", "feature", "question", or None (chit-chat), by deterministic
    keyword heuristic only. See module docstring for the exact signal lists and
    priority order."""
    text = (content or "").lower()
    if any(p in text for p in BUG_PHRASES):
        return "bug"
    if any(p in text for p in FEATURE_PHRASES):
        return "feature"
    if any(p in text for p in QUESTION_PHRASES) or "?" in (content or ""):
        return "question"
    return None


def is_operator(author: dict, operator_name: str) -> bool:
    op = operator_name.strip().lower()
    if not op:
        return False
    name = (author.get("name") or "").strip().lower()
    nickname = (author.get("nickname") or "").strip().lower()
    return op in (name, nickname)


def display_name(author: dict) -> str:
    nickname = (author.get("nickname") or "").strip()
    if nickname:
        return nickname
    return (author.get("name") or "unknown").strip()


def iter_export_files(export_dir: Path) -> list[Path]:
    return sorted(p for p in export_dir.rglob("*.json") if p.is_file())


def load_existing_ids(output_path: Path) -> set[str]:
    """Every id ever written to the journal, regardless of its current status --
    dedupe must not re-add a message that was already recorded once, even if a
    later append changed its status (see candidate-issues-README.md)."""
    ids: set[str] = set()
    if not output_path.exists():
        return ids
    with output_path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(obj, dict) and "id" in obj:
                ids.add(obj["id"])
    return ids


def extract_candidates(doc: dict, source_label: str, operator_name: str) -> list[dict]:
    channel = doc.get("channel") or {}
    thread = channel.get("name") or source_label
    candidates = []
    for msg in doc.get("messages") or []:
        if msg.get("type") not in CONTENT_MESSAGE_TYPES:
            continue
        author = msg.get("author") or {}
        if is_operator(author, operator_name):
            continue
        content = msg.get("content") or ""
        kind = classify(content)
        if kind is None:
            continue
        message_id = msg.get("id")
        if not message_id:
            continue
        candidates.append(
            {
                "schema_version": 1,
                "id": f"discord-{message_id}",
                "at": msg.get("timestamp"),
                "thread": thread,
                "author": display_name(author),
                "source": "discord-export",
                "kind": kind,
                "confidence": "heuristic",
                "excerpt": content.strip()[:EXCERPT_MAX_CHARS],
                "status": "candidate",
            }
        )
    return candidates


def append_jsonl(path: Path, records: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    chunk = "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in records)
    with path.open("ab") as f:
        f.write(chunk.encode("utf-8"))


def write_text_deterministic(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content.encode("utf-8"))


def render_prompt_file(new_candidates: list[dict], generated_at: datetime) -> str:
    lines = []
    lines.append("# Batch review prompt -- feedback distillation second pass")
    lines.append("")
    lines.append(
        "Never auto-run. Paste this whole file into a local LLM chat only if you want a "
        "second-pass sanity check on this batch before you skim it yourself."
    )
    lines.append("")
    lines.append(f"Generated: {generated_at.strftime('%Y-%m-%dT%H:%M:%SZ')}")
    lines.append(f"New candidates this run: {len(new_candidates)}")
    lines.append("")
    lines.append("Instructions for the model:")
    lines.append("- Re-check each candidate's kind (bug, feature, question) against its excerpt.")
    lines.append("- Flag any that read as chit-chat that slipped through, or where the kind looks wrong.")
    lines.append("- Do not invent new candidates and do not fetch anything -- judge only the rows below.")
    lines.append('- Reply with one line per candidate: id, your suggested kind (or "keep"), one line why.')
    lines.append("")
    lines.append("Candidates from this run:")
    lines.append("")
    for c in new_candidates:
        lines.append(
            f"- id: {c['id']} | kind: {c['kind']} | thread: {c['thread']} | "
            f"author: {c['author']} | excerpt: \"{c['excerpt']}\""
        )
    lines.append("")
    return "\n".join(lines)


def prompt_out_path(arg_value: Optional[str], generated_at: datetime) -> Optional[Path]:
    if arg_value is None:
        return None
    if arg_value == "__default__":
        date_str = generated_at.strftime("%Y-%m-%d")
        return DEFAULT_PROMPT_DIR / f"{date_str}-feedback-review-prompt.md"
    return Path(arg_value)


def relative_display_path(path: Path) -> str:
    try:
        return path.resolve().relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return str(path)


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Distill DiscordChatExporter exports into candidate-issues.jsonl. Never files or posts anything.",
    )
    parser.add_argument("--export-dir", type=Path, help="Directory of DiscordChatExporter JSON export files.")
    parser.add_argument(
        "--operator-name",
        default="Derek",
        help="Case-insensitive author name/nickname to skip (the operator's own replies). Default: Derek.",
    )
    parser.add_argument(
        "--prompt-out",
        nargs="?",
        const="__default__",
        default=None,
        metavar="PATH",
        help=(
            "Write a batch-review prompt file listing this run's new candidates. "
            "If PATH is omitted, writes under Lumberjacks/docs/workbench/discord/drafts/."
        ),
    )
    parser.add_argument("--self-test", action="store_true", help="Run the built-in self-test suite and exit.")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help=argparse.SUPPRESS)
    return parser


def run(args: argparse.Namespace) -> int:
    if not args.export_dir:
        print("error: --export-dir is required", file=sys.stderr)
        return 2
    if not args.export_dir.exists():
        print(f"error: export dir not found: {args.export_dir}", file=sys.stderr)
        return 2

    existing_ids = load_existing_ids(args.output)
    all_new: list[dict] = []
    files = iter_export_files(args.export_dir)
    if not files:
        print(f"No .json export files found under {args.export_dir}")
        return 0

    for path in files:
        try:
            doc = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            print(f"warning: skipping unreadable export {path}: {exc}", file=sys.stderr)
            continue
        candidates = extract_candidates(doc, source_label=path.stem, operator_name=args.operator_name)
        for c in candidates:
            if c["id"] in existing_ids:
                continue
            existing_ids.add(c["id"])
            all_new.append(c)

    if all_new:
        append_jsonl(args.output, all_new)
    print(f"Scanned {len(files)} export file(s); appended {len(all_new)} new candidate(s) to {relative_display_path(args.output)}")

    generated_at = datetime.now(timezone.utc)
    out_path = prompt_out_path(args.prompt_out, generated_at)
    if out_path is not None:
        if all_new:
            content = render_prompt_file(all_new, generated_at)
            write_text_deterministic(out_path, content)
            print(f"Wrote batch-review prompt: {relative_display_path(out_path)}")
        else:
            print("No new candidates this run; skipped writing a batch-review prompt file.")

    return 0


# --------------------------------------------------------------------------- #
# Self-test
# --------------------------------------------------------------------------- #

def _author(name: str, nickname: str = "", user_id: str = "0", is_bot: bool = False) -> dict:
    return {
        "id": user_id,
        "name": name,
        "discriminator": "0000",
        "nickname": nickname,
        "color": None,
        "isBot": is_bot,
        "roles": [],
        "avatarUrl": "",
    }


def _message(msg_id: str, author: dict, content: str, timestamp: str, msg_type: str = "Default") -> dict:
    return {
        "id": msg_id,
        "type": msg_type,
        "timestamp": timestamp,
        "timestampEdited": None,
        "callEndedTimestamp": None,
        "isPinned": False,
        "content": content,
        "author": author,
        "attachments": [],
        "embeds": [],
        "stickers": [],
        "reactions": [],
        "mentions": [],
    }


def _export_doc(channel_name: str, messages: list[dict]) -> dict:
    return {
        "guild": {"id": "1", "name": "Fixture Guild", "iconUrl": ""},
        "channel": {
            "id": "100",
            "type": "GuildPublicThread",
            "categoryId": "50",
            "category": "workbench",
            "name": channel_name,
            "topic": "",
        },
        "dateRange": {"after": None, "before": None},
        "exportedAt": "2026-07-28T00:00:00Z",
        "messageCount": len(messages),
        "messages": messages,
    }


def run_self_test() -> bool:
    results: list[tuple[bool, str]] = []

    def check(condition: bool, description: str, detail: str = "") -> None:
        results.append((condition, description if condition else f"{description}: {detail}"))

    with tempfile.TemporaryDirectory(prefix="distill-feedback-selftest-") as tmp:
        tmp_path = Path(tmp)
        export_dir = tmp_path / "exports"
        export_dir.mkdir()
        output_path = tmp_path / "candidate-issues.jsonl"

        long_content = "This crashes every time I open the picker. " + ("x" * 300)
        dup_content = "Getting an error when I try to save my selection, it just silently fails."

        messages = [
            _message("1001", _author("alice"), long_content, "2026-07-20T10:00:00Z"),
            _message("1002", _author("bob", nickname="bobby"), "Stack trace after clicking submit, full exception below.", "2026-07-20T10:05:00Z"),
            _message("1003", _author("carol"), "Feature request: could you add a dark mode toggle?", "2026-07-20T10:10:00Z"),
            _message("1004", _author("dave"), "How do I point this at my own guild's export?", "2026-07-20T10:15:00Z"),
            _message("1005", _author("erin"), "lol nice", "2026-07-20T10:20:00Z"),
            _message("1006", _author("frank"), "anyone around this weekend", "2026-07-20T10:25:00Z"),
            _message("1007", _author("Derek", nickname="Derek"), "Why does it crash? Let me check the error log.", "2026-07-20T10:30:00Z"),
            _message("1008", _author("gina"), dup_content, "2026-07-20T10:35:00Z"),
        ]
        doc = _export_doc("steward-view-thread", messages)
        (export_dir / "steward-view-thread.json").write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")

        # Pre-seed the output file with a record for message 1008 (the duplicate).
        pre_existing = {
            "schema_version": 1,
            "id": "discord-1008",
            "at": "2026-07-19T00:00:00Z",
            "thread": "steward-view-thread",
            "author": "gina",
            "source": "discord-export",
            "kind": "bug",
            "confidence": "heuristic",
            "excerpt": dup_content[:280],
            "status": "candidate",
        }
        output_path.write_text(json.dumps(pre_existing, ensure_ascii=False) + "\n", encoding="utf-8")

        parser = build_arg_parser()

        # --- First run --------------------------------------------------------
        args1 = parser.parse_args(["--export-dir", str(export_dir), "--output", str(output_path)])
        rc1 = run(args1)
        check(rc1 == 0, "first run exits 0", f"exit code was {rc1}")

        lines_after_1 = [json.loads(ln) for ln in output_path.read_text(encoding="utf-8").splitlines() if ln.strip()]
        new_after_1 = [r for r in lines_after_1 if r["id"] != "discord-1008"]
        check(len(new_after_1) == 4, "4 candidates appended on first run", f"got {len(new_after_1)}: {[r['id'] for r in new_after_1]}")

        by_id = {r["id"]: r for r in new_after_1}
        check(
            by_id.get("discord-1001", {}).get("kind") == "bug" and by_id.get("discord-1002", {}).get("kind") == "bug",
            "the two bug messages classified as bug",
            f"got {by_id.get('discord-1001', {}).get('kind')}, {by_id.get('discord-1002', {}).get('kind')}",
        )
        check(by_id.get("discord-1003", {}).get("kind") == "feature", "feature ask classified as feature", f"got {by_id.get('discord-1003', {})}")
        check(by_id.get("discord-1004", {}).get("kind") == "question", "question classified as question", f"got {by_id.get('discord-1004', {})}")
        check("discord-1005" not in by_id and "discord-1006" not in by_id, "chit-chat messages skipped")
        check("discord-1007" not in by_id, "operator message skipped despite bug-shaped content")
        check(
            "discord-1008" not in {r["id"] for r in new_after_1},
            "duplicate of pre-existing candidate skipped (not appended again)",
        )

        excerpt = by_id.get("discord-1001", {}).get("excerpt", "")
        check(len(excerpt) == 280, "excerpt trimmed to 280 chars", f"len was {len(excerpt)}")
        check(excerpt == long_content.strip()[:280], "excerpt matches first 280 chars of stripped content")

        # --- Second run: dedupe must yield zero new appends --------------------
        args2 = parser.parse_args(["--export-dir", str(export_dir), "--output", str(output_path)])
        rc2 = run(args2)
        check(rc2 == 0, "second run exits 0", f"exit code was {rc2}")
        lines_after_2 = [ln for ln in output_path.read_text(encoding="utf-8").splitlines() if ln.strip()]
        check(
            len(lines_after_2) == len(lines_after_1),
            "dedupe respected on second run (0 new appends)",
            f"line count went from {len(lines_after_1)} to {len(lines_after_2)}",
        )

        # --- --prompt-out on a fresh scenario with new candidates ---------------
        export_dir_c = tmp_path / "exports_c"
        export_dir_c.mkdir()
        output_c = tmp_path / "candidate-issues-c.jsonl"
        doc_c = _export_doc("another-thread", [_message("2001", _author("hank"), "Feature request: would be nice to export CSV too.", "2026-07-21T00:00:00Z")])
        (export_dir_c / "another-thread.json").write_text(json.dumps(doc_c, ensure_ascii=False), encoding="utf-8")
        prompt_path = tmp_path / "prompt-out.md"
        args3 = parser.parse_args(
            ["--export-dir", str(export_dir_c), "--output", str(output_c), "--prompt-out", str(prompt_path)]
        )
        rc3 = run(args3)
        check(rc3 == 0, "run with --prompt-out exits 0", f"exit code was {rc3}")
        check(prompt_path.exists(), "--prompt-out writes a prompt file when there are new candidates")
        prompt_text = prompt_path.read_text(encoding="utf-8") if prompt_path.exists() else ""
        check("discord-2001" in prompt_text, "prompt file lists the new candidate's id")

    all_pass = True
    for passed, description in results:
        status = "PASS" if passed else "FAIL"
        if not passed:
            all_pass = False
        print(f"[{status}] {description}")

    print(f"\n{sum(1 for p, _ in results if p)}/{len(results)} assertions passed")
    return all_pass


def _make_console_utf8_safe() -> None:
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
