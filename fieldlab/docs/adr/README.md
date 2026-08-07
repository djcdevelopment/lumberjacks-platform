# Architecture Decision Records — Valheim netcode-replacement program

Durable *decisions* (not lessons, not facts) for the fieldlab netcode-replacement work. A decision
that changes how we'll decide → ADR here; a fact about the world → memory; a how-to → a doc.
Format follows the standard Status / Context / Decision / Consequences shape.

| # | Title | Status | Rung |
|---|---|---|---|
| [0001](0001-guard-setownerinternal-directly.md) | Guard `ZDO.SetOwnerInternal` directly, not the OwnerRevision race | Accepted | I2/P3 |
| [0002](0002-auto-capture-gate-selector.md) | Auto-capture selector + observe-during-change for one-window behaviour gates | Accepted | I2/P3 |
| [0003](0003-server-side-mod-http-raw-socket.md) | Server-side mod HTTP over a raw TcpClient, not `WebRequest.Create` | Accepted | I3/P4 |
| [0004](0004-gcp-vm-for-p7-loopback-window.md) | Migrate the dedicated-server role to a GCP VM to lift the OOM ceiling | Accepted | I7/P7 |
| [0005](0005-carry-forward-unreproducible-artifacts.md) | Carry forward an unreproducible release artifact rather than rebuild it | Accepted | M5/cutover |
| [0006](0006-git-bundle-transport-no-vm-credentials.md) | Move repo history to the P7 VM by `git bundle`; keep no credentials on the box | Accepted | M5/cutover |
| [0007](0007-prune-audit-signal-discipline.md) | Choose prune signals after checking what the merge did to them; exclude proven-live code | Accepted | repo curation |
| [0008](0008-liveness-is-not-admission.md) | Record heartbeat liveness before admission; keep the primary gate strict | Accepted | M1/telemetry |
| [0009](0009-verify-against-an-independent-source.md) | A check that reads its own output is not a check | Accepted | cross-cutting |
| [0010](0010-consistency-is-predictability.md) | Consistency means predictable, not invariant | Accepted | AoI / degradation |
| [0011](0011-aoi-lives-on-the-producer.md) | AoI is enforced mod-side (producer); suppress/ack/emit are three separate operations | Accepted | Valheim netcode / ZDO redirect |
| [0012](0012-gameplay-telemetry-is-client-side.md) | Gameplay telemetry is captured client-side and relayed to the server by routed RPC | Accepted | Community telemetry / G4 |
| [0013](0013-ownership-visibility-split.md) | Ownership, visibility, delivery, and ack are four things — split them for area co-presence | Proposed | Valheim netcode / multi-player density |
| [0014](0014-boot-must-converge-or-say-so.md) | Boot must converge on its own, or say so loudly | Accepted | cross-cutting / P7 stack lifecycle |
| [0015](0015-pin-line-endings-for-load-bearing-bytes.md) | Pin line endings for bytes that are hashed or parsed elsewhere | Accepted | cross-cutting / repo hygiene |
| [0016](0016-banked-state-must-carry-session-identity.md) | Banked state must carry the identity scope of what it banks | Accepted | netcode / canonical session |
| [0017](0017-prove-the-lane-users-ship-on.md) | Acceptance proofs must exercise the lane users ship on | Accepted | M7 / cutover acceptance |
| [0018](0018-quest-proof-is-the-eventlog-row.md) | The quest proof is the durable EventLog row, not a re-materialized evidence envelope | Accepted | Community telemetry / QB-1 |

Canon: [`Lumberjacks/docs/roadmap/valheim-volunteer-roadmap.json`](../../../Lumberjacks/docs/roadmap/valheim-volunteer-roadmap.json)
(state — milestones, gate/proof state) ·
[`plans/full-roadmap-working-strategy.md`](../../../plans/full-roadmap-working-strategy.md) (plan —
active strategy, controls execution order) · `../../VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md` (I-ladder).
Retros: `../../retro/`. Networking-lane current status (hard hold since 2026-07-28):
[`PINNED-networking-lane-2026-07.md`](../../PINNED-networking-lane-2026-07.md).

*(This line pointed at `GROUND-TRUTH.md` and `TEST-PROGRAM.md` — both pruned 2026-07, fieldlab-native
docs never part of the external comfy archive, recoverable at baseline pre-prune ref `57654fd`;
explicitly retired in favor of the living roadmap, see `fieldlab/status/README.md`.)*
