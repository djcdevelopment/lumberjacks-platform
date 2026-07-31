# Portal lifecycle and scaling map

This note separates three portal problems that should not be solved with one
world-wide replication mechanism:

1. reconstruct the server's global portal link graph on world load;
2. update that graph when a portal/tag changes;
3. deliver portal state to players according to interest.

## What is in baseline and live

`PortalConnectionCache` did survive the split-repository cutover. It is enabled by
default and AM4 build 0.5.45 is running it.

Read-only AM4 evidence captured 2026-07-31:

- world load: `ConnectPortals => Connected 4472 portals`;
- steady cache: `portals=15133 connected=0 disconnected=0 forceSend=0`;
- retained section timings: approximately 25–30 ms for
  `PortalConnectionCache.ConnectPortals`.

The implementation replaces `Game.ConnectPortals` and its five-second coroutine
with two linear passes and a tag dictionary. This removes vanilla's repeated
per-portal search, but it still scans all 15,133 portals on Unity's main thread
every five seconds.

It does **not** replace private `ZDOMan.ConnectPortals`, which runs during
`ZDOMan.Load`. Current Valheim reconstructs persisted connections by nesting the
saved portal-source IDs against the saved portal-target IDs and comparing their
connection hashes. That is the initial-load path remembered as still slow.

The retired `C:\work\comfy` handoff confirms the same limitation: the cached
periodic loop was present, while one vanilla `ConnectPortals` still appeared
during every world load. Nothing needs copying from that retired root; baseline
already contains the same implementation.

## Correct ownership of each problem

### Initial world load: global, one-shot, hash join

Portal pairs may be on opposite sides of the world, so AoI is the wrong tool for
reconstructing canonical connectivity. Replace the private load-time nested match
with a single O(n) hash join:

```text
saved connection-hash source --+
                                +--> hash -> {source, target} --> set pair
saved connection-hash target --+
```

Snapshot the required IDs/hashes, validate that every pair has at most one source
and one target, then apply connections on the main thread. An O(n) pass over
15,133 portals should be cheap enough to run once; background Unity/ZDO mutation
is unnecessary and unsafe. If measurement still requires it, chunk the apply
phase across the loading coroutine rather than mutating ZDO tables from a worker.

### Runtime tag changes: incremental dirty index

`TeleportWorld.RPC_SetTag` clears the old connection. Portal create, destroy, and
tag-change events are rare and observable. Maintain:

```text
tag -> unmatched portal IDs
portal ID -> tag + current target
```

Only the affected old/new tag buckets need repairing. The five-second coroutine
can remain as a cheap dirty-queue drain or low-frequency reconciliation guard; it
should not scan 15,133 unchanged portals and spend 25–30 ms every pass.

### Player delivery: reliable descriptor plus AoI

The canonical server link graph remains global. Player visibility does not:

- on portal AoI entry, reliably send the local portal descriptor and current
  connection/target metadata required by `TeleportWorld`;
- ordinary static portals send no motion datagrams;
- a tag/link change is a reliable delta to observers currently subscribed to
  either endpoint;
- actual teleport is a reliable hard relocation followed by player and world AoI
  recomputation;
- distant portal meshes and unrelated portal ZDO bodies are not streamed merely
  because the server knows the global graph.

This preserves the dual-channel rule: reliable means ordered/existence state, not
automatically “broadcast the full world.”

## Cutover gaps

| Gap | Consequence | Small live proof |
|---|---|---|
| Private load-time `ZDOMan.ConnectPortals` remains vanilla nested matching | Large-world startup still pays the old reconstruction cost | Compare load phase before/after with exact 4,472 pair count |
| Periodic cache scans every portal every five seconds | Repeat 25–30 ms main-thread hitch | No portal change for 60 s produces zero full scans |
| Portal descriptor/connection breadth is not in C8's native-zero manifest | C8 can pass with portals disabled | Re-enable one known pair; both clients enter AoI and see connected state |
| Portal interaction RPC and distant relocation are not in the typed breadth gate | Native fallback may remain hidden until first use | Traverse both directions with poison armed |
| Teleport changes player sector/AoI abruptly | Remote-player generation can be evicted/reseeded | Observer sees one stable generation before/after teleport |

## Recommended ordering

Do not mix portal optimization into the immediate remote-player realization fix.
First finish the player descriptor/sector/teardown contract. Before declaring C8
complete, add one portal pair as a composition cell:

1. retain exact portal counts across load;
2. prove cached/incremental pairing with no recurring full scan;
3. enter AoI and observe the connected portal;
4. traverse both directions under native poison;
5. prove the remote player relocates without duplicate generation;
6. retain save-integrity and link-count evidence.
