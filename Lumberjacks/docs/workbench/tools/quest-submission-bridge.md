# Quest Submission → Review Bridge

The back half of quest capture: take what a player actually did in-game
and turn it into something a GM can read and act on, without a screenshot
and a Discord message in between.

## What it is

Two halves, on two very different footings.

**The front half is alive, today, in the live mod, with tests.**
`network/mod/ComfyNetworkSense/Core/Services/QuestViewLoader.cs` loads and
parses a player's `quest-view.json` (the file the Quest Picker one-pager's
tool produces) into a list of tracked quests — Unity-free, regex/balanced-
brace parsing, unit-tested (`QuestViewLoaderTests.cs`, 10 cases).
`QuestTriggerEvaluator.cs` matches already-classified kill events against
those tracked quests — creature name (case- and `(Clone)`-insensitive),
optional weapon-skill and projectile filters, a per-quest cooldown — and
yields completions to relay (`QuestTriggerEvaluatorTests.cs`, 12 cases).
Both are wired into the live mod (`ComfyNetworkSense.cs`,
`GameplayEventProducer.cs`) behind a config flag
(`PluginConfig.QuestEvaluatorEnabled`, off by default). When a kill
completes a tracked quest, the mod relays a `quest_completed` event to the
server with the quest's public-safe name; the quest id, guild, and turn-in
command ride along only as far as the durable EventLog.

**The back half — the part that turns a completion into a
GM-reviewable record — was pruned and isn't wired to anything.** It lived
in a different, retired mod (`ComfyControlSurface`, not `ComfyNetworkSense`)
that wrote a local "outbox": a submission JSON (player, world, biome,
position), a screenshot, a trace log, and a receipt. Two Python scripts
consumed that outbox with no network, no bot token, and no server:
`bridge_consumer.py` (renders one review-ready markdown file per
submission) and `review_inbox.py` (`list` / `show` / `accept` / `reject` /
`needs-info` / `export` — export drafts the exact `/slayer submit ...`-
style command a GM would paste into Discord, evidence attached).

## What it is NOT

