# ADR 0018 — The quest proof is the durable EventLog row, not a re-materialized evidence envelope

- **Status:** Accepted (2026-08-06)
- **Rung:** Community telemetry surface / quest-submission-bridge (workbench claiming task QB-1)

## Context

The quest-submission bridge has two halves on two different footings. The front half is alive in
the live mod with tests: `QuestViewLoader` parses a player's `quest-view.json`,
`QuestTriggerEvaluator` matches classified kill events against tracked quests, and
`GameplayEventProducer` relays a `quest_completed` event over the ADR 0012 seam
(client capture → routed RPC → server POST → gateway → durable EventLog). The back half — the
`bridge_consumer.py` / `review_inbox.py` pair that turns a submission into a GM-reviewable record
and drafts the exact guild command — was proved once against the retired `ComfyControlSurface`
mod's local outbox, then pruned; it re-landed byte-exact and unwired at
`recipes/quest-submission-bridge/`.

The two halves speak different languages. The old consumer's input contract is an **evidence
envelope**: a submission JSON carrying player name, world, position + biome, a screenshot on disk,
a trace file, and a bot-command template. The live path carries **none of that, by design**: the
port to `ComfyNetworkSense` deliberately removed the submission/evidence coupling ("no
SubmissionService/screenshots/outbox" — `QuestTriggerEvaluator.cs`; "the durable EventLog entry is
the proof now" — `TrackedQuest.cs`). A `quest_completed` EventLog row holds `actor_id`, world id,
timestamp, and a payload with `quest_id`, `guild`, `category`, `bot_command`, creature, weapon.

So QB-1 ("port the consumer") is really a design call: **re-materialize the evidence envelope on
top of the EventLog seam, or accept a thinner record?**

## Decision

**The durable EventLog row is the evidence. The bridge consumer ports to the EventLog
`quest_completed` contract and renders a thinner review record; the screenshot/trace envelope is
not re-materialized.**

- The reviewable unit is one `quest_completed` row fetched from the EventLog
  (`GET /events?type=quest_completed` on Game.EventLog), adapted into the existing
  `bridge-review/` shape (review markdown + state + index + events.jsonl) so the proven
  review-inbox workflow (`list` / `show` / `accept` / `reject` / `needs-info` / `export`)
  carries over unchanged.
- The "Evidence" section of a review names the EventLog row: event id, server receipt time,
  `source_service`, and the trigger facts (creature, weapon, ranged) the evaluator matched on.
  The export command is the quest's own `bot_command`, which rides the payload verbatim.
- The mod additionally puts the quest's public-safe display name into the EventLog payload
  (`quest_name`) — previously the name travelled only to the public feed as `detail` and the
  durable row kept only `quest_id`. Additive payload field; no wire or feed change. The consumer
  tolerates rows written before this lands.
- The ported consumer lives at `tools/quest-bridge/` (operator tooling, private plane). The
  `recipes/quest-submission-bridge/` copies stay byte-exact raw material per their PROVENANCE
  editing rule; the port records its MIT derivation.

## Why not re-materialize the envelope

1. **The server physically cannot.** Per ADR 0012 the server never sees the kill — combat is
   simulated on the owning client. Position, biome, and screenshots rebuilt server-side would be
   fabrications, wrong by construction.
2. **Client-side re-materialization is un-pruning, not porting.** The outbox/screenshot/trace
   machinery belonged to the retired `ComfyControlSurface` mod, whose C# stays archive-only on
   purpose. Rebuilding it inside `ComfyNetworkSense` reverses an accepted simplification and
   re-couples Unity capture into a mod that now ships as a paired client+server deploy.
3. **The trigger already did the verification the screenshot used to do.** The evaluator matched
   creature/weapon/projectile filters and armed a cooldown, and the row is a server-received
   attestation on a Producer-gated ingress a public client cannot reach. A GM reviewing the thin
   record judges the same thing they judged before — whether to accept the completion — with
   strictly better provenance and no image to squint at.
4. **Privacy gets lighter, not heavier.** The old envelope bundled a real player's name, world,
   position, and screenshots. The thin record carries only what the durable EventLog already
   holds, and it stays on the operator's disk exactly like the old inbox did.

**Cost accepted:** a thin record shows no scene — a GM who wants flavor (screenshot of the kill)
does not get it from this pipeline. If review practice proves that insufficient, an *optional*
client-side attachment lane (player-initiated screenshot alongside the completion) can be a later
additive increment; it is explicitly not part of QB-1 and must not block the thin path.

## Consequences

- QB-1's real remaining content is the live proof: one in-game completion with
  `QuestEvaluatorEnabled` on, fetched from the EventLog, out the other end as one review record.
  The fixture-driven path is implemented and tested (`tools/quest-bridge/`,
  `tests/test_quest_bridge.py`).
- The workbench catalog entry and one-pager change from "point the consumer at the live mod's
  telemetry" (which implied a folder that does not exist) to naming this contract.
- Reading the durable EventLog requires the private plane (the operator's lab or a tunnel) — the
  review inbox is an operator tool, consistent with its privacy note ("keep the review inbox off
  any public surface").

## Related

ADR [0012](0012-gameplay-telemetry-is-client-side.md) (capture is client-side; the EventLog entry
is the proof); `recipes/quest-submission-bridge/PROVENANCE.md` (byte-exact raw material and its
editing rule); `Lumberjacks/docs/workbench/tools/quest-submission-bridge.md` (the one-pager);
workbench catalog `quest-submission-bridge` / first task QB-1.
