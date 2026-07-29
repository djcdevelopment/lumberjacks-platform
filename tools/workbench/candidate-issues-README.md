# candidate-issues.jsonl — internal triage journal

This explains `Lumberjacks/docs/workbench/candidate-issues.jsonl`, produced by
`distill_feedback.py`.

**Internal file. Not for publication.** Unlike the public roadmap journal, this
one contains real Discord display names next to excerpts of what people said.
Don't link it from anything public, and don't paste it into a Discord post.

## What it is

One append-only distillation of Discord thread scrollback into things worth a
human look, so Derek can skim ~10 min/week instead of reading every thread. It is
produced entirely by deterministic keyword heuristics -- see the header of
`distill_feedback.py` for exactly which signals map to which `kind`, and
`09-discord-bot-setup.md` for how to produce the DiscordChatExporter input.
Nothing here was written or judged by an LLM, and nothing here got filed anywhere
else automatically. A candidate is a pointer to go read the real thread, not a
finished issue report.

## Record shape

One JSON object per line:

```
{schema_version, id: "discord-<message-id>", at, thread, author, source:
 "discord-export", kind: "bug"|"feature"|"question", confidence: "heuristic",
 excerpt, status}
```

`confidence` is always `"heuristic"` -- a reminder that a keyword match is a
starting point, not a verdict. `status` starts as `"candidate"`.

## Skim-and-promote workflow

1. Skim new `"candidate"` rows (they're easy to diff against last week -- the
   file only grows).
2. Open the real thread for anything that looks worth acting on.
3. Promote or dismiss it (see below). Ignore the rest; nothing needs a decision.

## Append-only status changes

JSONL has no header line to hold a legend, and this file follows the same
append-only discipline as `docs/roadmap/commit-notes.jsonl`: **never edit or
delete a past line.** A status change is a new line, same `id`, new `status`
("promoted" or "dismissed"), appended after the original. The current status of
a candidate is whichever record with that `id` appears last in the file. A
`notes` field on the new record is fine for why.

Example: message `123` was recorded as a candidate, then later promoted --
```
{"schema_version":1,"id":"discord-123", ..., "status":"candidate"}
{"schema_version":1,"id":"discord-123", ..., "status":"promoted","notes":"filed as GH-42"}
```
Both lines stay in the file forever; the second is the current truth.
