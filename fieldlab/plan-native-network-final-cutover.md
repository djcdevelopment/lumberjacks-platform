# Plan — native Valheim networking final cutover

**Written:** 2026-07-30

**Starting point:** `de4243e` and
[`NATIVE-NETWORK-LANDSCAPE-2026-07-30.md`](NATIVE-NETWORK-LANDSCAPE-2026-07-30.md)

**Development topology:** AM4 dedicated server; native Windows clients on OMEN and i5

**Reference deployment:** P7 remains stopped and unchanged; it is promoted only after
the AM4 gates pass

This plan drives the remaining native Valheim networking surface to zero. It is a
slice ladder, not a test-suite backlog: every slice changes one live boundary, suppresses
the corresponding native delivery, and retains evidence from the two real clients.

The old networking hard hold is no longer controlling. It existed because every useful
result required Derek to drive two game windows. The native-client harness now launches,
joins, drives bounded scenarios, collects both clients, and stops them without a KVM
loop. Derek is not scheduled as a live test operator.

The motion/transpiling experiments remain on hold until the native-use gate is green.

## Execution status

| Slice | Status | Retained boundary |
| --- | --- | --- |
| C0 | **Complete on AM4 (2026-07-30)** | `native-20260730-c0-clean`: both physical clients joined, moved, disconnected, relaunched, rejoined, and stopped under one manifest. Exact run-scoped ledgers recorded OMEN 4,360, i5 2,957, and server 12,339 native funnel calls with zero drops/faults. `native-20260730-c0-poison` blocked all 76 observed calls at the first forbidden connection boundary. |
| C1 | **Complete on AM4 (2026-07-30)** | `native-20260730-c1-final`: both physical clients kept a stable Lumberjacks connection id across a forced WebSocket abort, advanced from resume epoch 0 to 1, received the exact numbered request again, and produced one Gateway-accepted response. Both also reported the bounded intentionally-withheld receipt timeout with no native control fallback. |
| C2a | **Complete on AM4 (2026-07-30)** | `native-20260730-c2a-final`: both physical clients applied one typed Lumberjacks direct pulse on Unity `Update`; both withheld copies became bounded stale results. Client native handlers were registered, while all 107 selected server-native attempts were suppressed before `ZRpc.Invoke` and zero native copies arrived. |
| C2b | **Complete on AM4 (2026-07-30)** | `native-20260730-c2b-final`: both physical clients completed request/response, broadcast, real target-ZDO `RPC_ResetCloth`, deliberate withhold, and fresh-process reconnect. All 24 selected client attempts and all 19 selected server attempts were suppressed; zero native copies, duplicates, or dispatch failures were recorded. |
| C3 | **Complete on AM4 (2026-07-30)** | `native-20260730-c3-sixth`: a run-tagged ZDO outside both native sync rings crossed a durable Lumberjacks journal, survived a Gateway restart, reached late i5 as a snapshot, then reached both clients as a valid delta and tombstone through typed apply. Stale/malformed entries were rejected before mutation; selected native `CreateSyncList` candidates and network `RPC_ZDOData` calls were zero. |
| C4 | **Complete on AM4 (2026-07-30)** | `native-20260730-c4b-tenth`: the dedicated server created one real Raspberry ZDO per client; Lumberjacks issued three lease epochs, reclaimed on socket loss, rejected wrong/expired epochs, and authorized one valid action. Selected native ownership, selection, destroyed-ZDO, and inventory pickup paths were suppressed. OMEN inventory changed 9→10 and i5 11→12 exactly once; both completion frames were acknowledged and both clients completed fresh-process resume unattended. |
| C5 | **Complete on AM4 (2026-07-30)** | `native-20260730-c5-final`: both physical clients entered from a validated Lumberjacks world descriptor while native `PeerInfo` world fields were blank. Each resumed the same first snapshot chunk after a forced socket drop, applied three typed objects, completed once, unloaded to zero stale objects, and spawned nothing when membership was withheld. AM4 suppressed every selected native membership candidate. Wrong protocol/world-generation cells stopped before scene entry. |
| C6 | **Complete on AM4 (2026-07-30)** | `native-20260730-c6-eighth`: both physical clients applied numbered Lumberjacks motion to the real remote player in both directions while the selected native transform writer and position writes were suppressed. OMEN withheld sequences 600–619; i5 held without native fallback, applied the reliable resync, and queued its ACK. Both clients completed a fresh-process resume; i5 also proved binary-WebSocket fallback when its advertised UDP path was unreachable. |
| C7 | **Complete on AM4 (2026-07-31)** | `native-20260731-c7-cold-final`: OMEN and i5 launched without `+connect`, authenticated only to Lumberjacks, constructed the logical server peer, reached the character scene twice, and recorded zero client native use with poison armed. `native-20260731-c7-negative-second` passed invalid-enrollment, unavailable-Gateway, wrong-release, and wrong-descriptor fail-closed cells. |
| C8 | **Complete and runtime-reconfirmed on AM4 (2026-08-02)** | `native-20260731-c8-full44` + `full45` passed the full 49-action composition twice. `native-20260802-cutover-recovery5` repeated it on fresh GPU-rendered OMEN/i5 processes with the hash-identical candidate, 20/20 coverage, zero client/server native use or poison trips, clean save integrity, ownership contention, Gateway replay, and clean rejoin. The same window runtime-proved the session-scoped epoch across a real AM4 restart and rejected an old-session mutation. |
| C9 | **Machine/artifact complete; subjective acceptance pending** | `native-20260802-c9-motion6`: both foreground-verified physical clients rendered the real remote player through 20 s motion legs in both role/direction combinations. Both ordinary observers completed with zero holds, gaps, resyncs, failures, native use, or poison trips; the injected 20-frame loss recovered by reliable resync in 0.895 s. The retained 2560×720 side-by-side clip covers both views; Derek's `smooth`/`rough`/`mixed` verdict remains open. |
| C10 | **Paired release accepted on AM4; breadth and finalization remain** | `m7-c10a-20260802-r4` from `53260467` passed `native-20260802-c10a-r4`: both physical clients completed all 49 actions and one relaunch, forced zone resume passed, and OMEN/i5/server native totals and poison trips stayed zero. r1-r3 remain failed falsifiers. Four `[VERIFY]` items, separate vehicle and mount gates, containers/stations, AI/creatures, P7 promotion, fallback deletion, and the post-deletion release remain. |

C0 also proved the unattended recovery edges needed by the later ladder: the harness
waits for Steam Cloud profile visibility, promotes a completed interrupted `.fch.new`
transaction through Valheim's Cloud API, and bounds graceful/forced client shutdown.
The local Gateway explicitly uses the AM4 authoritative window with the retired
one-seat alpha gate disabled, so the two-client composition cannot be mistaken for a
Valheim capacity failure.

## Completion contract

The cutover is complete only when one promoted release satisfies all of these conditions:

1. **Lumberjacks is the only remote game-data path.** Steam may launch Valheim and prove
   game ownership, but no Steam session ticket, `ZSteamSocket`, Steam P2P/UDP packet, or
   native Valheim peer connection establishes or maintains the world session.
2. **Cold join is Lumberjacks-native.** An enrolled client launches, authenticates to
   Lumberjacks, receives the server/world descriptor, creates its local game-session
   state, loads the character, and reaches `Got character ZDOID` without
   `ServerHandshake`, `ClientHandshake`, or network-delivered `PeerInfo`.
3. **Reliable game traffic is Lumberjacks-carried.** Routed RPC, target-ZDO RPC, broadcast
   RPC, and direct peer/control messages cross the authenticated ordered Lumberjacks lane.
   No selected message has a native fallback in the cutover release.
