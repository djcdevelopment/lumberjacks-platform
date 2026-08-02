# ADR 0016 — Banked state must carry the identity scope of what it banks

- **Status:** Accepted and runtime-proved (2026-08-02). Source implementation,
  same-session Gateway restart/replay, dedicated-server epoch transition, and stale
  old-session rejection are complete. Release alignment remains in C10a.
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

## Interim rule (retired 2026-08-02)

The former rule required discarding the Gateway journal WAL after every AM4 server restart.
It is retired: physical run `native-20260802-cutover-recovery5` followed an actual AM4
restart, observed the session component change from `000000004f34febc` to
`000000008ef610a2`, and passed the complete 49-action native-zero composition. Gateway-only
restart replay retained 1,632 objects inside the new session; a valid mutation carrying the
old epoch returned HTTP 409 `world_epoch_not_active` without changing the new bank. The
tracked receipt is `fieldlab/evidence/c8-native-zero-composition/recovery5-session-epoch-gate.json`.

## Consequences

- Cold banks become routine, which exposed and forced the fix of two latent protocol defects
  (walls 12a/12b): verdicts that had been passing on bank *warmth* rather than on the protocol.
  Cold-cache verification of critical delivery lanes is now practice (`L-2026-07-31-2`).
- The wall-14 outbound-flood fix matters more under cold banks (re-publish floods are bigger);
  the two decisions compose.
- Any future banked store (motion regions, ownership leases, zone membership) inherits this
  contract question at design time: name the most session-scoped identity in the payload, key by
  it.
