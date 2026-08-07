*Meta: forum thread title suggestion — "Quest submission → review bridge". Paste everything below
the divider as the thread's opening post. This thread was "Recoverable pieces" until 2026-08-06:
world-photography moved to its own thread when it started running, and then this piece got ported
too, so nothing recoverable is left in it. Its provision key stays `recoverable` because that key
maps to this thread's Discord id — renaming it would orphan the thread and open a second one.*

---

**Quest submission → review bridge**

What it is: a player finishes a quest in-game, and a GM gets a review record they can act on —
read it, accept or reject it, and export the exact guild turn-in command — without chasing anyone
for a screenshot.

This used to be in the recoverable pile. It has been ported and it now runs against a test fixture
in about ten minutes; what is left is proving it on a live completion.

**The interesting part was that it was never a port.** The front half is alive in the mod today
with tests: `QuestViewLoader` reads a player's quest-view, `QuestTriggerEvaluator` matches kills
against tracked quests, and the completion travels client → routed RPC → server → durable
EventLog. But the old back half expected an *evidence envelope* — a screenshot on disk, a trace
file, position and biome — and the live mod deliberately produces none of that. So the real
question was: rebuild the envelope on top of the EventLog, or accept a thinner record?

Decided in **ADR 0018**: the durable EventLog row *is* the evidence. No re-materialized
screenshots. A review names the row — event id, server receipt time — and the proven review
workflow (`list` / `show` / `accept` / `reject` / `needs-info` / `export`) carries over unchanged.

Honest status: **local-only**. The path — EventLog row → thin submission → review record → guild
command — is implemented at `tools/quest-bridge/` and passes in `tests/test_quest_bridge.py`. What
has *not* happened is one real in-game completion travelling the whole way. That is the claiming
task.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **QB-1** — Prove it live: one real in-game completion, EventLog to review record. Run the mod
  with `QuestEvaluatorEnabled` on, complete a tracked quest, then pull it with
  `tools/quest-bridge/fetch_completions.py` and render it. Done when a real completion comes out
  the other end as one human-readable review record carrying its guild-command draft. This is the
  claiming task for this piece, and it is the last step — the design is settled and the
  fixture-driven path already passes.

What a useful reply looks like:

- What you ran, and how far you got.
- What actually happened — errors pasted verbatim, not summarized.
- What you expected instead.
- If you got a review record out: whether the guild-command draft was actually correct for your
  guild, or whether it needed hand-editing.

Worth knowing: a review record carries a player's name and what they did. Keep the review inbox
off any public surface.