4. **ZDO replication is Lumberjacks-owned end to end.** A server-side mutation journal and
   Lumberjacks interest policy choose deltas and snapshots. Clients use a typed apply
   adapter. Delivery no longer depends on `CreateSyncList`, and inbound state no longer
   enters through `RPC_ZDOData`.
5. **Ownership is explicit and single-writer.** Lumberjacks carries an epoch/lease for
   each transferable object; the Valheim server remains the canonical game authority and
   persistence owner. `ReleaseNearbyZDOS` no longer decides network ownership.
6. **World and zone synchronization are Lumberjacks-owned.** World identity, bootstrap
   epoch, zone membership, snapshots, deltas, tombstones, and resync cross Lumberjacks.
   Valheim may still generate terrain and run simulation locally; those are game-engine
   responsibilities, not remote networking.
7. **Motion is Lumberjacks-authoritative for presentation.** Sequenced motion frames are
   the sole remote transform source. Player-ZDO deltas cannot write the same transform.
   Loss produces a bounded stale/freeze/resync result, never a hidden native correction.
8. **Native poison stays green.** A shipping telemetry guard records zero remote calls to
   the native send/receive, handshake, `ZDOData`, and `RoutedRPC` network funnels during
   cold join, steady play, zone crossing, reconnect, and disconnect.
9. **The real scenario composes.** Both physical clients join, see each other, move,
   interact, acquire inventory, transfer ownership, cross a zone boundary, disconnect,
   resume, and preserve world state under one run id with no human driving either client.
10. **P7 runs the final build.** The same native-poison scenario passes against P7, the
    previous artifact remains the rollback mechanism, and feature-flag fallback code is
    absent from the final artifact.

An opaque tunnel carrying vanilla `ZRpc` packets through Lumberjacks would satisfy item 1
but not items 3–7. It is not an acceptable final state.

## Architectural posture

Keep Valheim as the dedicated simulation and persistence authority. Replace its remote
networking, not its game rules.

| Lumberjacks lane | Carries |
| --- | --- |
| Authenticated reliable ordered WebSocket | session admission, world descriptor, direct and routed RPC envelopes, ZDO snapshots/deltas/tombstones, ownership leases, zone membership, hard motion corrections, acknowledgements |
| Authenticated sequenced UDP with WebSocket fallback | player motion frames only |
| Local client adapter on Unity's main thread | applies validated messages to Valheim objects and dispatches gameplay handlers |
| Server adapter | observes canonical mutations, validates actions, publishes authoritative results, and owns save timing |

Use the existing enrollment credential, WebSocket session/resume token, recipient
partition, UDP token, and Gateway process. Do not add another public listener or another
identity system. Credentials and platform identifiers never enter retained evidence.

All worker I/O ends in a bounded main-thread queue. No HTTP, WebSocket read, reconnect
wait, or retry loop may block Unity's thread.

## Slice ladder

The slices are sequential because each removes the fallback required to prove the next
one. A slice is complete only after its real-client artifact exists under
`fieldlab/runs/native-valheim/`.

### C0 — Native-use ledger and unattended composition driver

**Build**

- Add counters and structured receipts at the five mapped native funnels:
  `ZSteamSocket.SendQueuedPackages`, `ZSteamSocket.Recv`, the three handshake messages,
  native `ZDOData`, and native `RoutedRPC`.
- Distinguish local adapter dispatch from data received through a native socket.
- Add `nativeNetworkPoisonEnabled`: when armed on AM4, any remote native send/receive is
  refused and produces a deterministic receipt naming only the funnel and message class.
- Instrument the exact connection stages surrounding the current 1.97–1.99 second
  pre-`PeerInfo` block. This is attribution, not an attempt to tune the native path.
- Extend the existing client harness with one run manifest that can drive movement,
  interaction/pickup, a disposable ownership target, zone transition, disconnect, and
  resume. Every action has a bounded deadline and cleanup for its exact run-tagged object.

**Real proof**

- Run the baseline pair once with poison off and retain the expected nonzero native
  ledger.
- Arm poison and prove the current native join stops at the first forbidden boundary.
  This falsifies a guard that merely reports without enforcing.

**Failure mode**

- A native call without a receipt, or a receipt that cannot be correlated to one client
  and run id, blocks the entire ladder.

**Exit**

- One machine-readable summary can say which native funnels were used, when, and by which
  phase. It never infers "zero" from missing logs.

**Cost:** 1–2 focused days.

### C1 — One durable Lumberjacks game session

**Build**

- Extend the existing authenticated WebSocket session into a Valheim game-session
  contract with server instance id, world id, protocol version, client connection id,
  monotonic reliable sequence, acknowledgement, bounded send queue, and resume epoch.
- Reuse the existing WebSocket and UDP bindings. Do not create a parallel connection per
  subsystem.
- Add a server-to-client control request and client-to-server response that run while the
  ordinary Steam world session is still present.
- Bank completed worker messages and apply them only during bounded Unity `Update`.

**Real proof**

- Both clients exchange a numbered request/response through Lumberjacks.
- Drop the WebSocket after the request, reconnect with the resume token, and receive
  exactly one response with the same connection id and a later resume epoch.

**Failure mode**

- Withhold the selected response while Gateway health remains green. The client must
  report a bounded timeout; it must not accept a native copy.

**Exit**

- The reliable lane has ordering, deduplication, reconnect, and backpressure receipts
  suitable for every later semantic slice.

**Cost:** 1–3 focused days; most substrate already exists.

#### Mandatory replan after C1 — 2026-07-30

**What the boundary proved**

- C1's `client_connection_id` survives a socket detach/resume, while `resume_epoch`
  advances and `server_instance_id`, `world_id`, and protocol version remain explicit.
- The Gateway retains a bounded 256-frame reliable send queue, replays an unacknowledged
  frame with the same sequence, removes cumulatively acknowledged frames, and deduplicates
  a client response by monotonic client sequence plus idempotency key.
- Both real clients passed the same forced-drop and withheld-receipt cells with plugin
  SHA-256 `a0213ebd9d55f739539fb3211d932fe1825f9d8f3adfaa21bef38452c04842a2`.
  Gateway health remained green after the run.
- Worker completion remains banked until Unity `Update`; the scenario driver never
  handles game state from the WebSocket worker.

**Limits that remain explicit**

- C1 durability is an in-memory two-minute socket-resume contract, not Gateway-process
  persistence. C3/C5 snapshots must reconstruct semantic state after a Gateway restart.
- A fresh Valheim process currently creates a new C1 connection id. C7 must bind the
  enrolled logical peer/character independently of this transport incarnation.
- The AM4 client route used its authenticated tailnet/private-plane capability through a
  bounded SSH tunnel. Enrollment-backed cold join is intentionally still a C7 proof.
- Enabling the canonical game session disables the legacy motion-only WebSocket so there
  is one active subsystem connection. C6 must move motion onto this canonical binding;
  observe-only motion is deliberately unavailable in the interim.
- C1 carried typed probe control only. No native RPC, ZDO, ownership, world, zone, motion,
  handshake, or Steam funnel was suppressed by this slice.

**C2 retained result**

1. **C2a — direct control pulse:** complete in `native-20260730-c2a-final`.
2. **C2b — routed RPC:** complete in `native-20260730-c2b-final`. The fixed registry
   carried the full envelope shapes in both directions, including broadcast and a
   zero-argument target-ZDO package. Dispatch occurred from Unity `Update`; the selected
   native hashes failed closed.
