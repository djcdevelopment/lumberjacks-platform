# Architecture Decision Records — Valheim netcode-replacement program

Durable *decisions* (not lessons, not facts) for the fieldlab netcode-replacement work. A decision
that changes how we'll decide → ADR here; a fact about the world → memory; a how-to → a doc.
Format follows the standard Status / Context / Decision / Consequences shape.

| # | Title | Status | Rung |
|---|---|---|---|
| [0001](0001-guard-setownerinternal-directly.md) | Guard `ZDO.SetOwnerInternal` directly, not the OwnerRevision race | Accepted | I2/P3 |
| [0002](0002-auto-capture-gate-selector.md) | Auto-capture selector + observe-during-change for one-window behaviour gates | Accepted | I2/P3 |

Canon: `../../GROUND-TRUTH.md` (state) · `../../TEST-PROGRAM.md` (plan) ·
`../../VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md` (I-ladder). Retros: `../../retro/`.
