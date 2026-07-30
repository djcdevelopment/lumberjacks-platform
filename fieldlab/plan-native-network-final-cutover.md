# Plan — native Valheim networking final cutover

**Written:** 2026-07-30

**Starting point:** `de4243e` and
[`NATIVE-NETWORK-LANDSCAPE-2026-07-30.md`](NATIVE-NETWORK-LANDSCAPE-2026-07-30.md)

**Development topology:** AM4 dedicated server; native Windows clients on OMEN and i5

**Reference deployment:** P7 stays running and is promoted only after the AM4 gates pass

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
| C2b | **Next** | Carry the complete routed-RPC shape through C1 and prove client-to-server, server-to-client, broadcast, target-ZDO, and one idempotent real interaction without native fallback for the selected hashes. |
| C3-C10 | Pending in dependency order | Do not skip the remaining mandatory replans after C3, C5, and C7. |

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

**Revised next build**

1. **C2a — direct control pulse:** expose a fixed typed-handler registry over C1, dispatch
   only from bounded Unity `Update`, replace one server pulse, and suppress that native
   message class after the reliable enqueue succeeds. Withhold the Lumberjacks pulse and
   require a stale marker rather than a native copy.
2. **C2b — routed RPC:** carry the complete routed shape over the same registry, then prove
   client-to-server, server-to-client, broadcast, and target-ZDO dispatch plus one
   idempotent real interaction. The selected native hash has no fallback.
3. Continue to C3 only after native routed/control counters for the selected classes stay
   zero. C3's mandatory replan remains in force.

The revised remaining estimate is **20–42 focused engineering days**. C2 remains a
2–4-day slice but now has two ordered internal gates; C3-C7 still dominate the range.

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

### C4 — Ownership lease and action boundary

**Build**

- Replace `ReleaseNearbyZDOS` ownership transfer with a server-originated Lumberjacks
  lease carrying object id, owner connection id, lease epoch, expiry, and reason.
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

**Cost:** 2–4 focused days.

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

**Cost:** 1–3 focused days.

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
- Produce one side-by-side clip rather than asking Derek to drive two clients. If a
  subjective verdict is desired, Derek reviews that retained clip once and answers
  `smooth`, `rough`, or `mixed`; this is not a live KVM gate.

**Exit**

- Movement correctness is verified by machine evidence and visible presentation has a
  retained, reviewable artifact. Any subjective statement is labeled with its actual
  reviewer.

**Cost:** 1–3 focused days.

### C10 — P7 promotion, fallback deletion, and final close

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

The current evidence supports **22–46 focused engineering days**, dominated by typed ZDO
state, world/zone bootstrap, and the logical-peer cold join. Existing session, motion,
recipient, client-harness, and runtime-control work keeps this below a greenfield rewrite.

| Cost class | Slices |
| --- | --- |
| Lower, existing substrate | C0, C1, C8, C9, C10 |
| Medium, mapped interception seams | C2, C4, C6 |
| High, state-machine boundaries | C3, C5, C7 |

This is a burn-down estimate, not a promise to execute all slices without reassessment.
The intended rhythm is: build one boundary, run it for real, retain the receipt, update
the landscape, then commit and replan.

## Immediate next build

C0, C1, and C2a are complete. Start **C2b's routed shapes** next. Do not begin C3
until client-to-server, server-to-client, broadcast, target-ZDO, and one idempotent
real interaction suppress their selected native delivery in a real-client failure
cell.