3. This is a boundary proof for the selected registry, not a claim that every Valheim
   method hash is migrated. The remaining registry expansion and post-admission controls
   stay in the native-use burn-down through C7.

The revised remaining estimate is **18–38 focused engineering days**. C3-C7 still
dominate the range, and C3's mandatory replan remains in force.

### C2 — Routed RPC and direct peer/control replacement

**Build**

- Intercept the complete `RoutedRPCData` shape before `RouteRPC`: message id, sender,
  target peer, target ZDO, stable method hash, and parameter package.
- Carry it through the C1 reliable lane and dispatch it directly to
  `HandleRoutedRPC`/the registered gameplay handler on the recipient's main thread. Do
  not invoke network `RPC_RoutedRPC`.
- Add the equivalent typed envelope for steady-state direct control messages. Migrate
  player/reference-position lists, server pulse, errors, disconnect, and other
  post-admission controls to that envelope.
- Suppress native delivery by message class only after Lumberjacks has accepted the
  envelope. Cutover mode has no per-message native fallback.

**Real proof**

- Exercise all routed shapes in one unattended pair run:
  client→server, server→client, server broadcast, and target-ZDO dispatch.
- Include one real idempotent gameplay interaction, not only a synthetic echo.
- Exercise a direct server pulse, withhold the Lumberjacks copy, and prove the client
  marks it stale instead of receiving a native pulse.

**Failure mode**

- Drop one numbered routed response. The originating action must time out once; no
  duplicate game mutation and no native response are allowed.

**Exit**

- Native `RouteRPC`/`RPC_RoutedRPC` remote counters stay zero while gameplay handler
  counters advance in both directions.

**Cost:** 2–4 focused days.

### C3 — Lumberjacks-owned ZDO mutation, selection, snapshot, and apply

**Build**

- Add a server-side changed-object journal at the data/owner revision mutation boundary.
  It records authoritative object id, prefab, zone, data revision, owner revision, body,
  and tombstone without waiting for `CreateSyncList`.
- Make the Lumberjacks interest service select recipients from explicit client reference
  positions and zone membership.
- Define snapshot and delta envelopes with world epoch, zone epoch, object revision,
  tombstone, source sequence, and recipient acknowledgement.
- Replace the client's synthetic `ZDOData` package replay with a typed main-thread apply
  adapter that performs validation, create/update/delete, revision checks, owner fields,
  position, and deserialize explicitly.
- Retain the current redirected queue as migration input only until the new journal proves
  parity; then remove `CreateSyncList` as a delivery source.

**Real proof**

- Mutate one allow-listed object that Valheim `CreateSyncList` did not select. The
  recipient must instantiate/apply the newer Lumberjacks revision.
- Join a second client after the mutation and prove snapshot-then-delta ordering gives it
  the same object and revision.
- Delete the run-tagged object and prove the tombstone removes it on both clients.

**Failure mode**

- Send a stale revision and a malformed body. Both are rejected before object mutation;
  neither enters `RPC_ZDOData`, and the next valid revision still applies.

**Exit**

- Native candidate count may still be observed for comparison, but it cannot alter what
  is delivered. Network `RPC_ZDOData` and adapter calls through that handler remain zero.

**Cost:** 3–6 focused days.

#### Mandatory replan after C3 — 2026-07-30

**What the boundary proved**

- The AM4 server created one run-tagged ZDO four zones beyond both clients'
  native sync rings. Across 1,198 observed `CreateSyncList` passes, that object was
  never a selected native candidate, yet Lumberjacks delivered it.
- The Gateway reconstructed the durable object from its WAL after an intentional
  process restart. Late-arriving i5 then typed-applied one snapshot; both clients
  rejected one stale revision and one malformed body before mutation, applied the
  next valid delta, and applied the typed tombstone.
- OMEN and i5 used distinct run-scoped recipients, acknowledged all queued
  deliveries, recorded zero network `RPC_ZDOData` calls and zero typed-apply
  failures, relaunched fresh Valheim processes, rejoined, and stopped.
- The final journal state had one durable tombstone, two interests, and zero pending
  deliveries before the disposable development state was deleted.

**Limits that remain explicit**

- This is a boundary proof for one synthetic run-tagged ZDO body, not parity for
  every prefab, component-specific mutation, or save/load semantic.
- The dedicated server remains the canonical simulation and persistence authority.
  C3 observes its mutation seams with Harmony; it does not move game rules into the
  Gateway.
- The C3 proof used run-scoped HTTP journal ingress/poll/ack beside C1. Final
  architecture still requires these semantic frames to ride the authenticated,
  ordered C1 session; the HTTP surface remains a development/control seam, not a
  second shipping gameplay connection.
- Restarting the Gateway preserved the semantic journal but exposed C1's in-memory
  identity limit: the socket resumed after one transient rejection with a new
  connection id. Ownership cannot bind to that transport incarnation.
- Legacy redirect/apply remains available outside the C3 gate and on P7. C3 proves
  the replacement seam; C8/C10 still own native-zero composition and fallback
  deletion.

**Revised C4**

1. Derive a durable logical peer id from the enrolled principal, server/world, and
   seeded character; persist it across Gateway and Valheim process restarts. Keep
   transport connection id and resume epoch as replaceable incarnations.
2. Register C3 snapshot/delta/tombstone/ack frames on the C1 reliable session and
   prove WAL replay plus recipient isolation there. Keep the current HTTP endpoints
   only for bounded lab control/status.
3. Issue server-originated ownership leases to the logical peer, attach lease epoch
   to a mutating target-ZDO action, reject expired/wrong leases before mutation, and
   prove reclaim/reissue on disconnect.
4. Use one run-tagged pickup in each direction so the action result and inventory
   return compose through C2/C3. Native ownership-transfer triggers must be
   suppressed for the selected object.

This adds an identity/carriage hardening cell to C4 but removes ambiguity before
ownership. The revised remaining estimate is **17–35 focused engineering days**,
including 1–2 days of remaining direct/routed method burn-down folded into C4-C7.

#### C4a checkpoint — logical identity and canonical ZDO carriage

`native-20260730-c4a-second` passed the first two revised-C4 prerequisites on the
real AM4/OMEN/i5 composition:

- Gateway derives an opaque logical peer from stable authenticated Valheim scope.
  The server, OMEN, and i5 each retained exactly one distinct logical id. Gateway
  restart and fresh Valheim processes changed transport connection ids without
  changing those logical ids; same-process C1 resume still retains its connection
  id and advances its resume epoch.
- C3 mutation, interest, delivery, mutation-receipt and journal-ACK frames now ride
  C1's authenticated reliable WebSocket. The local HTTP journal remains only for
  bounded status/reset control.
- The Gateway replayed the durable object from WAL after an intentional restart.
  Six server mutations were accepted, late i5 received the snapshot, both clients
  applied the valid delta and tombstone, and the final two-recipient queue reached
  zero pending before disposable state was deleted.
- Every run-scoped ZDO row reported `canonical_session`; HTTP fallback/failure rows,
  selected `CreateSyncList` candidates, network `RPC_ZDOData`, and typed-apply
  failures were zero. Both clients completed fresh-process reconnect on the
  intended GPU and stopped without operator input.

This proves identity and carriage only. It does not create or enforce an ownership
lease, gate a gameplay mutation, or prove a real pickup result. C4 therefore remains
open at its ownership/action boundary.

### C4 — Ownership lease and action boundary

**Build**

- **Complete in C4a:** establish a durable logical peer id independent of the current WebSocket
  connection id, and preserve it across Gateway and fresh-Valheim-process restart.
