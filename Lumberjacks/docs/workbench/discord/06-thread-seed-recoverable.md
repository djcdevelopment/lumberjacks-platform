*Meta: forum thread title suggestion — "Recoverable pieces". Covers two pieces, one thread. Paste
everything below the divider as the thread's opening post.*

---

**Two pieces that got cut for time — up for revival**

These two got pruned out of the repo a while back. Not because they failed — both proved
themselves before I ran out of time to carry them further. Nothing runs today; both are sitting in
the public archive as working code, just not wired back in.

**Camera flythrough → gallery**

The idea: turn a world save into a ranked, attributed list of the most-built areas, fly a camera
through the top spots, and cut the footage into a short clip and still per builder — a gallery for
builds that never had one. Proof it worked: the first stage (world save → ranked waypoint list,
attributed to whoever built there) was built and produced real output against an actual era save.
A separate camera proof-of-concept — a 746-line BepInEx plugin — proved the harder part: it could
load the world, find the player, teleport to a waypoint, and write out files proving it landed in
the right spot. The last two stages (flying the full waypoint list, and cutting a recording into
the gallery) are written briefs, not code yet.

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