Not a working pipeline today. The two halves don't talk to each other:
`bridge_consumer.py`'s input contract is the **old** mod's outbox shape
(`schema_version: 1`, `status: "ready_for_review"`, a screenshot file on
disk) — that is not what the live `ComfyNetworkSense` mod produces. The
live mod's proof is a durable server-side EventLog entry, not a local
outbox file with a screenshot (a deliberate simplification — see the code
comment in `QuestTriggerEvaluator.cs`: "the proof is now the durable
EventLog entry... and not a pair of screenshots"). Porting
`bridge_consumer.py` to the live mod means reading from somewhere new, not
just pointing it at a different folder.

Not a bot. Nothing here posts to Discord or holds a bot token — the whole
design, old and new, is local files and human review in the loop.

## Status

Not running as a whole. The front half is alive in the mod today with
tests — `QuestViewLoader.cs` and `QuestTriggerEvaluator.cs`. The back half
that would turn a completion into a GM-reviewable record sits in the
public `comfy` repo, unwired to the live mod's output. Since 2026-07-29
that back half also sits in this repo — byte-exact, still unwired — at
`recipes/quest-submission-bridge/` (provenance in its `PROVENANCE.md`);
the retired C# mod stays archive-only.

## Run it in about 10 minutes — what actually works today

This runs the **old** demo end to end, against its own fixture data — it
does not touch the live mod. It's real, working code, and it's the honest
starting point for understanding the shape QB-1 needs to fill.

From a `baseline` checkout, no clone needed:

```
python recipes/quest-submission-bridge/bridge-consumer/bridge_consumer.py recipes/quest-submission-bridge/bridge-consumer/mikers-demo
python recipes/quest-submission-bridge/bridge-consumer/review_inbox.py recipes/quest-submission-bridge/bridge-consumer/mikers-demo list
```

Or from the archive:

1. Clone the public archive:
   `git clone https://github.com/djcdevelopment/comfy` (or fetch
   `handoffs/comfy-control-surface/`).
2. From that checkout:
   ```
   python handoffs/comfy-control-surface/bridge-consumer/bridge_consumer.py handoffs/comfy-control-surface/bridge-consumer/mikers-demo
   python handoffs/comfy-control-surface/bridge-consumer/review_inbox.py handoffs/comfy-control-surface/bridge-consumer/mikers-demo list
   python handoffs/comfy-control-surface/bridge-consumer/review_inbox.py handoffs/comfy-control-surface/bridge-consumer/mikers-demo show 20260701-210000-slayer-rank-thrall-demo
   python handoffs/comfy-control-surface/bridge-consumer/review_inbox.py handoffs/comfy-control-surface/bridge-consumer/mikers-demo accept 20260701-210000-slayer-rank-thrall-demo
   ```
3. Read `QUEST.md` and `PROOF.md` in that folder (also landed at
   `recipes/quest-submission-bridge/`) — `QUEST.md` is the original
   volunteer-facing brief this pipeline was built from, and `PROOF.md` is
   the checklist that was used to prove it worked.

## What you'll see

The demo above writes `bridge-review/<submission_id>.md` (a human-readable
review of the demo Thrall-rank submission — player, world, biome,
position, evidence path, and a drafted `/slayer submit rank:Thrall
proof:...` command), `bridge-review/index.json`, and
`bridge-review/state/<submission_id>.json`. Every state change also
appends to `bridge-review/events.jsonl`, so nothing about a review
decision is silent.

The live mod side shows nothing new yet — with `QuestEvaluatorEnabled` on,
a matching kill just relays a `quest_completed` telemetry event server-side
(visible today only via the durable EventLog / the aggregated public
`/api/v0/telemetry/events` feed — not as any kind of local file).

## What's rough

- **The two halves speak different languages**, as above — this is the
  real content of QB-1, not a detail to smooth over.
- **The live mod expects `quest-view.json` in a different folder than the
  picker currently tells players to use** —
  `Valheim/BepInEx/config/comfy-network-sense/quest-view.json`, not
  `.../comfy-control/quest-view.json`. See the Quest Picker one-pager;
  it's the same bug, and it sits directly upstream of this tool.
- The old `ComfyControlSurface` plugin this back half was built for is
  itself retired and not part of the live mod — don't install it expecting
  it to feed `ComfyNetworkSense`.
- No public Thunderstore packaging exists for anything here — per
  `PROOF.md`, that was always a later step.

## First tasks

- **QB-1 — Port `bridge_consumer.py` to read the live mod's quest
  telemetry and emit one review record.** Done when: a real in-game quest
  completion, produced by the live mod with `QuestEvaluatorEnabled` on,
  travels through to one human-readable review record — reusing
  `review_inbox.py`'s review/export shape rather than reinventing it. This
  is the claiming task for this tool.

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

Two different licenses cover the two halves — say which one you're reading
before you copy from it:

- **Front half** (`QuestViewLoader.cs`, `QuestTriggerEvaluator.cs`, and
  their tests) is in `network/mod/` in this repo: BSL 1.1 public-source
  posture, root `LICENSE` / `LICENSING.md`.
- **Back half** (`bridge_consumer.py`, `review_inbox.py`, the old
  `ComfyControlSurface` mod) originates in the public `comfy` repo, which
  is MIT-licensed (`comfy/LICENSE`) — not BSL 1.1. The copies landed at
  `recipes/quest-submission-bridge/` retain those MIT terms, recorded in
  that folder's `PROVENANCE.md` and in the root `THIRD_PARTY_NOTICES.md`.

Privacy: a submission record carries a real player's name, world, and
position, and the old design bundled a screenshot with it. None of that
was ever meant to leave a local disk — the original brief's own guarantee
was "everything is a plain file on your disk... delete the folder and it
never existed." Keep that guarantee: don't put a review inbox, an outbox,
or exported submission records on any public surface, and don't reuse the
`mikers-demo` fixture's shape as a template for anyone's real data without
the same local-only handling.