- **Complete for C3 frames in C4a:** carry C3 journal frames on C1's authenticated
  reliable session. Carry ownership frames on the same session in the next cell.
  HTTP journal endpoints remain bounded lab control/status only.
- Replace `ReleaseNearbyZDOS` ownership transfer with a server-originated Lumberjacks
  lease carrying object id, owner logical-peer id, lease epoch, expiry, and reason.
- Keep the Valheim dedicated server as the sole mutation authority. Lumberjacks records
  and distributes the current lease; clients never grant ownership to themselves.
- Attach the lease epoch to mutating target-ZDO actions. The server validates it before
  changing world or inventory state.
- On disconnect/expiry, the server reclaims or reissues the lease through Lumberjacks.
  Gateway loss fails closed for new mutations while already-applied read state remains
  visible.

**Real proof**

- Spawn one run-tagged pickup, lease it, have one unattended client acquire it, and prove
  the server mutation and inventory return reach both clients through C2/C3.
- Repeat with the other client as lease holder.

**Failure mode**

- Submit an expired or wrong lease while the object is visible. The action must be
  rejected, inventory must not change, and a later valid action must succeed exactly once.

**Exit**

- Native ownership-transfer triggers cannot change the owner, and every observed owner
  revision maps to a Lumberjacks lease epoch.

**Cost:** 3–6 focused days.

#### C4 completion checkpoint — ownership lease and action authority

`native-20260730-c4b-tenth` closed C4 on the real AM4/OMEN/i5 composition:

- The dedicated server created one real Raspberry ZDO for each client. Gateway leases
  were keyed by run, world, object, logical holder and epoch; a forced socket loss
  reclaimed epoch 1, the wrong epoch and expired epoch 2 were rejected before mutation,
  and reissued epoch 3 authorized the action.
- The server temporarily assigned the selected object to the lease holder, restored
  server ownership, destroyed the authoritative ZDO without a native destroy RPC, sent
  one action result, and received one completed result receipt for each client.
- Selected `CreateSyncList`, `ReleaseNearbyZDOS`/`SetOwner`, `ItemDrop.RequestOwn`,
  `Humanoid.Pickup`, `DestroyZDO`, and destroyed-ZDO delivery paths were poisoned.
  The live falsifying cells first exposed a swallowed completion assertion, stale
  run state, and a native auto-pickup double credit; each remained retained and the
  accepted cell proved the corrected boundary.
- OMEN's inventory changed from 9 to 10 units and i5's from 11 to 12 units. Each
  authoritative completion was applied and reliably acknowledged exactly once.
  Both clients then launched a fresh Valheim process, resumed the scenario on the
  intended GPU, completed, and stopped without operator input.
- The machine reducer `ownership-lease-cutover-summary.json` passed every client,
  server, runtime-gate, pending-delivery, and disposable-journal cleanup check.

This proves the selected pickup ownership/action boundary. It does not make world
bootstrap, zone membership, general-prefab breadth, motion, or Steam transport
Lumberjacks-owned; those remain C5–C8.

### C5 — World bootstrap and zone/interest synchronization

**Build**

- Publish a server-shaped world descriptor through C1: protocol/release, world id, seed,
  seed name, world-generation version, network time, save epoch, and initial zone.
- Make the client initialize its connected-world state from that descriptor rather than
  the server branch of native `PeerInfo`.
- Publish explicit zone enter/leave membership, snapshot epoch, snapshot-complete marker,
  deltas, and tombstones. Chunk large snapshots with bounded acknowledgements.
- Separate terrain generation from network membership: Valheim may generate/render the
  zone, while Lumberjacks is the only source of which server objects belong there.
- Add resync from the last acknowledged zone epoch after reconnect.

**Real proof**

- Give a client a valid Lumberjacks descriptor while blanking the corresponding native
  world fields; it must enter the correct world.
- Move one client across an automated zone boundary. Native sector selection for the
  destination is withheld, yet Lumberjacks membership must seed the zone and later unload
  the run-tagged object on leave.
- Disconnect during snapshot chunking, resume, and finish with one snapshot-complete
  marker and no duplicate object.

**Failure mode**

- A wrong protocol/world-generation version must stop before scene entry with a
  deterministic reason. Removing membership must prevent spawn or cause deterministic
  unload; stale objects may not linger silently.

**Exit**

- World identity and visible object membership can be reconstructed from Lumberjacks
  receipts alone. Native `PeerInfo` world fields and `CreateSyncList` sector membership
  have no delivery effect.

**Cost:** 4–7 focused days.

**Accepted boundary — 2026-07-30**

- In `native-20260730-c5-final`, OMEN and i5 used ComfyNetworkSense 0.5.44
  (`0a9950b7...f648`) and the intended RTX 5070 / Intel Iris Xe renderers. Both
  observed blank native world fields, accepted the Lumberjacks descriptor for
  `initial_zone=0,0`, joined, and completed the unattended scenario.
- Each client received three complete run-tagged typed ZDO bodies for its entered
  membership. The client deliberately dropped the canonical socket after applying
  chunk 1 but before acknowledging reliable sequence 9. The same sequence replayed
  idempotently, chunks 2 and 3 remained semantic-ACK gated, and exactly one
  snapshot-complete marker followed.
- Membership leave destroyed all three selected objects on the client and server.
  The deliberately withheld membership produced zero local objects. Native
  `CreateSyncList` candidates for the selected objects were removed before delivery:
  57/57 in the OMEN boundary and 90/90 by the i5 boundary.
- `native-20260730-c5-fault-protocol` and
  `native-20260730-c5-fault-worldgen` retained deterministic
  `descriptor_protocol_mismatch` and `descriptor_world_generation_mismatch`
  pre-scene stops. The latter used the accepted 0.5.44 artifact.
- The retained `c5-boundary-summary.json` labels the limits: this is the selected
  run-tagged membership path, not general-prefab breadth, motion, cold join,
  native-zero composition, or P7 promotion.

#### Mandatory replan after C5 — 2026-07-30

- **Proceed to C6.** C5's descriptor, typed apply, recipient isolation, semantic ACK,
  replay, leave, and withhold seams all crossed the real AM4 boundary. The kill
  criterion for deterministic chunk resume did not fire.
- C6 must bind the existing motion lane to C1's canonical connection and C3's actual
  player ZDO. C5 source verification found that Valheim's
  `CreateNewZDO(position, prefabHash)` overload does not serialize the prefab; any
  C6-created or rebound entity must verify the real prefab/identity explicitly rather
  than infer it from that overload.
- The first C6 cell remains binary, not a feel test: one numbered motion range in each
  direction, all native remote-transform writers poisoned, then a withheld range that
  ends in hold plus explicit reliable resync. No smoothing/transpiling work is unlocked.
- C5's Gateway world/zone state is process-resident. Time-derived snapshot epochs
  prevent reuse across a process restart, but C7 must make descriptor publication and
  cold-join reconstruction independent of an already-established native session. C7's
  early 60-second socket-quarantine falsifier remains mandatory.
- General-prefab membership and ownership/RPC method breadth are not smuggled into
  C5's claim. C8 must run the complete poison ledger and reopen the owning slice for
  any unadmitted native method.
- Revised remaining cost is **9–20 focused engineering days for C6–C10**, plus C10's
  two bounded P7 world reloads. C7 remains the largest architectural uncertainty;
  C6 should stay within its existing 2–4 day band because the carriage and client
  apply substrate already exist.

### C6 — Lumberjacks motion authority, without tuning

**Build**

