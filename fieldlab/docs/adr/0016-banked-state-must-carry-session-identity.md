# ADR 0016 — Banked state must carry the identity scope of what it banks

- **Status:** Accepted (2026-08-01); source implementation and restart/replay contract
  completed 2026-08-02. Release alignment and bounded runtime restart proof remain a
  C10a precondition before retiring the interim WAL-discard rule.
- **Rung:** netcode program — canonical session / Gateway zone bank / world epoch contract

## Context

The Lumberjacks canonical zone bank persists ZDO journal state to a WAL keyed by
`world_epoch = "world-" + ZNet.GetWorldUID()` — an identity that is stable across server
restarts. ZDO ids are not: every world load reassigns the full disk set sequentially under the
server's session. The bank therefore survives a restart carrying uids the live server no longer
recognizes, and replays them to the next client as objects that exist nowhere.

Receipted consequence (2026-07-31, runs full34–full36 and diag1): the client materialized the
same disk terrain compiler under two sessions' uids (`1:2906630` / `1:2906632`, 291/290
alternating instantiations). Vanilla's duplicate handling (`TerrainComp.Awake` destroys the
*other* compiler via `ZNetScene.Destroy`, which only destroys owned ZDOs) removed instances but
never the phantom ZDO, so `IsAreaReady` never turned true and the spawn contract deadlined —
deterministically, on every cold join, after every server restart. A server-side sweep receipt
(`sweep-20260731-terrain-dedup`: zero duplicate compilers in 9,155,594 ZDOs) proved the world
clean and localized the defect to the replayed bank.

A full-assembly decompile bounded the blast radius: exactly two vanilla load-time sites destroy
non-owned objects on instantiation (`TerrainComp.Awake`, `SmokeSpawner.Awake`), so phantom-uid
replay has a small but guaranteed kill surface — and the failure mode (spawn-readiness livelock)
is silent and total.

## Decision

**Any state banked across process or session boundaries must be keyed by (or invalidated with)
the identity scope of the most session-scoped thing it contains.** For the zone bank, whose
payloads carry ZDO uids, that scope is the server session, not the world.

Concretely, the durable fix (required before C10 fallback deletion; P7 restarts are routine and
an operational wipe rule does not survive production):

1. The server publishes a session-scoped epoch — world UID plus a boot nonce — in the world
   descriptor and on every journal payload.
2. The Gateway invalidates the zone bank whenever the session component changes; clients treat a
   session-epoch change as a fresh world.
3. The world-stable component remains available for identities that genuinely survive restarts.

## Interim rule (in force now)

Until the durable fix lands: **discard the Gateway journal WAL after any AM4 server restart**
(`rm /data/valheim-zdo-journal.jsonl` in the gateway container, then restart the gateway). The
rule is recorded in memory (`server-restart-stales-gateway-zone-bank`) and the scenario runbook.
The bank is a cache of live server state and rebuilds on demand; nothing durable is lost.

## Consequences

- Cold banks become routine, which exposed and forced the fix of two latent protocol defects
  (walls 12a/12b): verdicts that had been passing on bank *warmth* rather than on the protocol.
  Cold-cache verification of critical delivery lanes is now practice (`L-2026-07-31-2`).
- The wall-14 outbound-flood fix matters more under cold banks (re-publish floods are bigger);
  the two decisions compose.
- Any future banked store (motion regions, ownership leases, zone membership) inherits this
  contract question at design time: name the most session-scoped identity in the payload, key by
  it.
