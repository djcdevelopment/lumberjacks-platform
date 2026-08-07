*Meta: forum thread title suggestion — "Recoverable pieces". Covers two pieces, one thread. Paste
everything below the divider as the thread's opening post.*

---

**Two pieces that got cut for time — up for revival**

These two got pruned out of the repo a while back. Not because they failed — both proved
themselves before I ran out of time to carry them further. Nothing runs today; both are sitting in
the public archive as working code, just not wired back in.

**World photography → gallery**

The idea: turn a world save into a ranked list of the places people actually built, put a camera at
every one of them, and photograph the lot — a gallery for builds that never had one.

Update, 2026-08-06: this one is no longer in the recoverable pile. It got built, as **stills rather
than a video flythrough**. It reads a world save, finds 1,833 structures, works out where to stand
and which way to point from each one's own geometry, then shoots them unattended. Last run: 161
structures, 1,411 photographs, none of them framed by a human. The camera proof-of-concept grew
from 746 lines to 1,787 along the way — it now has a camera boom, aim-at-target, and an orbit
runner that writes a receipt for every frame.

What is still open is packaging, not code: the planning half is in `baseline` at
`tools/selfie-stick/`, the in-game plugin is still archive-only, and nobody but the operator can
run it yet. Claiming the in-game half is a real, available piece of work — that's CG-1.

How it works, and what it found: <https://djcdevelopment.github.io/baseline/selfie-stick/>

**Quest submission → review bridge**

The idea: a player completes a quest or rank task in-game, the mod packages a screenshot, context,
and a trace into a local file, and a human reviews it — approving it and exporting the exact guild
turn-in command when they do. Proof it worked: the front half is alive in the mod today, with
tests — it already reads a player's tracked quests and evaluates them against what they kill. The
back half — packaging a submission into something reviewable — isn't wired to anything right now,
but it proved itself before it was cut: a real submission package (screenshot, trace, receipt) got
reviewed by a human, approved, and turned into an exact guild command on the other end.

Both are archived here, working code and all: <https://github.com/djcdevelopment/comfy/tree/main/handoffs>

Reviving either one — getting the core loop running again, even rough — is a real claim on it, not
a warm-up exercise. It jumps you straight to Contributor on the ladder (see the pinned post).

First things to try:

- **CG-1** — Revive segment 1 against a current ComfyStewardView build and produce a fresh
  `waypoints.sample.json`. Start in `handoffs/segment-1-emit-waypoints.py` and
  `handoffs/valheim-camera-proof/`. This is the claiming task for this piece.
- **QB-1** — Port the review-bridge consumer to read the live mod's quest telemetry and emit one
  real, human-readable review record. Start in
  `handoffs/comfy-control-surface/bridge-consumer/`. This is the claiming task for this piece.

What a useful reply looks like:

- What you ran, and how far you got.
- What actually happened — errors pasted verbatim, not summarized.
- What you expected instead.

Worth knowing: a flythrough shows other people's builds, and a submission record carries a
player's name and what they did — ask before publishing either one anywhere public.

---

*Update 2026-07-29: the revivable raw material for both pieces now also lives in the baseline
repo itself — `recipes/camera-gallery/` and `recipes/quest-submission-bridge/`, byte-exact from
the archive with provenance recorded in each folder's `PROVENANCE.md`. You can start CG-1 or
QB-1 from a baseline checkout without cloning the archive. The C# pieces (the old control-surface
mod and the camera proof kit) stay in the archive, and the claiming tasks are unchanged.*