- Bind the existing sequenced motion lane to the C1 connection id and C3 player entity.
- Make it the sole remote writer of player position, rotation, and velocity. Mask those
  fields from ordinary player-ZDO deltas and disable every native remote-transform writer
  in cutover mode.
- Use UDP for current motion, WebSocket for fallback carriage, and the reliable lane only
  for teleport/hard correction and resume baseline.
- Define loss behavior now: age out, hold the last safe presentation, then apply an
  explicit reliable resync. Do not fall back to native motion.

**Real proof**

- Run the existing straight and stutter movement patterns in both directions with role
  reversal. The non-moving client must apply numbered Lumberjacks frames while all native
  transform-writer counters remain zero.
- Withhold one frame range and prove the documented hold/resync path.

**Failure mode**

- Loss may look rough at this rung, but it may not produce a native correction, an
  implausible teleport, or an unbounded stale player.

**Exit**

- Motion authority is binary-proven. Smoothing quality is deliberately not tuned here.

**Cost:** 2–4 focused days.

**Retained result (2026-07-30):** C6 is accepted in
`native-20260730-c6-eighth`. OMEN and i5 rendezvoused without operator input and
applied numbered canonical motion to the real remote player in both directions.
The selected native remote-transform writer was suppressed after canonical apply,
and native position writes to that remote identity were masked. OMEN deliberately
withheld sequences 600–619; i5 recorded one gap hold with `native_fallback=false`,
applied the reliable sequence-619 resync, and queued its ACK. Both clients relaunched
once in fresh Valheim processes and completed the scenario. i5's unreachable
advertised UDP path fell back to binary WebSocket; OMEN exercised UDP. The Gateway
recorded zero unauthorized and zero stale motion drops, plus two invalid early
frames retained as a caveat. This is binary authority evidence only: subjective
feel, smoothing, Steam-free join, general breadth, native-zero composition, and P7
promotion remain open.

### C7 — Steam-free cold join and logical Valheim peer

**Build**

- Start C1 directly from the client request manifest after Steam launches Valheim.
  Do not issue `+connect` to a Valheim UDP endpoint.
- Authenticate with the existing enrollment, obtain C5's descriptor, and create the
  minimal in-process logical peer/session state Valheim needs for scene and character
  lifecycle. The logical peer has no `ZSteamSocket`.
- Replace connection-state, peer-list, reference-position, disconnect, and shutdown
  dependencies with the C1/C2 session.
- Remove Steam ticket verification and the native
  `ServerHandshake`→`ClientHandshake`→`PeerInfo` path from the cutover mode.

**Early falsifying check**

- Before filling out the cold join, prove Valheim can remain in-world for 60 seconds with
  a logical peer and the native socket quarantined. If the engine requires a concrete
  socket, stop and replan the adapter seam before adding an opaque packet tunnel. A byte
  tunnel is not silently accepted as completion.

**Real proof**

- From a stopped client, launch, connect only to Lumberjacks, and reach
  `Got character ZDOID` with native poison armed.
- Repeat concurrently on OMEN and i5, then disconnect and resume both through C1.

**Failure mode**

- Invalid enrollment, wrong release, unavailable Gateway, or wrong world descriptor must
  fail closed with a deterministic client receipt. None may try the native server.

**Exit**

- `ZSteamSocket` is never connected or used for the world session. The old pre-`PeerInfo`
  block is absent by construction, and phase timing shows where the Lumberjacks cold join
  spends its time.

**Cost:** 4–7 focused days. This is the highest architectural-risk slice.

**Retained early falsifier (2026-07-30):** `native-20260731-c7-fourth`
passed concurrently on OMEN and i5. Each client had an authenticated canonical
Lumberjacks session and accepted the C5 world descriptor before closing its selected
native `ZSteamSocket`. OMEN held the live scene for 60,001 ms with a zero native
funnel delta while suppressing 7,962 socket sends, 7,961 RPC updates, and 1,168
native RPC invocations. i5 held for 60,004 ms with a zero native funnel delta while
suppressing 7,031 socket sends, 7,030 RPC updates, and 1,129 invocations. Both
reported `native_fallback=false`, completed one fresh-process resume, and stopped
unattended. This proves the running scene does not inherently require a continuously
live native socket. It does not satisfy C7's real proof because initial world entry
still used native `+connect`, handshake, and peer construction.

**Retained cold-join result (2026-07-31):** `native-20260731-c7-cold-final`
closed C7 on the final candidate. OMEN and i5 launched without `+connect`, started
the authenticated C1 session from the request manifest, validated C5's descriptor,
constructed the local logical server peer, queued the typed character id, and reached
the joined scene. Each repeated the path in a fresh Valheim process. All four client
ledger sessions had poison armed and recorded zero native calls, poison trips, drops,
or writer faults. AM4 reconstructed both logical clients after resume and recorded
zero selected native peer, handshake, `PeerInfo`, `ZDOData`, or routed-RPC ingress.
Its 4,357 aggregate native rows were idle dedicated-host accept polls, not selected
logical-client ingress, and are carried forward as an explicit C8 server-poison check.

`native-20260731-c7-negative-second` passed all four required fail-closed cells:
invalid enrollment, unavailable Gateway, wrong release, and wrong descriptor/protocol.
No cell joined or tried a native server; all remained poison-armed and native-zero.
The retained boundary is in
[`evidence/c7-steam-free-cold-join/`](evidence/c7-steam-free-cold-join/).

#### Mandatory replan after C7 — 2026-07-31

**What the boundary proved**

- The highest-risk architectural branch is closed: Valheim can enter and re-enter the
  character scene from Lumberjacks state without a `ZSteamSocket`, native handshake,
  network `PeerInfo`, or opaque vanilla packet tunnel.
- C1/C2/C5 already contain enough typed state to construct the minimum local peer.
  Transport incarnation and fresh process lifetime do not define logical identity.
- Enrollment, Gateway availability, release compatibility, and descriptor validation
  all have bounded fail-closed outcomes before scene entry.

**Limits that remain explicit**

- C7 proves connection/bootstrap only. It does not broaden C2/C3/C4/C5/C6's selected
  registries to the complete gameplay method and prefab surface.
- The dedicated host still performs idle Steam accept polling. C8 must arm poison on
  AM4 as well as both clients and classify those polls explicitly; a selected native
  ingress or egress call remains a failure.
- Descriptor-rejection telemetry currently repeats while the rejected client remains
  alive. C8 preparation must make that terminal receipt single-shot so the complete
  fault window remains bounded and legible.
- C8 must audit the scenario against every completion-contract action before its first
  live run. A missing pickup, target-ZDO action, ownership contention, zone transition,
  Gateway interruption, UDP loss, disconnect/rejoin, or save-integrity receipt blocks
  execution rather than becoming an inferred pass.

**Revised ordered gates**

1. **C8a — candidate closure:** deduplicate terminal failure telemetry, make server
   poison semantics explicit, and produce a machine-checked coverage manifest for the
   complete scenario.
2. **C8b — first composition:** run every required action and both injected faults from
   clean launches under one run id; any native trip or semantic divergence reopens its
   owning slice.
3. **C8c — repeat and integrity:** repeat the same manifest from clean launches and
   require identical boundary checks plus stable world/save integrity.
4. **C9:** capture and, only if objective evidence requires it, tune Lumberjacks motion.
5. **C10:** promote one paired release to P7, re-prove, delete fallback branches, cut a
   new final artifact, and re-prove again.

No new product decision is opened: existing policy already rejects an opaque tunnel,
requires fail-closed native poison, and keeps AM4 as the development lane. The revised
remaining estimate is **5–11 focused engineering days**, plus C10's two bounded P7
world reloads.

