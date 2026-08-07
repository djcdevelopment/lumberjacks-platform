*Meta: forum thread title suggestion — "Recoverable pieces". Paste everything below the divider as
the thread's opening post. This used to cover two pieces; world-photography moved to its own thread
on 2026-08-06 when it stopped being recoverable and started running.*

---

**A piece that got cut for time — up for revival**

This got pruned out of the repo a while back. Not because it failed — it proved itself before I ran
out of time to carry it further. Nothing runs today; it is sitting in the public archive as working
code, just not wired back in.

**Quest submission → review bridge**

The idea: a player completes a quest or rank task in-game, the mod packages a screenshot, context,
and a trace into a local file, and a human reviews it — approving it and exporting the exact guild
turn-in command when they do. Proof it worked: the front half is alive in the mod today, with
tests — it already reads a player's tracked quests and evaluates them against what they kill. The
back half — packaging a submission into something reviewable — isn't wired to anything right now,
but it proved itself before it was cut: a real submission package (screenshot, trace, receipt) got
reviewed by a human, approved, and turned into an exact guild command on the other end.

Archived here, working code and all: <ACCESS-URL>
One-pager: <ONEPAGER-URL>

Reviving it — getting the core loop running again, even rough — is a real claim on it, not a
warm-up exercise. It jumps you straight to Contributor on the ladder (see the pinned post).

First things to try:

- **QB-1** — Port the review-bridge consumer to read the live mod's quest telemetry and emit one
  real, human-readable review record. Start in
  `handoffs/comfy-control-surface/bridge-consumer/`. This is the claiming task for this piece.

What a useful reply looks like:

- What you ran, and how far you got.
- What actually happened — errors pasted verbatim, not summarized.
- What you expected instead.

Worth knowing: a submission record carries a player's name and what they did — ask before
publishing one anywhere public.

---

*Update 2026-07-29: the revivable raw material now also lives in the baseline repo itself at
`recipes/quest-submission-bridge/`, byte-exact from the archive with provenance recorded in its
`PROVENANCE.md`. You can start QB-1 from a baseline checkout without cloning the archive. The C#
pieces stay in the archive, and the claiming task is unchanged.*

*Update 2026-08-06: the other piece this thread used to cover — world photography → gallery — is
no longer recoverable. It got built, and it now has its own thread.*
