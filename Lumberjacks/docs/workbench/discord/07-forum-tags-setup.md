# Forum tags — one-time setup (do this in the same session as thread creation)

*Why now: Discord cannot convert existing threads into a forum channel later, and there is
no thread history yet — this is the cheap moment. Doing it after threads fill means manual
copy-paste migration.*

## Channel

Create `#workbench` as a **Forum channel** (not a text channel). Settings:
- **Require tags on posts: ON** (Overview → "Require people to select tags").
- Default reaction: none needed. Sort: Recent Activity.
- Post guidelines box: paste the two-line version — "One post per topic. Pick one tag;
  it's how the reading pass gets sorted."

## Tags (8 total — member-facing first, then status)

Member-facing (people pick one when posting):
1. `question` — how do I / where is / what does
2. `bug` — something broke, errored, or silently did nothing
3. `claiming a task` — announcing you're taking a listed first task
4. `first task done` — evidence post for a finished first task

Status (applied by you — and by a Stage-3 Contributor on their own tool only):
5. `needs-derek` — filtered view for the batch pass; apply on first skim, remove when answered
6. `answered` — replied, waiting on the poster
7. `resolved` — closed out
8. `ladder: claimed` — post-level marker on a tool's main post when its `OWNERS.md` state
   changes (the page + ledger stay canonical; this tag is just the Discord-visible echo)

That's deliberately under Discord's limit and under the 8–15 practitioner sweet spot —
don't add tags until a real sorting need shows up twice.

## The batch pass, after tags exist

Filter the forum by `needs-derek` instead of reading every thread top to bottom. Apply
tags as you close each one — tagging at close is faster than the re-scan it replaces.

## Contributor handoff (already in the pinned post's rules)

Stage-3 Contributors re-tag posts on their own tool's threads. That's the first concrete thing
"triage rights" means, and it costs you nothing to grant — the ledger already records who
holds it.

## The six threads

Create one post per seed file (`01`–`04`, `06`), pin `05-pinned-how-this-works.md`'s
content as the channel guideline post. Tag each tool's opening post `question` — it invites
the first reply shape you want.