### C8 — Native-zero composition and fault window on AM4

**Build**

- Arm native poison unconditionally for the candidate.
- Make the C0 scenario run the full joined-world sequence under one manifest:
  cold join, co-presence, movement both ways, pickup both ways, target-ZDO interaction,
  ownership contention, zone exit/enter, WebSocket resume, clean disconnect, and rejoin.
- Add one bounded Gateway interruption and one deliberately dropped UDP range. Do not
  restart the 9.15M-ZDO server between ordinary cells.
- Record world/save integrity before and after without making a test-data backup.

**Real proof**

- Run the complete scenario twice from clean client launches.
- Required summary: native funnel deltas all zero; reliable sequences contiguous or
  explicitly resumed; duplicates/rejects/pending explained; both inventories correct;
  world epoch stable; zone objects converge; motion resync bounded; save integrity clean.

**Failure mode**

- Any native poison trip, silent fallback, duplicate mutation, unexplained object
  divergence, or save-integrity change reopens the owning slice. Do not average it away
  with more runs.

**Exit**

- Native replacement is complete on AM4. Only now may the tuning lab resume.

**Replanned cost:** 2–4 focused days. C8 now includes the explicit breadth audit,
server-poison classification, two complete fault compositions, and save-integrity
comparison.

#### Mandatory replan after C8 â€” 2026-08-01

**What the boundary proved**

- C8 closed on the acceptance pair full44 + full45 on one frozen build: the complete
  49-action composition (Steam-free cold join, co-presence, movement both directions,
  pickup both clients, target-ZDO, two-peer ownership contention, zone exit/enter,
  Gateway restart, WebSocket resume, UDP drop window, clean disconnect/rejoin) run
  twice from clean client launches with unconditional client AND server native poison,
  zero poison trips, clean save integrity, and 20/20 machine-checked coverage per run.
- The road there receipted six additional wall classes in one night, none of them
  gameplay-boundary defects: (11) the canonical zone bank keys on the world-stable
  epoch while ZDO ids are server-session-scoped, so a server restart replays phantom
  uids and a duplicate terrain compiler livelocks spawn readiness; (12a) the journal
  observe verdict was blind to idempotent receipt-snapshot arrival; (12b) the drive
  protocol constructed but never enqueued its valid receipt-required delivery â€” the
  verdict had been passing on bank warmth; (13) the intermittent whole-client freeze
  was the Windows QuickEdit console selection blocking the BepInEx console WriteFile
  on Unity's main thread (root-caused from a live hang dump; consoles now disabled on
  both clients); (14) lease control frames shared the bulk outbound cap and a
  post-resume interest re-publish flood starved a reissue grant at queue_depth=256;
  (15) a legitimate local teleport had no legal channel past the observer's 30m
  fail-closed guard and now announces on the reliable resync lane. A seventh finding
  (the portal roundtrip return leg deadlining 0.2s after area-ready) was calibration,
  fixed against the decompiled vanilla teleport contract.
- The earlier drive-object leak finding (zone 35,-1, 1245 â†’ 1790) was real but
  coincidental to the spawn failures; the run-tagged `cutoverResidueCleanup` verb now
  rides the orchestrator's cleanup path unconditionally and receipts `destroyed=0` on
  clean runs as the steady-state hygiene proof.
- Method note: every wall above was closed from receipts plus decompiled/read source
  (ilspycmd against the pinned assembly, dump analysis via procdump + WinDbg), one
  named defect per failed run. The observability the campaign built is what made that
  cadence possible.

**Limits that remain explicit**

- C8 proves the selected/registered method and prefab surface, not complete gameplay
  breadth. Per the C8 breadth audit (`C8-BREADTH-AUDIT-2026-07-31.md`): all 33 P1
  methods — 29 instance plus four global — must be admitted and contract-tested before
  native fallback deletion in
  C10; four `[VERIFY]` items (`RPC_SetConnection`, `RPC_TeleportPlayer`, `UseStamina`,
  and vehicle `RequestControl`/`ReleaseControl`/`RequestRespons`) and the component-family
  gates remain. Vehicles and mounts are separate runtime gates, followed by
  containers/stations and AI/creatures; vehicle/mount ownership is the largest genuinely
  novel work left. P2/P3 may ship behind the poison
  tripwire with the deferred bucket documented in the C10 gate.
- **"vehicles/mounts" is two gates, not one** (pre-C10a research, 2026-08-01 —
  [RESEARCH-vehicles-mounts-ownership](docs/RESEARCH-vehicles-mounts-ownership-2026-08-01.md)).
  A ship separates *control* from *simulation authority*: `ShipControlls` grants the helm
  by writing `ZDOVars.s_user` and never calls `SetOwner`, while ownership moves only via
  `Ship.UpdateOwner` on a 2 s timer — so the steady state is helmsman ≠ physics owner and
  rudder input round-trips through a third machine. A mount fuses the two: `Sadle.cs:349`
  sets `s_user` and `SetOwner` in the same handler. The audit's single family therefore
  costs and gates as two with opposite shapes; do not plan one slice for both.
- **The lab cannot currently run the vehicle/mount experiments.** The harness cannot spawn
  a boat or a cart and cannot tame a Lox (`spawn` and `tame` are cheat-gated and the
  dedicated server refuses them), and no scenario verb boards a vehicle. Any C10a
  vehicles/mounts cell therefore needs new mod code *and* a world-seeding step before it
  can run at all — cost that belongs in the slice estimate, not discovered inside it.
- **C4's ownership interception is scoped to one funnel, and the other five are open.**
  Vanilla mutates ZDO ownership from client-side RPC handlers in at least five verified
  places (`Ship.cs:696`, `Sadle.cs:349`, `Vagon.cs:141`, `ArmorStand.cs:347`,
  `ItemStand.cs:385`), none through `ReleaseNearbyZDOS` — which falsifies the
  single-funnel headline in [NETCODE-OWNERSHIP-MAP.md](NETCODE-OWNERSHIP-MAP.md) (the
  narrower proximity-release claim still holds). `OwnershipLeaseCutoverRunner`'s prefix on
  `ZDO.SetOwner(long)` is attached globally but gated on `ReleaseScopeDepth > 0`, raised
  only inside the `ReleaseNearbyZDOS` prefix, so those five paths pass through untouched.
  Three of them are the vehicles/mounts surface. Whether that is a gap or correct scoping
  is a C10a question with evidence attached, not an assumption to inherit.
- Wall 11's durable source fix is implemented, contract-tested, and runtime-proved as of 2026-08-02:
  descriptor protocol 2 publishes the stable world component plus Valheim's server
  `ZDOMan` session component; every journal/ownership payload uses the accepted combined
  epoch; the Gateway retains a same-session bank across its own restart and atomically
  invalidates objects, interests, pending delivery, and WAL rows on a server-session
  change. Stale-session mutations/interests fail closed. Physical run
  `native-20260802-cutover-recovery5` followed one real AM4 restart, changed the epoch,
  passed all 49 actions on both rendered clients, and a valid old-epoch mutation returned
  HTTP 409 without changing the new bank. The interim AM4 WAL-discard rule is retired;
  only release alignment remains for this item in C10a.
- The blast radius of phantom-uid replay is bounded by construction: a full-assembly
  sweep found exactly two load-time non-owned-destroy sites (`TerrainComp.Awake`,
  `SmokeSpawner.Awake`). The epoch fix removes the class.

**Revised ordered gates**

