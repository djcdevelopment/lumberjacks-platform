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

Canon: `../../GROUND-TRUTH.md` (state) · `../../TEST-PROGRAM.md` (plan) ·
`../../VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md` (I-ladder). Retros: `../../retro/`.
