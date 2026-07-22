# Runbook — co-presence fan-out live test (ADR 0013, Phase 0 + Phase 2)

> Two humans, one shared base. Proves the ownership/visibility split fixes area co-presence:
> shadow first (measure, no behaviour change), then fan-out (deliver a read copy to every in-range
> observer). Both are **flag-gated, default off**, and roll back instantly. The producer/redirect
> runner is **server-side**, so the flags live in the **dedicated server's** mod config; clients keep a
> compatible mod (the wire envelope is unchanged — fan-out only sends *more* envelopes to more
> partitions).

## Preconditions
- Gateway is the current image on P7 and healthy (`/api/v0/telemetry/cutover` reachable; dashboard at
  `http://127.0.0.1:8080/community` over the tunnel).
- Seat gate open for 2 players (`LUMBERJACKS_ALPHA_SEAT_GATE=disabled` in
  `/etc/comfy-p7/environment`; confirm `/valheim/handshake/status/p7-primary-v1` returns
  `seat_capacity: 0` after any Gateway restart). `VALHEIM_HANDSHAKE_SEAT_CAPACITY=0` remains only as
  rollback compatibility for older Gateway images. Emergency fallback while debugging remains
  `POST /valheim/handshake/config` with `seat_capacity: 0`, but that runtime override is not durable.
- `VALHEIM_QUEUE_PRODUCER_EMITS_RECIPIENTS=true` in `/etc/comfy-p7/environment` (durable; confirm).
- Cutover mode is `lumberjacks-primary` and coverage is 100% (`native_only=0`) — the state the repro
  needs (no vanilla fallback).

## Deploy the mod (server)
Build + ship the updated `ComfyNetworkSense.dll` to the dedicated server's BepInEx plugins via
`infra/gcp/p7/scripts/deploy-network-sense.ps1`. Both flags default **off**, so this deploy changes
nothing until armed. Clients need a release-compatible mod but not necessarily this exact build (the
envelope format is unchanged).

Flags (server BepInEx config, `[Netcode]`, both hot-reloadable):
- `zdoCoPresenceShadowEnabled` — measurement only, zero delivery change.
- `zdoCoPresenceFanoutEnabled` — the actual read-copy fan-out.

## Step 1 — Shadow ON, fan-out OFF (measure the defect)
Set `zdoCoPresenceShadowEnabled=true`, `zdoCoPresenceFanoutEnabled=false`. Both players join and stand
in the **same base**. Reproduce the miss: the second player should see the other as a *player* but not
their buildings/portals (the 2026-07-21 repro).

Tail the redirect rows (`valheim_tail_zdo_redirect`, or the server's `redirect-send.jsonl`) and filter
`event=="copresence_shadow"`. Each contended building ZDO emits one row **per observer** with:
`exposing_peer`, `observer`, `is_exposing_pass`, `distance_meters`, `band`, `disposition`
(`Emit`/`AlreadyDelivered`/`OutOfBand`), `visible`, `would_redirect`, `already_delivered`,
`delivered_data_rev`, and `owner`/`owner_rev` (evidence only).

**Verification (the shadow must match the repro):** for a building the starved player cannot see, the
shadow shows that observer as `visible=true` / `would_redirect=true` (an in-band observer a correct
model *would* serve) while the current single-recipient delivery did not reach them. The set of
`would_redirect=true` observers who nonetheless see nothing in-game = the exact starved set. If the
shadow does **not** flag the missing buildings, stop — the mechanism is not what we modelled (suspect
the client-side `RPC_ZDOData` apply instead), and fan-out will not help.

## Step 2 — Fan-out ON (deliver to everyone)
With the shadow confirmed, set `zdoCoPresenceFanoutEnabled=true` (leave shadow on to keep the evidence
stream, or off to reduce rows). Hot-reload; no restart needed.

**Verification — both players, same view:**
- Both players standing in the same base **see the same buildings** and **connected portals**.
- Dashboard / `/api/v0/telemetry/cutover`: **`duplicates=0`**, **`rejected=0`**, **`pending` drains**
  to 0 after movement settles (each observer's partition drains independently).
- Redirect rows now show the same `uid` redirected to **multiple recipients** (one per in-band
  observer), each at its own seq.

**Expected caveat (not a failure):** the window may read **`complete=False`** with 2+ recipients. This
is the pre-existing global-seq interleaving artifact (`missing_seq = maxSeq − distinctCount` assumes
contiguous per-partition seqs; the producer stamps a global `_seq`). It is a completeness *metric*, not
a delivery failure — delivery, dupes, rejects, and pending drain are the real signals here. The
per-recipient-seq fix that restores `complete=True` is the scoped follow-up in ADR 0013.

## Step 3 — Rollback (instant)
Set `zdoCoPresenceFanoutEnabled=false` (and `zdoCoPresenceShadowEnabled=false`). Hot-reload restores the
single-recipient path byte-for-byte — no redeploy, no restart. The gateway needs nothing (it only ever
saw ordinary redirect envelopes).

## What this test does and does not prove
- **Proves:** the server-side fan-out produces per-recipient read copies for every in-range observer,
  and co-located clients render the same shared area. Combined with the gateway substrate proof
  (`8f92edf`, N=2/10 isolation + WAL replay + dedup), the delivery path is end-to-end validated.
- **Does not cover:** the completeness-gate refinement (per-recipient seq), AoI band-shaping *within*
  fan-out (v1 emits full to all in-band), snapshot seeding for a player who joins far and approaches,
  and the HTTP ingress harness (recorded as later defence-in-depth). These are follow-ups, not part of
  this increment.