1. **C9 â€” rendered motion quality:** run the already-built motion phase capture
   against the native-zero candidate; produce one side-by-side observer clip covering
   both direction/role combinations; tune only the Lumberjacks presentation path if
   objective evidence requires. C9 must not reopen C0â€“C8 architecture without new
   evidence. Derek's remaining verdict on the retained clip is one word.
2. **C10a â€” admissions, aligned runtime, and remaining breadth:** the 33 P1 contracts
   and one paired mod/Gateway release are accepted on AM4. Resolve the four explicit
   `[VERIFY]` items, then pass separate vehicle, mount, container/station, and
   AI/creature physical gates. Wall 11's release alignment/runtime gate and interim
   WAL-discard rule are closed.
3. **C10b â€” P7 promotion and close:** boot P7 per `RUNBOOK-boot-determinism.md`
   (step 1 evidence BEFORE applying the boot fix), cut one release from one commit,
   run the pair scenario on P7 poison-armed, delete migration-only fallback branches,
   cut and promote the final artifact, re-prove the five boundaries, flip every
   landscape row to `Swapped`, supersede the PINNED notes.

No new product decision is opened: existing policy already rejects an opaque tunnel,
requires fail-closed native poison, and keeps AM4 as the development lane. The revised
remaining estimate is **2â€“6 focused days** (C9 1â€“3, C10 1â€“3) plus C10's two bounded P7
world reloads.

### C9 — Post-replacement motion quality and rendered evidence

This is the first slice allowed to use `fieldlab/experiments/`, patch-load A/B, or the
CRE-E0x material.

**Build/run**

- Run the already-built motion phase capture against the native-zero candidate.
- Tune only the Lumberjacks presentation path if objective evidence requires it.
- Capture a short observer-side rendered clip for both direction/role combinations,
  synchronized with frame timing, target error, receive spacing, applies, and corrections.

**Acceptance**

- No unexplained hard correction, no persistent target divergence, bounded recovery from
  the injected loss, and no new wall-clock hitch attributable to apply.
- Produce one side-by-side clip rather than asking Derek to drive two clients. Derek
  reviews that retained clip once and answers `smooth`, `rough`, or `mixed`; this is
  not a live KVM gate and does not require a rerun.

**Exit**

- Movement correctness is verified by machine evidence, visible presentation has a
  retained reviewable artifact, and Derek's one-word subjective verdict is recorded
  with its actual reviewer.

**Cost:** 1–3 focused days.

#### C9 machine/artifact checkpoint — 2026-08-02

- Physical run `native-20260802-c9-motion6` passed on rendered OMEN and i5 clients
  against AM4 with poison armed, zero native use, one clean resume per client, and
  zero probe failures.
- The ordinary observer windows applied numbered Lumberjacks motion in both directions
  with zero holds or resyncs. The deliberate 20-frame loss held fail-closed and recovered
  through the reliable lane in 0.895 s.
- The foreground-verified, 20.067 s side-by-side artifact is retained under
  `fieldlab/runs/motion-clips/native-20260802-c9-motion6/`; machine findings and artifact
  SHA-256 are recorded in `fieldlab/evidence/c9-motion-quality/README.md`.
- The machine and artifact portions are closed; C9 itself remains open until Derek's
  `smooth`/`rough`/`mixed` verdict is recorded. C10a engineering can proceed in parallel;
  no Workbench/dashboard work is on this cutover path.

### C10 — P7 promotion, fallback deletion, and final close

#### C10a admission-contract checkpoint — 2026-08-02

- One source file in `Game.Contracts` now defines the exact method name, Valheim stable
  hash, global-vs-instance target shape, maximum payload size, priority, and extracted
  payload signatures used by both the Gateway and ComfyNetworkSense build.
- The admitted P1 surface is 33 methods: the previously named 29 instance RPCs plus the
  four P1 global routed registrations `ChatMessage`, `ShowMessage`, `SleepStart`, and
  `SleepStop`. Tests compare every P1 signature to the pinned extractor-v2 inventory and
  reject hash collisions, name/hash mismatch, incomplete target ZDOs, and oversize payloads.
- An outbound method outside the admission contract no longer bypasses evidence. With
  cutover armed it records `routed_rpc_unadmitted_send`; poison blocks it, while an
  explicitly non-poisoned migration window may still use the observed native route.
- The mod Release build is zero-warning/zero-error, its focused suite passes 107/107,
  and the .NET 9 Gateway suite passes 221/221 including exact forwarding and fail-closed
  rejection at the shared contract boundary.

#### C10a first paired-release falsifier and repair — 2026-08-02

- Paired release `m7-c10a-20260802-r1` was built from commit
  `f9c413b0a8a524d7eb074f499b4bc8548c67ca0f`, then deployed hash-exactly to the
  real AM4 server and local Gateway. Both physical clients received the exact DLL and
  completed all 49 C8 actions, including the clean disconnect/relaunch.
- The run is **failed evidence**, not acceptance. Reducer
  `native-20260802-c10a-r1/c7-logical-peer-summary.json` found ten native poison trips
  on each client. The newly instrumented outbound seam resolved them to `Step`,
  `RPC_DamageText`, and `DestroyZDO`. AM4 also exposed `SetEvent`, `GlobalKeys`,
  `LocationIcons`, and the mod's `ComfyNetworkSense_AutoPort` and
  `ComfyNetworkSense_ServerPulse`. This proves the older native-zero claim did not see
  this outbound method-level hole; it does not justify another synthetic pass.
- Per the breadth audit's poison rule, normal-play trips reopen their owning rows. The
  r2 source therefore distinguishes methods that must cross the reliable routed lane
  from methods already owned by a replacement lane. `Step`, damage text, event state,
  and all three mod-owned routed RPCs have exact route contracts. `DestroyZDO`, world
  keys/icons, ping/pong, and ZDO requests are exact, payload-validated suppressions with
  their owning journal, descriptor, or logical-session lane named in the shared contract.
  Gateway injection and client inbound dispatch reject those superseded methods.
- The repaired source passed the mod suite 107/107, the canonical repository suites
  614/614, and a zero-warning Release mod build. Paired release
  `m7-c10a-20260802-r2` was then built from
  `ab4586b4c3a86e28d1aea977c7f8f34f00fe2f23`, deployed hash-exactly, and exercised
  on both physical clients. OMEN completed all 49 actions; i5 failed
  `i5-c8-zone-resume` after applying chunk sequence 2842 once but receiving no replay.
- Gateway evidence resolved that failure to the gate itself: a later processed frame sent
  cumulative ACK 2843 before the intentional socket abort took effect, removing the held
  chunk from Gateway's replay buffer. r3 installs the hold on the receive worker and defers
  every cumulative ACK above it until sequence 2842 is replayed. The same window identified
  exact P2 instance calls `RPC_HealthChanged(Single)` and `RPC_UpdateMaterial(Int32)`; both
  are now shared-contract admissions. The broader component families remain open.
- The r3 source passed 112/112 focused mod tests, 615/615 containerized repository tests
  (including exact P2 Gateway forwarding), and a zero-warning Release mod build. Paired
  release `m7-c10a-20260802-r3` was cut from
  `025b3b02504714a1a112058e316594c69c82208f` and deployed hash-exactly to AM4,
  OMEN, and i5. OMEN completed all 49 actions. i5 now proved the ACK barrier itself:
  sequence 2249 was applied, deliberately left unacknowledged, replayed once, and released
  cumulatively through 2262. It still failed before semantic completion because its
  outbound WebSocket queue stopped draining.
