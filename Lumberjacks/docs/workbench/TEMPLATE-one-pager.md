# <Name>

<!--
Template for a Community Workbench one-pager. Copy this file to
tools/<tool-id>.md and fill in every section — do not delete a section
because it feels awkward; write "unverified" or "none yet" instead of
skipping it. Delete this comment block in the copy.

Ground rule for anyone filling this in: every claim about a path or a
command must come from actually reading that file or running that command.
If you can't verify something, say "unverified" or leave it out. This
document is a volunteer's first impression of the project's honesty —
protect that.
-->

One or two plain sentences: what would you call this thing if a friend
asked what it does, with no jargon and no pitch.

## What it is

What actually exists and runs (or ran) today. Name the real files, the real
language/runtime, the real inputs and outputs. No aspirational language —
if it's planned but not built, it belongs in "What's rough" or "First
tasks," not here.

## What it is NOT

The assumption a new volunteer would reasonably make that turns out to be
wrong. Say it plainly, the same way you'd say what it IS.

## Status

One honest sentence. This must be kept verbatim in sync with this tool's
entry in `workbench.json` — if one changes, change the other in the same
pass. Use the enum `workbench.json` uses (`live`, `local-only`, `dev-only`,
`recoverable-not-running`, etc.) as the word this sentence has to justify.

## Run it in N minutes

Numbered, copy-pasteable commands, in the order you'd actually run them,
from the repo root unless stated otherwise. State any prerequisite (a
runtime, a package, an account, an invite) before step 1 if it isn't obvious
from the commands. If nothing runs end-to-end yet, say so up front and give
the smallest real thing that does run today instead of a fictional path.

1. `...`
2. `...`
3. `...`

## What you'll see

What actually appears on screen (or on disk) once the steps above succeed,
concretely enough that a volunteer can tell "it worked" from "it silently
did nothing." If a step fails instead, say what the honest failure looks
like too, if you know it.

## What's rough

Named gaps: a file that's supposed to exist and doesn't, a doc that
disagrees with the code, a stub that raises on purpose, a manual step
nobody automated. Specific and unflattering beats vague and reassuring.

## First tasks

One to three tasks a new volunteer could actually finish without needing
anything they don't already have. Each one gets an id, a title, and a
concrete "done when" — not "improve X" but a condition you could check
without asking the author what they meant.

- **ID-1 — title.** Done when: ...
- **ID-2 — title.** Done when: ...

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

The license that actually covers the code a volunteer would read, run, or
fork for this tool — name it specifically, and say plainly if it differs
from Baseline's own default (BSL 1.1 public-source, converting to
AGPL-3.0-only; see `LICENSE` / `LICENSING.md`), because more than one
repository shows up across these tools and they are not all licensed the
same way. Then: what real, identifiable data this tool can touch (player
names, positions, screenshots, guild-tracker content) and the concrete
handling rule for it — not "be careful," a rule you could actually follow.