- The r3 trace proves a receive-only half-open session rather than another replay loss.
  Gateway received no further i5 message after `2026-08-02T14:38:34.0007213Z`, while i5
  continued receiving reliable sequences 2477, 2483, and 2495; their handlers reported
  `ack_queued=false`, and the shutdown disconnect queue was full. The client sender task
  had no timeout, fault supervision, or causal event, so the OS-level stall-vs-fault detail
  is not recoverable from r3. The retained falsifier is
  `fieldlab/evidence/c10a-r3-outbound-stall/`.
- The r4 source wraps every WebSocket send in a five-second guard. Completion leaves the
  socket alone; a fault or stall records type, sequence, and queue depth, aborts the socket,
  and forces the existing reliable-resume path instead of leaving a half-open receive loop.
  Five deterministic tests cover completion, synchronous/asynchronous failure, an operation
  that ignores cancellation, and caller cancellation. The focused suite passes 117/117 and
  the canonical .NET 9 repository suites pass 615/615; the Release mod build remains
  zero-warning/zero-error.
- Paired release `m7-c10a-20260802-r4` was cut from
  `53260467e56ba0497a103d347ad2c463f83c7728`. The frozen mod SHA-256 is
  `e2e3ba8d1342ae29264adee7942e0535f23685aaf96bad8af86e80ea5f083e78`; the admitted
  Gateway image is
  `sha256:b118e335325e53e0eb3d74f2bf40cd984ba7fd3d5377259480057c76083b6d3e`.
  Both were deployed hash-exactly to the local AM4/OMEN/i5 lane with r3 rollback
  artifacts retained. P7 was not contacted or changed.
- Physical run `native-20260802-c10a-r4` passed the complete 49-action reducer. OMEN
  and i5 each completed one forced WebSocket resume and one bounded fresh-process
  rejoin; the formerly failing i5 zone-resume action completed. Client and server
  native totals and poison trips were zero, Gateway restart replay retained 5,634
  durable objects, ownership contention rejected the second logical peer, and the AM4
  save fingerprint remained exact.
- Cleanup passed: both Valheim clients stopped, all runtime controls disarmed, the
  run-scoped residue sweep found `matched=0 destroyed=0`, and AM4/Gateway remained on
  the exact r4 artifacts. The compact retained receipt is
  `fieldlab/evidence/c10a-r4-physical-acceptance/acceptance-summary.json`.

This closes the **P1 source admission, contract parity, paired-release alignment, and
selected physical runtime candidate** portions of C10a. The first three paired releases
remain retained falsifiers; r4 is the first accepted AM4 candidate. It does not close
the remaining breadth: four `[VERIFY]` items, separate vehicle and mount gates,
container/station and AI/creature runtime gates, P7 promotion, fallback deletion, and
the final post-deletion release remain open.

**Candidate promotion**

- Cut one mod/Gateway release and manifest from the same commit.
- Retain the currently deployed P7 artifact as the rollback unit. Do not create a backup
  of test data and do not change VM power state.
- With explicit promotion authority, deploy the candidate, restart only the Valheim
  container when the DLL requires it, and allow the normal world reload.
- Run C8's non-destructive pair scenario against P7 with native poison armed.

**Finalization**

- Delete migration-only native fallback branches and flags. Keep the native-use telemetry
  as a regression guard.
- Cut and promote the final artifact, then repeat the cold join, pickup, motion, zone, and
  reconnect boundaries.
- Update the landscape so every remote path is `Swapped`, link the retained evidence, and
  close or supersede the old pinned networking notes rather than restating their history.

**Failure mode**

- A P7 failure rolls back the complete artifact pair. It does not selectively re-enable a
  native path inside the candidate.

**Exit**

- P7 is running the final no-fallback release; both clients pass; native use is zero; the
  roadmap and landscape identify the release and evidence without credentials or platform
  identifiers.

**Cost:** 1–3 focused days plus two bounded P7 world reloads.

## Dependency and replan points

```text
C0 ledger/driver
  └─ C1 durable session
       ├─ C2 RPC/control
       └─ C3 ZDO journal/apply
            └─ C4 ownership/action
                 └─ C5 world/zone
                      └─ C6 motion authority
                           └─ C7 Steam-free cold join
                                └─ C8 native-zero composition
                                     └─ C9 tuning/feel
                                          └─ C10 P7 final
```

Reassess cost and architecture after C1, C3, C5, and C7. These are the only planned
replan points. Do not start a later slice merely because its code can be written in
parallel; its proof would be confounded by an earlier native dependency.

Kill criteria:

- If C1 cannot resume without duplicate reliable delivery, do not put gameplay state on
  it.
- If C3 cannot reject malformed/stale state before mutation, do not move ownership.
- If C5 cannot resume a chunked snapshot deterministically, do not remove native join.
- If C7 requires opaque vanilla packet tunneling, stop and decide whether that is an
  explicitly temporary scaffold or an unacceptable architectural endpoint.
- If native poison cannot distinguish local adapter dispatch from native network input,
  it cannot certify completion and must be fixed before C8.

## Evidence contract

Every retained slice directory contains:

- commit, mod DLL hash, Gateway image identity, config digest, run id, and world epoch;
- both client lifecycle receipts and renderer/GPU identification;
- server and Gateway boundary counters before/after;
- native-use ledger deltas;
- exact injected failure and its observed outcome;
- machine summary with `verified`, `inferred`, and `unverified` fields;
- a short `README.md` stating only what that boundary proved.

Unit tests and synthetic protocol checks may support development, but they are never the
acceptance artifact. A green slice requires the real dedicated server, real Gateway, and
at least one native Valheim client; composition slices require both clients.

## Estimated remaining cost

After r4's physical acceptance, the honest remaining local burn-down is **eight named
gates**, not one blended day estimate: four explicit verification items plus separate
vehicle, mount, container/station, and AI/creature runtime gates. Some may close from one
artifact, but none receives credit without its own source classification and physical
receipt. C10b then has two bounded P7 reloads: paired-candidate promotion/reproof, followed
by fallback deletion and the final post-deletion artifact/reproof. C9 still needs Derek's
one-word retained-clip verdict.

| Remaining unit | Count |
| --- | ---: |
| `RPC_SetConnection`, `RPC_TeleportPlayer`, `UseStamina`, vehicle-control `[VERIFY]` | 4 |
| Vehicle, mount, container/station, AI/creature physical family gates | 4 |
| P7 candidate promotion and final no-fallback promotion | 2 reloads |
| C9 subjective retained-clip verdict | 1 operator verdict |

C8, Wall 11, C9's machine/artifact run, P1 admission, release alignment, and r4's
49-action physical reducer are retained boundaries and are not rerun by default. The
intended rhythm is: close one named boundary, run it for real, retain the receipt, update
the landscape, then commit and replan.

## Immediate next build

C0-C8 are complete; C9's machine/artifact run is retained with only the one-word
subjective verdict open. C10a's 33 P1 contracts, release alignment, and poison-armed r4
physical candidate are also retained. The immediate next build is the smallest independent
remaining item: close **`RPC_SetConnection [VERIFY]`** using the existing portal/linking
fixture, its exact extracted signature and ownership classification, shared contract tests,
and one native-poisoned physical receipt. It requires no vehicle world seeding. Then close
`RPC_TeleportPlayer`, `UseStamina`, the vehicle-control verification item, and the four
family gates in that order unless a poison trip supplies contradictory priority evidence.
Do not rebuild the epoch, Gateway replay, C8 composition, or rendered-motion boundaries
without new contradictory evidence. Workbench/dashboard implementation remains frozen
until the C10a local breadth gates are complete. C10b then promotes the accepted pair to
P7, re-proves the named boundaries, removes migration-only fallback, cuts the final paired
artifact, and closes the cutover.
