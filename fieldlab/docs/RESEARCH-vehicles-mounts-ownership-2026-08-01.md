# Research — vehicles and mounts on the wire (pre-C10a)

**Written:** 2026-08-01 · **Status:** research only. No code proposed, nothing
implemented, no slice started. C10a remains queued behind C9.

Machine-readable companion, carrying every correction, hazard and open question:
[`RESEARCH-vehicles-mounts-ownership-2026-08-01.json`](RESEARCH-vehicles-mounts-ownership-2026-08-01.json).

## Post-research physical closure — 2026-08-02

This file remains the pre-C10a research record; the implementation status has moved.
Pinned source confirms that `BaseAI.UpdateAI` returns false for a non-owner and that the
concrete `MonsterAI` and `AnimalAI` update paths delegate to that gate. Exact paired r36
physically accepted the selected `MonsterAI` boundary on an actual tamed Lox across OMEN
ownership, transfer to i5, disconnect loss, and AM4 reclaim. Owners executed 160–161 real
AI ticks, replicas executed zero owner ticks and 160 non-owner blocks, autonomous recovery
stayed under 0.035 s, native use stayed zero, and exact cleanup passed.

r35 remains the useful falsifier: a delayed durable player snapshot restored a released
rider parent edge and stalled the next owner. r36 repairs that stale edge whenever the
canonical rider token is empty; the physical run recorded one repair without weakening
the one-metre motion or two-second recovery bounds. Compact evidence is in
[`../evidence/c10a-creature-physical-acceptance/`](../evidence/c10a-creature-physical-acceptance/).
This closes the selected Lox/`MonsterAI` canary, not every creature species. Arbitrary
untagged vehicle/mount targeting and third-recipient AoI/relevance remain the next named
physical gate.

## What this is, and how much to trust it

Output of a multi-agent research pass: seven independent lenses over the
decompiled vanilla Valheim source and the Baseline mod, one synthesis, then three
adversarial critics (accuracy, completeness, pragmatics). It exists because the
C8 breadth audit names vehicles/mounts as the largest genuinely novel ownership
work left, and nobody had yet read the actual mechanisms end to end.

**Read the epistemic tags with care.** The body tags claims `[V]` verified, `[I]`
inferred, `[U]` unverified. Those tags are the *authoring agent's* confidence, not
an independent audit — and the adversarial review below falsified several claims
the body tags `[V]`. Treat a `[V]` as "a citation you can go check", not as
"already checked".

### Verified independently before landing

Checked from source by the session author:

- `ShipControlls` grants control by writing `ZDOVars.s_user` and never calls
  `SetOwner`; `Sadle` does both (`Sadle.cs:349`). The ships-vs-mounts split
  between *control* and *simulation authority* is real, and it is the note's
  central claim.
- Vanilla mutates ZDO ownership from client-side RPC handlers in at least five
  places: `Ship.cs:696`, `Sadle.cs:349`, `Vagon.cs:141`, `ArmorStand.cs:347`,
  `ItemStand.cs:385`.
- That falsifies the headline of
  [`NETCODE-OWNERSHIP-MAP.md`](../NETCODE-OWNERSHIP-MAP.md) — "ZDO ownership is
  100% server-authoritative and churns through a single funnel". The map's
  narrower supporting claim, that `ReleaseZDOS`/`ReleaseNearbyZDOS` runs
  server-only, still holds; the generalisation from that one funnel to *all*
  ownership change does not. A correction is recorded in that document.

Everything else below is unverified by the session author and should be checked
against the cited file and line before it carries weight in a plan.

## Corrections from adversarial review — read before the body

37 problems were raised against the synthesised note. The
16 blocking and serious ones are reproduced here, worst first; the
remaining 21 moderate and minor ones are in the JSON companion.
**Where a correction contradicts the body, the correction wins.**

### [BLOCKING] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

The note never opens the mod's server-side peer bookkeeping, and that is where C6 and vehicles actually collide. `LogicalPeerCutoverRunner.DrainMotion` (network/mod/ComfyNetworkSense/Core/Services/LogicalPeerCutoverRunner.cs:109-150) writes `matched.Peer.m_refPos` directly from the C6 motion snapshot XYZ (:131-134). In vanilla that field is only ever set from `RPC_ServerSyncedPlayerData` (DECOMP/ZNet.cs:1386-1393), a direct ZRpc that native-zero suppresses. `peer.m_refPos` is the input to `ZDOMan.ReleaseNearbyZDOS(peer.m_peer.m_refPos, peer.m_peer.m_uid)` (ZDOMan.cs:605), `ZDOPeer.ShouldSend` (ZDOMan.cs:40), the per-peer sync list (ZDOMan.cs:897) and `ZoneSystem.CreateGhostZones(peer.GetRefPos())` (ZoneSystem.cs:953). So the note's fork C ('how does a rider's transform travel') is not a rendering choice — it silently decides the server's entire area-of-interest for that peer.

**Correction:** Rewrite fork C with a hard constraint: every attached player must keep publishing a world-space position on the C6 frame, or `DrainMotion` must be taught to resolve the parent before writing `m_refPos`. Price the two options accordingly — C2 ('let attached players fall back to native ZSyncTransform') stops refPos updates for an entire voyage, so the server stops sending ZDOs and starts releasing ownership around the boarding point while the boat sails into unloaded ocean; C1 ('parent-relative pose in the frame') writes relative coordinates into `m_refPos`, teleporting the peer's AoI to near-origin. Neither failure produces an error.

### [BLOCKING] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

§6 leaves 'whether a ridden player is in `_remote`' as an unresolved 10-minute read that gates escalating H3. Source settles it, and it settles blocking. `DrainReceived` (LumberjacksMotionRunner.cs:644-693) keys `_remote` purely on `ZdoUserId + ":" + ZdoId` from any received snapshot that has a descriptor (:680); `HasRemote` (:1419-1423) adds only `HasAppliedPosition`. Attach state is nowhere in the predicate. Every tracked remote player — seated in a ship chair, saddled, or standing on a deck — is in `_remote`, so `MotionAuthorityTransformPatch` suppresses their `CustomFixedUpdate` and with it the whole `ClientSync`→`SyncPosition` parent-resolution path.

**Correction:** Promote H3 from conditional to live (gated only on the `m_characterParentSync` prefab flag) and drop the §6 open item. Add the asymmetry the note misses: `OwnerSync` runs from `CustomLateUpdate` (DECOMP/ZSyncTransform.cs:480-486), which the mod does not patch, so the write side survives. The local player keeps publishing `SetConnection(SyncTransform, parent)` plus `s_relPosHash`/`s_relRotHash`/`s_attachJointHash` into its own ZDO while no peer consumes them — the data is already on the wire today, unread.

### [BLOCKING] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

E2 is ranked the highest-leverage cheap step but is priced as an asset dump or a new probe. It is already answerable offline from receipts the programme is currently writing. `ZdoJournalCutoverRunner` serializes the entire vanilla ZDO body — `zdo.Serialize(body)` at :607, `body_b64 = body.GetBase64()` at :625 — and `ZDO.Serialize` (DECOMP/ZDO.cs:764-778) sets bit 1 and emits the connection whenever `ZDOExtraData.GetConnection(m_uid)` is non-None.

**Correction:** Re-scope E2 to a base64 decode over existing `zdo-*.jsonl` receipts: if `m_characterParentSync` is set on the Player prefab, any envelope for a player who sat in a chair or rode a Lox already carries the connection bit plus the three attach fields. Zero live tests, zero new instrumentation. Also correct §1.6 — 'the runtime journal handles only ConnectionType.Portal' is true of explicit handling (:681) but misleading about the wire: the SyncTransform connection rides the journal opaquely inside `body_b64`.

### [BLOCKING] · Harness feasibility and experimental discrimination: can the two-client AM4 manifest-driven composition actually run E1-E5, and would the results discriminate anything?

E1, E3 (cart leg) and E4 all presuppose a boat, cart, or tamed mount exists in the AM4 world, and the lab has no way to create one. `spawn` is registered `isCheat: true` (Terminal.cs:1206 + the trailing flags line, which carries the `ZNetScene.instance.GetPrefabNames()` options fetcher), as is `tame` (Terminal.cs:1480-1483). The gate is absolute: `IsCheatsEnabled()` returns `ZNet.instance.IsServer()` (Terminal.cs:2232-2243) and `IsValid` short-circuits on `!IsCheat || context.IsCheatsEnabled()` (Terminal.cs:236). On a client attached to a dedicated server `IsServer()` is false, so no cheat command runs regardless of admin status — the repo already knows this and worked around it for exactly one command (ComfyNetworkSense.cs:999-1001: "the vanilla 'god'/'fly' console commands are cheat-gated on a dedicated-server client"). `-console` IS passed at launch (Invoke-NativeValheimClient.ps1:647), which makes this failure look surprising in the moment. E1's parenthetical "(spawn it on A)" is therefore not a step, it is the unbudgeted prerequisite. Hand-building is not free either: a Karve needs 80 bronze nails, a Cart needs 10, a tamed Lox needs Plains plus taming time. Evidence that something saddleable is already near the test area exists but is weak and probably wild — `Setting saddle:False` (Tameable.cs:386) appears in omen bepinex.log across full32/full34/full43/c8twoclient16/portalgate14, one line each; no Karve/VikingShip/Cart/Raft string appears anywhere under fieldlab/runs or fieldlab/evidence.

**Correction:** Make world seeding an explicit, first, separately-scheduled item: stand up a local non-dedicated host on the ComfyEra16 save (where `IsCheatsEnabled()` is true), `spawn Karve`/`spawn Cart`/`spawn Lox`+`tame` at fixed coordinates near the existing test origin (2211,80,-69) and the Plains target (3250,80,2250), then redeploy the .db/.fwl to AM4 and re-baseline `Get-Am4SaveFingerprint` (Invoke-NativeValheimCutoverScenario.ps1:217-276 — `portals`/`spawned`/`targets`/`locations` are compared exact, so the before-baseline must be recaptured after seeding). Separately: run the whole vanilla-oracle half of section 6 (does `Ship.m_players` populate for a remote, what does `s_user` actually contain, does the attach channel emit, does a cart pull churn ownership) on that same local-host + one-client pair. It is cheat-enabled, needs no manifest, no Gateway, no poison, and no AM4 time.

### [BLOCKING] · Harness feasibility and experimental discrimination: can the two-client AM4 manifest-driven composition actually run E1-E5, and would the results discriminate anything?

E4 is not runnable on this harness at any cost, because there is no verb that can put a player on a vehicle and no verb that can move a player in a way that respects one. The action-kind set is closed and re-validated in-process — anything else returns `manifest_action_kind_invalid` (NativeCutoverScenarioController.cs:202-278). The only two interaction verbs are `pickup_nearest`, hard-typed to `AccessTools.TypeByName("Pickable")` and rejecting everything else with `pickable_type_unavailable`/`pickable_not_found` (:645-690), and `ownership_target`, which calls `ZNetView.ClaimOwnership()` on the nearest GameObject whose *name* contains a tag that must start with `cutover-<runid>` (:883-911, SafeTargetTag at :1123-1126) — no world object is ever named that, and the accepted composition does not use it. There is no board, use, helm, hitch, mount, sit, or steer verb. Worse, the two movement verbs are transform teleports, not locomotion: `move` does `((Component)Player.m_localPlayer).transform.position = target` with `target.y = _origin.y` (:337-343), and `motion_drive` does the identical thing (LumberjacksMotionRunner.cs:906-918). `_origin`/`probe.Origin` are captured in absolute world space at action start. So a "passenger on the deck" driven by `move` is pinned to a fixed world line and a fixed Y while the hull translates and pitches underneath — the harness's own writes would dominate the exact quantity E4 wants to measure (observer-side rider-to-deck offset), and would also fight `Character`'s deck-stick `m_body.position` write. The note's claim that E4 "needs no new scenario kind... everything else is existing machinery" is false in both halves.

**Correction:** Drop E4 from the cheap tier. If the rider-separation question survives E2 (i.e. `m_characterParentSync` is true on the Player prefab), it requires new mod code: at minimum a `vehicle_board`/`vehicle_helm` action kind driving `ShipControlls.Interact`, plus a passenger verb that does NOT write `transform.position` (or that writes parent-relative), plus a new observer-side measurement that samples rider world position against the parent ZDO's position per frame. That is a slice of work, not an experiment. In the interim the same question is answerable for free on the local-host + one-client pair with no Lumberjacks at all: it establishes the vanilla baseline offset, which you need anyway before an A/B means anything.

### [SERIOUS] · Adversarial verification of the note's technical claims against the actual source — spot-checking every file:line citation in the decompiled Valheim tree, the ComfyNetworkSense mod, the Game.Gateway lease service, the C8 audit, the cutover plan, synthetic_baseline_v2.json, and the C8 acceptance scenario.

§1.6 and H5 state that for the C4 ownership-lease guard "the only Harmony seam is on `ItemDrop.RequestOwn` (`:218-227`)". This is false and it is tagged [V]. `OwnershipLeaseCutoverRunner.cs:1137-1144` installs a universal prefix on `ZDO.SetOwner(long)`:

```
[HarmonyPatch(typeof(ZDO))]
static class OwnershipLeaseOwnerPatches {
  [HarmonyPatch("SetOwner", new[] { typeof(long) })]
  static bool SetOwnerPrefix(ZDO __instance, long uid) =>
      !OwnershipLeaseCutoverRunner.ShouldBlockRelease(__instance, uid);
}
```

There are four seams, not one: ZDO.SetOwner (:1137-1144), ZDOMan.DestroyZDO (:1146-1151), ItemDrop.RequestOwn (:1153-1159), plus the ReleaseNearbyZDOS scope prefix/finalizer pair (:1125-1134). The note's grep of `Patches/` missed them because they are nested classes inside the runner file.

**Correction:** H5's conclusion survives — the three vehicle SetOwner calls are not blocked — but the reason is the predicate, not a missing hook. `ShouldBlockRelease` (:205-216) requires `ReleaseScopeDepth > 0`, and `ReleaseScopeDepth` is incremented in exactly one place: the prefix on `ZDOMan.ReleaseNearbyZDOS` (:1125-1127). So the guard is scoped exclusively to the AoI sweep, and Ship.cs:696 / Sadle.cs:349 / Vagon.cs:141 pass through because they fire outside that scope, not because they are unhookable. This cuts in the note's favour and should be corrected before the note is planned against: the universal interception point vehicles need already exists and is already proven. C10a needs a wider predicate (scope condition + target selection), not a new seam. That materially reduces the cost of fork A3 as sized in §4.A.

### [SERIOUS] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

An entire family is absent, and vanilla itself classifies it alongside ships: fishing. `ZDOMan.ConvertOwnerships` (DECOMP/ZDOMan.cs:1305-1321) converts `s_zdoidUser`→`s_user` and `s_zdoidRodOwner`→`s_rodOwner` in one loop, logged as 'Converting Ships & Fishing-rods ownership' (:365). `FishingFloat` writes `ZDOVars.s_rodOwner` (FishingFloat.cs:87) — a fifth identity space beyond the three fork D enumerates — and resolves it via `ZNet.instance.GetPlayerList()` matched on `m_characterID.UserID` then `ZNetScene.FindInstance` (:100-120), the same peer-list dependency that breaks `Sadle.CalculateHaveValidUser`. `Fish` is pulled continuously toward the float by the fish ZDO's owner (`Utils.Pull`, Fish.cs:403-410) — a cross-ZDO continuous physics coupling with exactly the vehicle shape — and registers `RequestPickup`/`Pickup` with a targeted unicast `InvokeRPC(uid, "Pickup")` (:222-223, :303).

**Correction:** Add fishing as a family-1 member and note that its failure mode is not the note's 'silent no-op': `FishingFloat.FixedUpdate` calls `m_nview.Destroy()` when the owner cannot be resolved (:136-145). The same missing-peer-list root cause is silent for mounts and destructive for fishing floats — that difference should drive test ordering.

### [SERIOUS] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

§4H asks 'what is actually in family 1' and leaves it open. It is closable from source, and the answer is larger than 16 RPCs. `Catapult` carries a `Vagon` — verified, not inferred: `m_wagon = GetComponent<Vagon>()` (Catapult.cs:151). `SiegeMachine` declares `public Vagon m_wagon` (SiegeMachine.cs ~:35). `Catapult` additionally registers three of its own instance RPCs — `RPC_Shoot`, `RPC_OnLegUse(Boolean)`, `RPC_SetLoadedVisual(String)` (Catapult.cs:187-189, all three present in synthetic_baseline_v2.json under `Catapult.Start`) — and invokes `RPC_RequestOwn` on the Vagon's own ZDO for two non-vehicular reasons: leg locking (:285) and ammo loading (:341).

**Correction:** State family 1 as {Ship, ShipControlls, Sadle, Vagon, Catapult, SiegeMachine, ShipConstructor, ShipEffects, Leviathan} and the method count as 19, not 16. Add the concrete hazard the enumeration exposes: `Vagon.RPC_RequestOwn`'s `InUse()` veto will refuse a catapult's ownership claim while it is hitched, and `RPC_OnLegUse` persists `s_locked` only `if (m_nview.IsOwner())` (:293-296) — so the state lands or silently does not, depending on a race the caller never checks. The cart lease has three consumers, not one.

### [SERIOUS] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

The attach channel is broader than riders and pilots, and its most important property is unstated: `AttachStart` is never replicated. `Player.AttachStart` (DECOMP/Player.cs:5948-5960) is called only locally — `Chair.Interact` (Chair.cs:68), `Bed` (Bed.cs:95), `Barber` (Barber.cs:74), `Sadle.RPC_RequestRespons` (Sadle.cs:401), `ShipControlls.RPC_RequestRespons` (ShipControlls.cs:128), `Valkyrie`. The only replicated traces are the ZSyncTransform connection and `s_inBed` (Player.cs:6049/6079). Chairs are the ship passenger-seat mechanism — `Chair.m_inShip` (Chair.cs:17) feeds `Player.m_attachedToShip` — and seat mutual exclusion is `Player.GetClosestPlayer(m_attachPoint.position, 0.1f)` (Chair.cs:56), a 10 cm scan over locally-instantiated Players, i.e. a predicate now reading a Lumberjacks-authored transform.

**Correction:** Split §2.4(c) into two channels: the transform channel (ZSyncTransform connection, which the note covers) and the *occupancy/arbitration* channel (`AttachStart` + `GetClosestPlayer`, which is unreplicated, has no RPC at all, and is therefore invisible to the RPC-shaped audit for the same reason). Note that `Chair` registers zero RPCs, so it appears in neither the 160-row table nor any C10a backlog. Also connect `SleepStart`/`SleepStop` — already in the audit's P1 set — to the same `AttachStart` mechanism; nobody has.

### [SERIOUS] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

Tamed followers are treated as a footnote to mounts, but the mechanism is different in kind and introduces a fourth identity space. `Tameable.Command` sends `InvokeRPC("Command", user.GetZDOID(), message)` (Tameable.cs:443); `RPC_Command` resolves the commander with `ZNetScene.instance.FindInstance(characterID)` on the *creature owner's* machine and returns silently if not found (:446-462) — the same silent-no-op signature as H1, on a path H1 does not cover. The persisted follow target is `ZDOVars.s_follow` = the player **display name** (:482), re-resolved by string match over `Player.GetAllPlayers()` (:503-515). `AddSaddle` is owner-unicast that re-broadcasts `SetSaddle` to `ZNetView.Everybody` (:328-334); `RPC_UnSummon` is broadcast (:711).

**Correction:** Add a followers row: control depends on (a) the ZDO owner having the commander's Player GameObject instantiated — under Lumberjacks that is the logical-remote ZDO path, untested for this consumer — and (b) an unauthenticated, non-unique display-name identity. Extend fork D from three id spaces to five (profile UID, ZDOID.UserID, logical-peer id, `s_follow` player name, `s_rodOwner`). Add the two broadcast paths to the ADR-0013 multicast dependency list alongside `RequestRespons`.

### [SERIOUS] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

§2.3's 'simulation-locus, not mutation-authority' framing is correct but scoped to vehicles, and that mis-sizes fork A. `BaseAI.UpdateAI` returns early for non-owners (DECOMP/BaseAI.cs:303-309), so every creature in the world already has the 'whose machine simulates this non-player entity for the next N minutes' problem. The audit's own family 3 (AI/creatures) reduces it to 'server-to-client ownership handoff must not stall AI'.

**Correction:** Restate fork A as a world-wide simulation-locus decision that vehicles merely force first. As written, A2 ('server simulates, clients send input') would produce a world where boats are server-simulated and every wolf, Lox and troll is client-simulated — an architectural inconsistency the fork table does not price, and one that also reopens H2's 'stand up Unity physics on the dedicated server' cost for the creature population.

### [SERIOUS] · Adversarial gap-check: what vehicle/mount surface the note did not open, which accepted-slice interactions it did not consider, and which families beyond ships/carts/mounts belong in C10a. All claims re-derived from source this session; where the note was right I say so rather than manufacturing an objection.

H5 tags `fieldlab/NETCODE-OWNERSHIP-MAP.md` as [U-lens] 'does not include them'. The map does not merely omit the sites — its thesis asserts the opposite. Line 13: 'ZDO ownership is 100% server-authoritative and churns through a single funnel.' Verified client-side `SetOwner` counterexamples: Ship.cs:696, Sadle.cs:349, Vagon.cs:141, ArmorStand.cs:347, ItemStand.cs:385, plus Catapult's two `RPC_RequestOwn` invokes (Catapult.cs:285, :341).

**Correction:** Upgrade H5 from 'the enumeration is incomplete' to 'the map's headline claim is falsified', and flag the downstream artifact: `OwnershipPinRunner` was designed against that single-funnel thesis (map :113-121), so its auto-capture selector has never been evaluated against a funnel it cannot see.

### [SERIOUS] · Harness feasibility and experimental discrimination: can the two-client AM4 manifest-driven composition actually run E1-E5, and would the results discriminate anything?

Even given a boat and a boarding verb, E4's stated A/B ("C6 authority ON and OFF") cannot be run inside the native-zero composition. The cutover flags are per-run request fields, not runtime toggles (NativeAutotestRequest.cs:30-38, set once at load from the file the launcher writes), so ON/OFF is two full runs rather than an A/B — that alone doubles the cost. More decisively, the mod refuses the combination: `SteamFreeColdJoin` with `!MotionAuthorityCutover` is rejected as `steam_free_request_cutover_prerequisites_invalid` (NativeAutotestRequest.cs:141-149). So the OFF cell cannot be a Steam-free cold-join run. Whatever composition you use for OFF differs from the ON cell in join path, world-descriptor handling, and peer bootstrap, which is precisely the kind of cross-composition difference that has produced calibration misses in this lane before.

**Correction:** State the OFF cell's composition explicitly and accept that it is a vanilla-transport baseline, not a matched control. Or make the ON/OFF distinction narrower and legal — e.g. an authority-scope flag that excludes ZDOs with a resolved `SyncTransform` connection — so both cells stay inside the same accepted native-zero composition. That is a design decision for the slice, not something to discover at run time.

### [SERIOUS] · Harness feasibility and experimental discrimination: can the two-client AM4 manifest-driven composition actually run E1-E5, and would the results discriminate anything?

E1 names an instrument that cannot carry the signal. It predicts "`LogicalPeerCutoverRunner._suppressedInvokes` climbs (sampled log lines at counts 1-4 and powers of two)". But `TryHandleInvoke` increments that single counter for *every* suppressed `ZRpc.Invoke` on a logical peer (LogicalPeerCutoverRunner.cs:339), and `ZDOMan.SendZDOToPeers2` invokes `ZDOData` on a 0.05s timer (ZDOMan.cs:565-571, 787) — 20 Hz, continuously, plus NetTime and the rest. Over a 30-minute manual session that is roughly 36,000 increments; the power-of-two sampler fires at 16384 and then not again until 32768, i.e. once in a fourteen-minute stretch. The method name is only emitted on those samples (:367-371). You would watch a number go up and be unable to attribute a single increment to the moment someone pressed Use on a boat. The note also undersells its own strongest instrument: the run-level discriminator is the *server-side* poison ledger, because inbound `RPC_RoutedRPC` calls `NativeNetworkLedger.Observe("routed_rpc_receive", "inbound", "RoutedRPC")` (NativeNetworkPatches.cs:204-206). Either the vehicle RPC never leaves the client (no server row, silent swallow, H1 confirmed) or it lands on the server and trips poison (H1 refuted, run aborts loudly). That fork is clean, already collected as `native-network-use.jsonl`, and needs no sampling at all.

**Correction:** Rewrite E1's success criteria around the server-side `native-network-use.jsonl` trip/no-trip fork. If per-method attribution on the client is wanted (and it is, to prove *which* method was swallowed), add a small always-log carve-out in `TryHandleInvoke` for methods outside a known-noisy set (`ZDOData`, `NetTime`, `PlayerPos`), or a per-method counter dictionary — that is a two-line change but it is a change, so E1 stops being "no code".

### [SERIOUS] · Harness feasibility and experimental discrimination: can the two-client AM4 manifest-driven composition actually run E1-E5, and would the results discriminate anything?

E4's stated motion windows exceed what the mod will accept, and the composition is close to its action ceiling. The generator advertises `-MotionDurationSeconds` 4..120 and `-MotionDistanceMeters` 2..200 (New-NativeValheimCutoverScenario.ps1:30-37), but the in-process validator rejects any motion probe with `duration_seconds` outside 2..20 or `distance_meters` above 24 (NativeCutoverScenarioController.cs:263-270) — the manifest is refused wholesale with `manifest_motion_probe_invalid`, not clamped. A "straight run and a turn" on a longship is minutes and hundreds of metres; you get 20 seconds and 24 metres. Separately, the manifest cap is `MaxActions = 64` checked before the per-client filter (:27, :184-187), and the accepted C8 composition already uses 49 actions across both clients. Fifteen slots remain, shared, and a two-client board/helm/observe/settle cell burns them fast.

**Correction:** Note the generator/validator range mismatch as a standing bug worth fixing regardless (the generator should not be able to emit a manifest the mod always rejects). For vehicle work, plan a separate profile rather than extending `c8` — and size the action budget before designing the choreography, not after.

### [SERIOUS] · Harness feasibility and experimental discrimination: can the two-client AM4 manifest-driven composition actually run E1-E5, and would the results discriminate anything?

E3 is neither free nor unambiguous. The finding that `TelemetryCoordinator.RecordOwnershipChurn` exists (TelemetryCoordinator.cs:439-444) with zero callers is correct and genuinely useful — but the class exposes no static accessor a Harmony patch can reach (the only `Instance` reference in the file is `ComfyNetworkSense.Instance.Config.Reload()` at :329), so the postfix needs a static hook wired the way the other runners do it. And `ownership-churn.jsonl` is not in the orchestrator's evidence pull: the collected set for the accepted run (fieldlab/runs/native-valheim/native-20260731-c8-full44/omen/) is 18 files and does not include it, so the orchestrator needs a collection change too. Beyond cost, there is a design ambiguity the note does not resolve: measured on the *cutover* composition, `ZDO.SetOwner` churn is Lumberjacks' behaviour (the C3 journal and world/zone cutover are already in the ownership path), not vanilla's — so it cannot answer "is cart churn folklore" about vanilla. And the cart leg is unscriptable anyway: hitching a cart needs `Vagon.Interact`, which `pickup_nearest` cannot reach.

**Correction:** Split E3 in two. The vanilla-baseline half belongs on the local-host + one-client pair with the mod in observe-only mode — cheap, cheat-enabled, and it answers the `ReleaseNearbyZDOS` hysteresis question the note actually cares about. The cutover half is a real instrumentation task: static hook, Harmony postfix on `ZDO.SetOwner` recording (prefab, old_owner, new_owner, callsite), plus an orchestrator collection entry. Say which side of the boundary each cell runs on.


---

## The synthesised research note

*Reproduced as produced, so the corrections above can be checked against what they correct. Section numbers are the body's own.*

# Research note — vehicles and mounts on the wire, ahead of C10a

**Status:** research only. No code proposed, nothing implemented.
**Date:** 2026-07-31. **Reader:** whoever plans the C10a vehicle slice.

Two source roots are cited throughout:

- `DECOMP` = `C:\Users\derek\AppData\Local\Temp\claude\C--work-baseline\fcd55b3e-63db-4d3b-a12d-2867230a5273\scratchpad\valheim-decomp` (550 files present this session; the brief said 603 — the delta is not material to anything below, but note the set may be partial).
- `MOD` = `C:\work\baseline\network\mod\ComfyNetworkSense`
- Gateway = `C:\work\baseline\Lumberjacks\src\Game.Gateway`

Every claim below is tagged **[V]** verified by me from source this session, **[I]** inferred from source but requiring a step the source does not take, or **[U]** unverified / open. Where a claim came from a prior lens and I did not re-open the file, it is tagged **[U-lens]** and should be re-checked before it carries weight in a plan.

---

## 1. What vehicles and mounts actually are, on the wire

### 1.1 The headline: three orthogonal axes, and vanilla never joins them up

A "vehicle" in Valheim is one ZNetView/ZDO carrying a Rigidbody. On it, three independent things are happening, and every design mistake available here comes from conflating them.

| Axis | Where it lives | What it confers | Who decides |
|---|---|---|---|
| **1. Control claim** | a ZDO data field `ZDOVars.s_user` (a `long`) | the right to feed input | whoever currently owns the ZDO, evaluated locally |
| **2. Simulation authority** | the ZDO **owner** field (a session/peer id) | the exclusive right *and duty* to integrate the Rigidbody and write the transform | ownership funnels (below) |
| **3. Physical attachment** | a Unity joint or attach-point, replicated as ZDO scalars/connections | the actual coupling of body to body | the owner, locally |

Axis 1 and axis 2 are **separable**, and vanilla separates them for ships and fuses them for mounts and carts. That single divergence is the largest classification error in the current audit (§2.4).

### 1.2 Ships — control without ownership

`ShipControlls` registers its RPCs on the **Ship's** ZNetView, not its own (`DECOMP/ShipControlls.cs:19-25`) **[V]**:

```
m_nview = m_ship.GetComponent<ZNetView>();
m_nview.Register<long>("RequestControl", RPC_RequestControl);
m_nview.Register<long>("ReleaseControl", RPC_ReleaseControl);
m_nview.Register<bool>("RequestRespons", RPC_RequestRespons);
```

The grant writes one ZDO field and **never calls `SetOwner`** (`ShipControlls.cs:93-107`) **[V]**:

```
if (m_nview.IsOwner() && m_ship.IsPlayerInBoat(playerID))
{
    if (GetUser() == playerID || !HaveValidUser())
    {
        m_nview.GetZDO().Set(ZDOVars.s_user, playerID);
        m_nview.InvokeRPC(sender, "RequestRespons", true);
```

I grepped every `SetOwner(`/`ClaimOwnership` site across the decomp: `ShipControlls.cs` has none **[V]**. The only ship ownership write in the game is `Ship.UpdateOwner` (below).

Movement is four RPCs on the ship ZDO, registered in `Ship.Start` alongside a 2-second ownership timer (`DECOMP/Ship.cs:184-191`) **[V]**:

```
m_nview.Register("Stop", RPC_Stop);
m_nview.Register("Forward", RPC_Forward);
m_nview.Register("Backward", RPC_Backward);
m_nview.Register<float>("Rudder", RPC_Rudder);
InvokeRepeating("UpdateOwner", 2f, 2f);
```

Input is rate-limited and locally predicted (`Ship.cs:201-225`) **[V]** — rudder is an **absolute** value sent at most every 0.2 s; throttle is **edge-triggered and relative** (`RPC_Forward` steps Stop→Slow→Half→Full, `Ship.cs:257-297`) **[V]**. None of `RPC_Rudder`/`RPC_Stop`/`RPC_Forward`/`RPC_Backward` checks `IsOwner()` or compares the sender to `s_user` **[V]**. They reach the owner only because `ZNetView.InvokeRPC(string, …)` addresses `m_zdo.GetOwner()` (`DECOMP/ZNetView.cs:326-329`) **[V]** — that is addressing, not authorization.

Physics is owner-gated (`Ship.cs:299-308`) **[V]**:

```
UpdateControlls(fixedDeltaTime); UpdateSail(...); UpdateRudder(...);
if ((bool)m_nview && !m_nview.IsOwner()) { return; }
```

Everything above the gate is presentation and runs on every peer; everything below (buoyancy probes, sail/steer/edge forces, damage) runs on exactly one machine.

**The entire replicated control state for a ship is three scalars** (`Ship.cs:567-580`) **[V]**:

```
m_nview.GetZDO().Set(ZDOVars.s_forward, (int)m_speed);
m_nview.GetZDO().Set(ZDOVars.s_rudder, m_rudderValue);
...
m_speed = (Speed)m_nview.GetZDO().GetInt(ZDOVars.s_forward);
if (Time.time - m_sendRudderTime > 1f) { m_rudderValue = m_nview.GetZDO().GetFloat(ZDOVars.s_rudder); }
```

`s_forward` (int enum), `s_rudder` (float), plus `s_user` (long). Note the **1-second client-side prediction hold** on line 576 — a non-owner helmsman keeps its own rudder for a full second after its last send. That is a pre-existing client-authority window.

Ownership moves by exactly one gameplay path (`Ship.cs:690-699`) **[V]**:

```
if (m_nview.IsValid() && m_nview.IsOwner() && !(Player.m_localPlayer == null)
    && m_players.Count > 0 && !IsPlayerInBoat(Player.m_localPlayer))
{ RefreshPlayerList(); long newOwnerID = GetNewOwnerID(); m_nview.GetZDO().SetOwner(newOwnerID); }
```

A **push**, on a 2 s `InvokeRepeating`, from the incumbent owner, only when the incumbent has stepped off a crewed boat. It requires `Player.m_localPlayer != null`, so a headless dedicated server never participates **[V]**. Boarding does not claim the boat; taking the helm does not claim the boat.

Consequence: **the steady state is helmsman ≠ physics owner.** Player A builds the boat, stays aboard, keeps ownership indefinitely; B takes the helm; B's rudder round-trips through A's machine at 5 Hz and A integrates the hull.

`m_players` — which gates the grant (`IsPlayerInBoat`), the handoff, speed decay (`Ship.cs:311-315`) and `CanBeRemoved` (`Ship.cs:179-182`) — is built purely from local Unity trigger callbacks (`Ship.cs:730+`) **[V]**. It exists in no ZDO. Every peer has its own answer.

**Trap:** `Ship.Rudder(float)` at `Ship.cs:237-240` calls `m_nview.Invoke("Rudder", rudder)`. `ZNetView` defines only `Register` overloads and two `InvokeRPC` overloads (`ZNetView.cs:278-329`) **[V]** — there is no `Invoke`. This binds to `MonoBehaviour.Invoke(methodName, delay)` and is unreferenced. Do not admit it as an entry point.

### 1.3 Mounts — control fused with ownership, on the creature's ZDO

`Sadle` has no ZDO of its own; it binds to the parent `Character`'s ZNetView (`DECOMP/Sadle.cs:62-73`) **[V]**, registering five RPCs including `Controls(Vector3, int, float)`.

The grant does **both** (`Sadle.cs:339-356`) **[V]**:

```
m_nview.GetZDO().Set(ZDOVars.s_user, playerID);
ResetControlls();
m_nview.InvokeRPC(sender, "RequestRespons", true);
m_nview.GetZDO().SetOwner(sender);
```

Note the ordering: the client is told it has control *before* the ownership bump is issued, and `s_user` rides DataRevision while the owner rides OwnerRevision — two independent replication channels with no atomicity **[V]**.

Because the rider becomes the owner, per-input-frame control costs **zero bytes**: `ZRoutedRpc.InvokeRoutedRPC` short-circuits when the target is self (`DECOMP/ZRoutedRpc.cs:130-137`) **[V]**:

```
if (targetPeerID == m_id || targetPeerID == 0L) { HandleRoutedRPC(routedRPCData); }
if (targetPeerID != m_id) { RouteRPC(routedRPCData); }
```

Riding then drives the ordinary AI locomotion API (`Sadle.cs:130-174`, `m_monsterAI.MoveTowards(...)`) **[V]**, and `MonsterAI` hands its whole tick to the saddle when ridden **[U-lens: MonsterAI.cs:356]**. Stamina/drown are owner-only and persist in the creature ZDO (`Sadle.cs:85-105`) **[V]**.

Rider validity is a **derived, never-replicated** distance scan (`Sadle.cs:363-379`) **[V]** over `ZNet.GetAllCharacterZDOS()`, which returns the local character plus each *ready peer's* reported `m_characterID` (`DECOMP/ZNet.cs:1847-1867`) **[V]**. On a client connected to a dedicated server, `m_peers` holds only the server (whose `m_characterID` is `None`), so **a bystander client computes `HaveValidUser == false` for a mount that is in fact ridden** — silently changing that client's mount run speed, slide angle and animator source (`Character.cs:1084`, `:1493`, `:3605`) **[V]**.

Dismount clears `s_user` but **does not return ownership** (`Sadle.cs:381-388`) **[V]**. Nothing clears `s_user` on death, disconnect or unload.

### 1.4 Carts — ownership *is* the control token, and the joint is local-only

`Vagon` registers exactly two parameterless RPCs (`DECOMP/Vagon.cs:95-96`) **[V]**. There is **no grant message**: the grant *is* the ownership change (`Vagon.cs:130-144`) **[V]**:

```
if (m_nview.IsOwner()) {
    if (InUse()) { m_nview.InvokeRPC(sender, "RPC_RequestDenied"); }
    else { m_nview.GetZDO().SetOwner(sender); }
}
```

Only the **denial** is a wire message. The requester learns it succeeded by observing `IsOwner()` become true and completing the attach in its own `FixedUpdate`. Intent (`m_useRequester`) is set unconditionally on interact (`Vagon.cs:116-128`) **[V]** and never expires.

The coupling is a `ConfigurableJoint` on the cart connected to the puller's Rigidbody (`Vagon.cs:252-285`) **[V]**, and every non-owner destroys it **every physics tick** (`Vagon.cs:186-189`) **[V]**:

```
else if (IsAttached()) { Detach(); }
```

Replicated attach state is one bool under `ZDOVars.s_attachJointHash`, written unconditionally by `AttachTo` (`:279`) but cleared **only if owner** by `Detach` (`:302-305`) **[V]**. `OnDestroy` does no cleanup at all (`Vagon.cs:101-104`) **[V]**, so a cart unloaded while attached persists `attachJoint = 1` into the save.

`Detach` also runs `m_body.WakeUp(); m_body.AddForce(0f, 1f, 0f);` **unguarded** (`:306-307`) **[V]** — a physics write into a cart this peer may not own.

### 1.5 Riders and passengers — a cross-ZDO parent edge with an intra-frame ordering guarantee

This is the mechanism the audit does not mention at all, and it is orthogonal to ownership.

A rider's world transform is **not** what other peers use. The owner writes, into the **rider's own ZDO**, a typed connection plus relative pose (`DECOMP/ZSyncTransform.cs:183-214`) **[V]**:

```
zDO.SetConnection(ZDOExtraData.ConnectionType.SyncTransform, m_tempParent);
zDO.Set(ZDOVars.s_attachJointHash, m_tempAttachJoint);
... zDO.Set(ZDOVars.s_relPosHash, ...); zDO.Set(ZDOVars.s_relRotHash, ...); zDO.Set(ZDOVars.s_velHash, m_tempRelativeVel);
```

Two shapes, chosen by whether the parent is a `Character` (`DECOMP/Player.cs:6702-6727`) **[V]**:

- **Mount** (parent has `Character`): `attachJoint = m_attachPoint.name`, `relativePos = zero`, `relativeRot = identity` — "I am exactly at the bone called X".
- **Ship helm / seat** (parent has no `Character`): `attachJoint = ""`, `relPos`/`relRot` in the parent's frame.

And a **third** shape that widens the blast radius enormously — base `Character.GetRelativePosition` (`DECOMP/Character.cs:3732-3752`) **[V]** fires for *anyone standing on any ZNetView-bearing rigidbody*, with a genuine relative-velocity term. A deckhand walking a longship, or a creature standing on it, is on this path.

The observer side resolves it with a **hard ordering dependency** (`ZSyncTransform.cs:273-345`) **[V]**: find the parent instance, force-run **the parent's `ClientSync` first**, then either snap to the named child transform (mount, zero error, no interpolation) or `TransformPoint(relPos)` with lerp and extrapolation (ship). Rotation is reconstructed as `parentRotation * relRot` — a full quaternion.

Note also `ZSyncTransform.cs:179` writes **world** velocity to `s_velHash` and `:202` **overwrites it with parent-relative** velocity in the same pass **[V]**. One hash, two meanings, discriminated only by whether the connection resolves.

Deck-sticking further leaks ship ownership into character motion (`Character.cs:1611-1621`) **[V]**: a standing character on a ship it owns gets a velocity nudge; on a ship it does **not** own, `m_body.position` is hard-set every FixedUpdate.

### 1.6 What Lumberjacks carries for any of this today: nothing

- **Zero vehicle references in the mod.** A case-sensitive grep over all `.cs` under `MOD` for `Ship|ShipControlls|Sadle|Vagon|Tameable|RequestControl|Rudder|Doodad|AttachStart|attachJoint|relPos` returns **no matches** **[V]**.
- **The routed allow-list is seven methods**, none of them vehicular (`MOD/Core/Services/RoutedRpcCutoverRunner.cs:526-538`) **[V]**; anything outside it returns `true` from `AllowNativeRoute` — i.e. take the native path (`:150-153`) **[V]**.
- **The C6 motion frame is 36 bytes** and structurally cannot express a rider: ZDO user/id, cm-quantized world XYZ, cm/s velocity, a single `uint16` yaw at 0.1°, and a timestamp (`MOD/Core/Services/ValheimMotionCodec.cs:9`, `:43-54`) **[V]**. No parent ZDOID, no attach joint, no relative pose, no pitch/roll, no angular velocity.
- **C6's suppression is keyed to remote *players*.** `SuppressNativeTransform` / `MaskNativePosition` gate on `HasRemote(zdo.m_uid)` against `_remote`, populated from received motion snapshots (`MOD/Core/Services/LumberjacksMotionRunner.cs:1369-1423`) **[V]**; the Harmony prefix skips `ZSyncTransform.CustomFixedUpdate` wholesale (`MOD/Patches/MotionAuthorityPatches.cs:11-16`) **[V]**. No creature or vehicle ZDO can ever be in that set today.
- **The `SyncTransform` connection type appears in the mod exactly once**, in a load-time spawner-reconnect filter mask (`MOD/Core/Services/SpawnerConnectionCache.cs:35-38`) **[V]**. The runtime journal handles only `ConnectionType.Portal` (`ZdoJournalCutoverRunner.cs:681`) **[V]**.
- **The C4 `SetOwner` guard cannot see any of the three vehicle sites.** It requires `ReleaseScopeDepth > 0` *and* membership in a run-tagged target set (`MOD/Core/Services/OwnershipLeaseCutoverRunner.cs:205-216`) **[V]**; the only Harmony seam is on `ItemDrop.RequestOwn` (`:218-227`) **[V]**.
- **AoI band shaping defaults OFF** (`MOD/Config/PluginConfig.cs:786-796`, `zdoBandShapingEnabled = false`) **[V]**, so the "carts get thinned like chests" concern is latent, not live. When on, the band decision is **pure distance + landmark + player-fast-lane** (`MOD/Core/Services/ZdoBandPolicy.cs:42-70`) **[V]** — the priority classifier's rank is telemetry only (`ZdoRedirectRunner.cs:439-447`, `:603-624`) **[V]**. Correction to a prior lens claim.
- Note an accident worth knowing: `LumberjacksPriorityClassifier` matches `Character` in the component list first and returns `player_critical` rank 0 (`LumberjacksPriorityClassifier.cs:25-29`) **[V]** — so a tamed Lox already classifies as player-critical, while a Karve/Longship/cart matches the storage clause at `:44` before the ship clause at `:51`. Telemetry-only today; a trap if rank ever feeds shaping.

### 1.7 Server-side reality

`ZDOMan.ReleaseZDOS` runs every 2 s, server-only, once for the server's own reference position and once per peer (`DECOMP/ZDOMan.cs:594-607`) **[V]**, and `ReleaseNearbyZDOS` releases/seizes on pure active-area geometry over **persistent** ZDOs, with no knowledge of `attachJoint`, `s_user` or `InUse()` (`ZDOMan.cs:623-648`) **[V]**.

Every `SetReferencePosition` call site in the decomp is local-player driven: `Game.cs:459/488/510/513`, `Player.cs:644/1502`, `Tracker.cs:13/31`, `Valkyrie.cs:180` **[V]**. So on a headless server the reference position stays at origin, its own seize window is the world origin, and `ZNetScene` never instantiates a ship at sea. **There is no server-side vehicle simulator to promote authority to** **[I — strong, from the exhaustive call-site sweep; the negative "no headless bootstrap sets it" is the inferential step]**.

---

## 2. Why this is different in kind from the proven pickup boundary — and where it is *less* novel than the audit assumes

The audit's framing is that vehicles are "owner-authoritative physics; requires lease-lane extension + explicit tug-of-war verification… the largest genuinely novel ownership work left" (`fieldlab/C8-BREADTH-AUDIT-2026-07-31.md:115-117`) **[V]**. That is right about the size and wrong about the shape. Four specific corrections, two of which make the work *smaller* and two of which make it *different*.

### 2.1 LESS novel: the continuous-control *stream* is much smaller than "continuous" suggests

The brief's premise — "control is continuous rather than one-shot" — is true of the *input loop* and largely false of the *wire*.

- **Mounts:** `Sadle.ApplyControlls` fires per input frame (`Sadle.cs:268`), but the rider **is** the ZDO owner, so `ZRoutedRpc.InvokeRoutedRPC` dispatches locally and never routes (`ZRoutedRpc.cs:130-137`) **[V]**. Vanilla mount control costs **zero bytes**.
- **Ships:** rudder is throttled to 5 Hz (`Ship.cs:218-222`) and throttle is one RPC per keypress — and even that traffic only leaves the machine when helmsman ≠ owner.
- **Carts:** two parameterless RPCs, one bool.

The total replicated control state across all three families is: one int, one float, two longs, one bool. **There is no continuous state-replication problem here.** Anyone sizing C10a as "we must build a 60 Hz input lane" is sizing the wrong thing — *unless* the design moves ownership to the server, which is exactly what converts a free local loop into a real per-input-frame message stream (§4.A).

### 2.2 LESS novel: the lease's *identity* model already fits

C4's lease is keyed `(world_epoch, uid_user, uid_id)` with a server-attested holder and a monotone per-slot epoch (`Gateway/Valheim/ValheimOwnershipLeaseService.cs:26-38`, `:137-138`) **[V]**. A vehicle is one ZDO. One lease slot per vehicle is the correct shape, and the "who may drive" question maps cleanly onto "who holds the lease". The mutual-exclusion semantics vanilla wants (`ShipControlls.HaveValidUser`, `Vagon.InUse`, `Sadle.CalculateHaveValidUser`) are strictly *weaker* than what the lease store already provides. That part is a port, not a design.

### 2.3 MORE different: this is a **simulation-locus** problem, not a mutation-authority problem

C0–C8 never had to answer "whose machine runs this Rigidbody for the next four minutes." The C4 lease answers "who may perform this one mutation, right now, validated at the instant of mutation." Those are not the same question, and the built artifacts show it:

- The game-server authorization gate is a **latch**, not a window: `target.Completed || target.Authorized` throws (`MOD/.../OwnershipLeaseCutoverRunner.cs`, HandleAuthorized) **[U-lens: :422-426]**, and `Complete` marks the slot terminally `completed` (`ValheimOwnershipLeaseService.cs:102-107`) **[V]**.
- Lease duration is hard-capped at **1..30 seconds**, thrown at the Gateway (`ValheimOwnershipLeaseService.cs:24-25`) **[V]**. A crossing is minutes.
- There is **no handoff verb**. `Issue` overwrites the slot and bumps the epoch unconditionally, without checking the incumbent (`:29-37`) **[V]**; contention is answered by rejection only. Vanilla's ships hand ownership peer-to-peer every 2 s (`Ship.cs:690-699`) and mounts transfer it as part of the grant (`Sadle.cs:349`) **[V]**.
- **Reclaim on socket loss is blanket and terminal** (`ValheimOwnershipLeaseService.ReclaimByLogicalPeer`, `:112-135`) **[V]**, fired from the WebSocket `finally` ahead of the transport's resume window **[U-lens: `GameWebSocketMiddleware.cs:293-299`]**, and nothing informs the game server. For a 0.75 s pickup blip that divergence is invisible; for a boat mid-crossing it is the central failure mode.
- And there is **nowhere to promote authority to**: no headless-server ship instance exists (§1.7).

So the novel work is not "extend the lease to more objects." It is: **decide who simulates a non-player Rigidbody, make that designation survivable across disconnect and zone churn, and make the lease govern the designation rather than a mutation.** That is a genuinely new primitive and the audit is right that it is the biggest thing left — it just named the wrong reason.

### 2.4 MORE different, and unnamed: two audit rows are misclassified, and one whole channel is missing

**(a) One RPC name, two ownership semantics, two identity spaces.** `synthetic_baseline_v2.json` records `RequestControl` → `["Sadle.Awake", "ShipControlls.Awake"]`, signature `["Int64"]` **[V]** — one row, one bucket, one priority. But:

| | ShipControlls | Sadle |
|---|---|---|
| transfers ZDO ownership | **no** (`ShipControlls.cs:93-107`) | **yes** (`Sadle.cs:349`) |
| `s_user` payload | `player.GetPlayerID()` (`:60`) — persistent profile UID | `player.GetZDOID().UserID` (`Sadle.cs:224`) — session id |
| liveness test | local trigger list (`IsPlayerInBoat`) | distance scan over peer character ZDOs |

All **[V]**. A payload contract or lease lane keyed on the method-name hash will be wrong for one of them. Worse: the ship's `s_user` is a client-generated profile UID, i.e. exactly the wrong thing for an authenticated lane to trust, and `RPC_ReleaseControl` authorizes on the *payload*, not the sender (`ShipControlls.cs:109-115`) **[V]** — a free eviction primitive.

The extractor sees only name and signature, so **this class of divergence is invisible to the instrument that produced the 160-row table.** How many of the other 118 needs-lane instance rows have registrant-dependent semantics is unknown.

**(b) `RPC_RequestOwn` is bucketed superseded on a false premise.** The audit says "the other registrants (`ArmorStand`, `ItemStand`, `Vagon`) ride the same replaced dance" (`C8-BREADTH-AUDIT:39`) **[V]**. `Vagon.RPC_RequestOwn` is owner-side admission control with an `InUse()` veto and an explicit `RPC_RequestDenied` reply, acquiring **continuous** control of a physics joint (`Vagon.cs:130-144`) **[V]** — structurally the same thing the audit reopened one line earlier for `Sadle`/`ShipControlls`. Meanwhile its reply half, `RPC_RequestDenied` (`Vagon.Awake` only) **[V]**, sits in the 118-row needs-lane bucket. Two halves of one handshake, two buckets, two fates. I did **not** read `ArmorStand.cs`/`ItemStand.cs`, so I cannot say whether those two are also miscategorised **[U]**.

**(c) The rider/passenger channel is not in the audit at all.** It is not an RPC, so an RPC-shaped audit could not see it. It is a ZDO connection type plus three fields, with an intra-frame ordering guarantee, and C6's prefix deletes the code that consumes it. This is the part that is genuinely "different in kind" from anything C0–C8 proved, and it is currently unnamed in every programme document.

### 2.5 The one claim the source fully confirms

"Owner-authoritative physics" is exactly right: `Ship.cs:305-308`, `Vagon.cs:163/186`, `BaseAI.UpdateAI` non-owner return **[U-lens: BaseAI.cs:303-309]**, `ZSyncTransform.OwnerSync`/`ClientSync` complementary gates (`ZSyncTransform.cs:119-128` and the `ClientSync` owner early-return) **[V/partial]**.

---

## 3. The hazards, ranked

### H1 — BLOCKING. Under native-zero, vehicle interaction fails silently, and the poison ledger does **not** trip

**Verified chain [V]:**
1. All 16 vehicle/mount RPC names are outside the seven-method admitted set → `AllowNativeRoute` returns `true` (`RoutedRpcCutoverRunner.cs:150-153`, `:526-538`).
2. Native `RouteRPC` runs → `ZRpc.Invoke("RoutedRPC", …)`.
3. `DirectControlNativeInvokePatch` calls `LogicalPeerCutoverRunner.TryHandleInvoke` **first** (`MOD/Patches/NativeNetworkPatches.cs:133-144`).
4. `TryHandleInvoke` increments a counter, logs at counts 1–4 and powers of two, and returns `true` — suppressing the native invoke (`LogicalPeerCutoverRunner.cs:332-373`).
5. `LogicalPeerCutoverRunner` contains **zero** references to `NativeNetworkLedger` (grep, **[V]**). No `Observe`, no poison trip.

Contrast the inbound funnels, which do `Observe` and do trip (`NativeNetworkPatches.cs:184`, `:205`) **[V]**.

**In play:** a player walks up to a boat owned by another client, presses Use, and *nothing happens* — no message, no error, no ledger row, no failed gate. Same for hitching a cart or mounting a Lox. Because `InvokeRoutedRPC` short-circuits when you are already the owner (`ZRoutedRpc.cs:130`) **[V]**, it will appear to work intermittently — specifically, whenever the player happens to own the vehicle ZDO — which is the worst possible failure signature.

This directly falsifies the audit's stated safety net: "the poison ledger remains the runtime tripwire for anything classified wrongly here" (`C8-BREADTH-AUDIT:28`) **[V]**. It holds inbound. It does not hold on the outbound unadmitted-method path.

*Caveat:* verified statically only. Not observed live. E1 in §5 falsifies it in one manual session.

### H2 — BLOCKING. There is no server-side vehicle simulator to promote authority to

`ZNet.m_referencePosition` is only set from local-player code paths (8 call sites, all client) **[V]**; `ZNetScene` builds GameObjects around that position; a headless server therefore has no `Ship` MonoBehaviour, no Rigidbody, no buoyancy solver. **[I]**

**In play:** a server-authoritative boat is not a re-plumbing of an existing mechanism — it is standing up Unity physics on the dedicated server for objects nobody has claimed, with the `Floating.GetWaterLevel`/`Heightmap` dependencies those forces need. Every previous slice replaced a mechanism that already existed server-side. This one does not exist.

### H3 — BLOCKING. C6's suppression deletes the rider-attach reconstruction path

`MotionAuthorityTransformPatch` prefixes `ZSyncTransform.CustomFixedUpdate` and returns false for any tracked remote (`MotionAuthorityPatches.cs:11-16`, `LumberjacksMotionRunner.cs:1369-1382`) **[V]**. `CustomFixedUpdate` is the only entry to `ClientSync` → `SyncPosition`, which is where the connection lookup, the parent-first ordering, the bone snap, the `TransformPoint(relPos)` solve **and the non-owner gravity-disable/`Sleep()` handling** all live (`ZSyncTransform.cs:273-345`, and the non-owner Rigidbody block) **[V]**.

Whether this bites depends on whether a *ridden* player is in `_remote`. I did not trace every insertion site into `_remote` **[U]** — see §6.

**In play (if riders are tracked):** a passenger on a moving longship is placed by 20 Hz world-space frames while the hull under them is placed by native interpolation. They slide aft, visibly, proportional to hull speed. On a mount, the rider is placed at a world position while the mount's bone moves independently. And separately: a suppressed `ClientSync` never disables gravity on the remote body, so the remote vehicle/rider Rigidbody is awake and unconstrained.

Compounding: the rider's replicated rotation is supposed to be `parentRot * relRot` — a full quaternion, identity-relative for mounts (`Player.cs:6713-6714`, `ZSyncTransform.cs:330-337`) **[V]**. The C6 frame carries yaw only at 0.1° (`ValheimMotionCodec.cs:51`) **[V]**. Riders will be gimbal-upright on a pitching deck and will counter-rotate against their mounts.

### H4 — SERIOUS. One RPC name, two ownership semantics, two identity spaces

See §2.4(a). **In play:** a lease lane that grants ownership on `RequestControl` gives away the boat every time someone takes the helm (a behaviour change vanilla deliberately avoids); one that doesn't grant ownership leaves a mount unsimulated. And whichever `s_user` mapping is chosen, one of the two registrants is wrong.

### H5 — SERIOUS. Three component-level `SetOwner` sites sit outside every guard the programme has built

`Ship.cs:696` (client, 2 s timer), `Sadle.cs:349` (on grant), `Vagon.cs:141` (on grant) **[V]**. The C4 guard requires `ReleaseScopeDepth > 0` and run-tagged selection (`OwnershipLeaseCutoverRunner.cs:205-216`) **[V]**, so none is blocked. `fieldlab/NETCODE-OWNERSHIP-MAP.md`'s enumeration of funnels does not include them **[U-lens]**.

**In play:** any Lumberjacks lease on a ship is overwritten roughly every 2 s by whichever client currently owns it and steps off, with no rejection and no telemetry.

### H6 — SERIOUS. The lease's lifetime and reclaim semantics are actively wrong for a held vehicle

30 s hard cap **[V]**; renewal is a full `reissue` round trip that mints a **new epoch**, and `Validate` requires **exact** epoch equality (`ValheimOwnershipLeaseService.cs:61`) **[V]** — so every renewal invalidates in-flight frames; expiry is **lazy and silent**, discovered only when an action arrives (`:65-70`) **[V]**; reclaim on any socket loss is blanket and terminal **[V]**.

**In play:** a 3-second WebSocket blip mid-crossing strips helm authority with no notification, leaves the native ZDO owner wherever the last authorized action put it, and designates nobody to simulate the hull. For a cart it is worse — a lapsed lease means a non-owner tick, which destroys the joint (`Vagon.cs:186-189`) **[V]** and drops a loaded cart, silently, mid-haul.

### H7 — SERIOUS. Throttle is a relative state machine with no idempotency

`RPC_Forward`/`RPC_Backward` *step* the enum rather than setting it (`Ship.cs:257-297`) **[V]**, and the pilot has no local `m_speed` prediction — it reads `s_forward` back (`Ship.cs:575`) **[V]**.

**In play:** over a lossy or reordered lane, one dropped `Forward` leaves the boat permanently one notch slower than the pilot believes, with no self-correction. Vanilla only got away with this over ordered, reliable Steam channels.

### H8 — SERIOUS. Gameplay predicates read from *local physics perception*, which Lumberjacks now authors

`Ship.m_players` (trigger callbacks) gates the helm grant, the ownership handoff, speed decay and `CanBeRemoved` **[V]**. `Sadle.CalculateHaveValidUser` gates the grant, saddle removal and `HaveRider()` **[V]** — and already returns false on bystander clients in vanilla (`ZNet.cs:1847-1867`) **[V]**. C6 applies remote positions by writing `transform.position` directly, which produces trigger events only after the physics step resolves them **[U-lens: `LumberjacksMotionRunner.cs:333`]**.

**In play:** the boat owner's client never registers a remote player as "aboard", so `RequestControl` is refused with no error path; or a boat with two people on it decides `m_players.Count == 0` and forces `Speed.Stop`. This is invisible until two real clients share one boat.

### H9 — SERIOUS. Nothing in vanilla authorizes vehicle movement, so the lease is a **new** boundary with no vanilla oracle

`RPC_Rudder`/`Stop`/`Forward`/`Backward` have no authorization at all (`Ship.cs:242-297`) **[V]**, and `RPC_RequestRespons` starts doodad control on the receiving client with no check that this client asked (`ShipControlls.cs:117-135`, `Sadle.cs:390-408`) **[V]**.

**In play:** a validating server will reject traffic vanilla accepted, so contract tests cannot diff against a vanilla oracle for this row — they must assert the new rejection behaviour. Also: strict unicast for `RequestRespons` becomes load-bearing. Any mis-fanout (the ADR-0013 multicast work is built-but-unproven) mounts uninvolved players onto creatures they never touched.

### H10 — MODERATE. The 2 s ownership tick vs. RPC target resolution

`InvokeRPC` resolves the target from `m_zdo.GetOwner()` at send time (`ZNetView.cs:326-329`) **[V]**; `Ship.UpdateOwner` and `ZDOMan.ReleaseNearbyZDOS` both move ownership on independent 2 s ticks **[V]**. Vanilla tolerates the race by silently dropping the input.

**In play:** a fail-closed validator turns a routine handoff into a burst of lease-mismatch rejections that look like an attack. The lease must be vehicle-scoped and survive owner migration, or migration itself must be lease-mediated.

### H11 — MODERATE. `attachJoint` is an int on cart ZDOs and a string on character ZDOs

`Vagon.cs:279` writes `Set(s_attachJointHash, bool)` → int table; `ZSyncTransform.cs:190` writes `Set(s_attachJointHash, string)` → string table, read back at `:289` **[V]**. Vanilla is safe because `ZDOExtraData` is type-partitioned and the two live on different ZDOs.

**In play:** a journal or contract test mapping stable-hash → one declared type mis-decodes one of them. Failure mode is a cart that reads permanently attached (refusing every hitch), or a rider whose attach point decodes as garbage.

### H12 — MODERATE. `s_velHash` means two different things and both are on the wire

`ZSyncTransform.cs:179` (world) then `:202` (parent-relative), same pass **[V]**; read as relative at `:292` and world at `:357` **[V]**.

**In play:** a schema that types `vel` as world-space is silently wrong for every rider — producing plausible drift, not a crash.

### H13 — MINOR. Stale `attachJoint = 1` persists into the save

`Detach` clears only if owner; `OnDestroy` clears nothing (`Vagon.cs:101-104`, `:302-305`) **[V]**. Vanilla self-heals on the next owner's `FixedUpdate`. A lane that derives "in use" from the replicated flag inherits a field that is written by one side, cleared conditionally, never cleared on unload, and saved.

### H14 — MINOR. `Ship.Rudder(float)` is a phantom entry point

`Ship.cs:237-240` binds to `MonoBehaviour.Invoke` **[V]**. An agent enumerating "ship RPC surface" by method name will admit it and may wire real behaviour to it.

### H15 — MINOR. Puller extra-mass leaks on unload

`SetExtraMass` is reverted only in `Detach`, never in `OnDestroy` (`Vagon.cs:281-284`, `:308-311`, `:101-104`) **[V]**. Gated on `m_playerExtraPullMass != 0f`, whose prefab value is unknown **[U]**.

---

## 4. Design questions that must be answered before implementation

These are the real forks. I am deliberately not picking winners where the evidence does not.

### A. Who simulates a vehicle?

| Option | For | Against |
|---|---|---|
| **A1. Designated client under lease** (lease says *who*, that client runs physics) | closest to vanilla; latency-correct for the pilot; no new server capability; the free local control loop stays free | client-authoritative physics for a shared world object — the exact thing C6 removed for players; a fail-closed programme accepting a client-authored transform needs an explicit written justification |
| **A2. Server simulates, clients send input** | matches the plan's "server is the sole mutation authority"; uniform with C6; no ownership churn at all | **no server-side vehicle exists today** (H2); converts a 0-byte mount loop into ~60 msg/s/rider (§2.1); adds a tick budget for buoyancy/heightmap on the dedicated host; the pilot loses local prediction unless one is built |
| **A3. Status quo — native ownership keeps deciding, lease governs only *who may ask*** | smallest diff; preserves vanilla feel exactly | leaves three unguarded `SetOwner` sites (H5) and the 2 s churn funnel inside the fail-closed boundary; arguably fails the C10 exit criterion "native ownership-transfer triggers cannot change the owner" |

This is the decision that sizes C10a. Everything else follows from it. Note the plan makes no such decision anywhere I could find **[V — grep of the plan for C10a returns one bullet, `plan-native-network-final-cutover.md:777`]**.

### B. Does the lease bind *control* to *simulation*?

Vanilla deliberately separates them for ships and fuses them for mounts and carts **[V]**. A single "vehicle lease" that fuses them is simpler and is probably the right engineering answer — but it is a **behaviour change** for ships (the helmsman would start owning the hull), not a re-plumbing. It must be a stated, owned decision, not an emergent consequence of reusing the C4 shape.

Sub-question: does one lease abstraction cover control-with-ownership (Sadle, Vagon) *and* control-without-ownership (Ship), or are these two rows that must be split in the audit first?

### C. How does a rider's transform travel?

| Option | For | Against |
|---|---|---|
| **C1. Extend the motion frame** with an optional parent ZDOID + attach-joint + relative pose | one lane, one authority model, consistent with C6 | breaks the fixed 36-byte contract (`ValheimMotionCodec.cs:9`, `TryRead` rejects any other length at `:31`); needs a full quaternion for the ship case; must reproduce the parent-before-child ordering, which an arbitrary-order applier cannot |
| **C2. Narrow C6's suppression** so attached players fall back to native `ZSyncTransform` while attached | zero new protocol; the ordering guarantee and the bone-name resolution come free; smallest change | reintroduces a second authority for player transforms in exactly the state the programme just took authority over; the attach state itself is only known on the rider's own client (`Player.FixedUpdate` owner gate) so the *observer* needs a signal to know when to fall back |
| **C3. A separate attachment lane** (seat identity as a first-class Lumberjacks concept) | clean; can carry seat occupancy, which vanilla has nowhere (`Chair.Interact` is a purely local proximity check) | most new surface; no vanilla field to mirror; C10a is supposed to be admissions, not new subsystems |

Note the scope trap in all three: base `Character.GetRelativePosition` (`Character.cs:3732`) **[V]** covers *anyone standing on a moving body*, not just seated pilots. Sizing this around `RequestControl`/`ReleaseControl` undercounts by the whole passenger and cargo-creature population.

### D. Which identity is `s_user` under Lumberjacks?

Three id spaces collide: profile UID (ships, client-generated, spoofable), ZDOID.UserID / session id (mounts, ZDO owners, RPC senders), and the authenticated logical-peer id (C4 leases) **[V]**. The profile UID is exactly the wrong thing to trust. Mapping is not mechanical, and `RPC_ReleaseControl` authorizing on payload rather than sender (`ShipControlls.cs:111`) **[V]** must be closed as part of it.

Related, and possibly load-bearing: `ZNet.SetCharacterID`'s direct (non-routed) `CharacterID` message is what populates `GetAllCharacterZDOS` and therefore the *server's* ability to evaluate mount rider validity **[V for the dependency; U for whether the Lumberjacks handshake carries it]**. `LogicalPeerCutoverRunner.TryHandleInvoke` does special-case `"CharacterID"` and queue it as a logical-peer control (`LogicalPeerCutoverRunner.cs:340-357`) **[V]** — so it *is* carried, but I did not verify the server-side consumption path **[U]**.

### E. Ship throttle wire contract: bug-compatible or fixed?

Keep the relative stepping (bug-compatible, inherits H7 on a lane that is not ordered-reliable), or send the absolute `Speed` enum (idempotent, but a wire-contract change that must be decided in design, not discovered in test).

### F. Lease lifetime: reissue-as-renewal, or a new held-lease verb?

Reissue is a full round trip that mints a new epoch and invalidates in-flight frames **[V]**. A held-lease shape (grant / heartbeat / revoke, with a push notification on expiry) is what a continuous hold actually wants, and C4 has not proven it. The 30 s cap forces a choice either way.

### G. Reclaim policy for a held vehicle on socket loss

Current policy is blanket and terminal ahead of the 2-minute transport resume **[V]**. For a pickup that is correct. For a boat mid-crossing, what *should* happen — a grace window, a designated-successor handoff, a fail-safe "drop anchor" — is an unanswered product question, not just an implementation gap.

### H. What is actually in family 1?

The audit writes "(`Ship`, `ShipControlls`, `Sadle`, `Vagon`, …)" with an ellipsis and no enumeration retained (`C8-BREADTH-AUDIT:115`) **[V]**. `synthetic_baseline_v2.json`'s component list also contains `Catapult`, `SiegeMachine`, `Leviathan`, `ShipConstructor`, `ShipEffects` **[V]**. `Catapult`/`SiegeMachine` carry a `Vagon` per prior lens **[U-lens]**. Family membership must be enumerated before it can be scheduled.

---

## 5. What could be proven cheaply first

Ordered by (uncertainty collapsed) ÷ (cost). All five run on the existing AM4 + two-client (omen/i5) harness or cheaper.

### E1 — "Does a boat work at all under native-zero?" (manual, ~30 min, no code)

Two clients on AM4, poison armed, one boat. Client A owns the hull (spawn it on A). Then, in order: A boards and takes the helm; B boards and takes the helm; A steps off; B hitches a cart; either mounts a tamed creature.

**Predicts, from H1:** A's helm request succeeds (local short-circuit, `ZRoutedRpc.cs:130`); B's helm request **silently does nothing**; `native-network` poison ledger shows **no trip**; `LogicalPeerCutoverRunner._suppressedInvokes` climbs (sampled log lines at counts 1–4 and powers of two, `LogicalPeerCutoverRunner.cs:367-371`).

**Collapses:** H1 in full, including the audit's poison-tripwire claim at `C8-BREADTH-AUDIT:28`, and it establishes whether vehicles are broken-loud or broken-quiet — which changes how C10a sequences everything. If the tripwire *does* fire, my static reading is wrong and that is worth knowing immediately.

**Instruments already present:** `valheim_tail_bepinex_log`, `valheim_netcode_probe_summary`, `valheim_server_log_tail`.

### E2 — Read the prefab flags (no live test at all)

Dump `ZSyncTransform.m_characterParentSync`, `m_syncPosition`, `m_syncRotation`, `m_syncBodyVelocity`, and `Rigidbody.isKinematic` off the **Player**, **Karve/Longship**, **Cart** and a **saddled creature** prefab — via an asset dump, or one log line from an existing probe on `Awake`.

**Why it is the single highest-value cheap step:** the class defaults are `m_syncBodyVelocity = false` and **`m_characterParentSync = false`** (`ZSyncTransform.cs:6-14`) **[V]**. The *entire* rider-attach channel — both the write side (`:183`) and the read side (`:276`) — is gated on a flag whose default is off. If it is not set on the Player prefab, H3, H12, C1/C2/C3 and most of §1.5 evaporate. If it is set, they are all live. Nothing else in this note has that leverage-to-cost ratio.

`isKinematic` additionally selects between two completely different receive paths (`ZSyncTransform.cs:384` vs the dead-reckoning branch) **[V]**, which decides whether a remote boat extrapolates or tracks.

### E3 — Measure vehicle ownership churn instead of asserting it

**Finding that makes this nearly free:** `TelemetryCoordinator.RecordOwnershipChurn` exists and writes `ownership-churn.jsonl` (`MOD/Core/Services/TelemetryCoordinator.cs:439-444`) **[V]**, and a repo-wide grep finds **zero callers** **[V]**. The sink is plumbed; the producer went with the swarm harness. The MCP tails (`valheim_tail_ownership_churn`, `valheim_ownership_churn_summary`) already point at it.

One postfix on `ZDO.SetOwner` recording `(prefab, old_owner, new_owner, callsite)` into the existing sink, then: a scripted cart pull across two zone boundaries, and a two-client boat crossing.

**Collapses:** whether "carts are an ownership churn hotspot" is measured or folklore, and — more importantly — whether the `ReleaseNearbyZDOS` *release* branch fires at all during a normal pull. The cart is joint-locked ~2 m from the puller, so the hysteresis band (`FindSectorObjects` over `m_activeArea`, decisions over `m_activeArea - 1`, `ZDOMan.cs:627-628`) **[V]** may make single-peer pulls churn-free and only multi-peer proximity contentious. That distinction changes fork A materially.

### E4 — Rider separation A/B on the existing motion harness

Two clients, one boat, one pilot and one passenger standing on the deck. Run with C6 authority ON and OFF, measure observer-side rider-to-deck offset over a straight run and a turn. The `motion_drive`/`motion_observe` action kinds and the C9 clip-capture wrapper already exist (`fieldlab/scenarios/native-*-c8-full44.json` action kinds **[V]**).

**Collapses:** H3's magnitude — whether the rider/deck problem is a visual annoyance or a hard break — and whether ridden players are even in `_remote` (§6). Gate this behind E2: if `m_characterParentSync` is off, skip it.

**Note:** this needs no new scenario *kind*; it needs a boat placed at a known world position in the scenario and a `wait`/`move` sequence on the deck. Everything else is existing machinery.

### E5 — Cost one lease cycle before designing renewal cadence

Instrument reliable-lane frames per lease cycle and the client/gateway queue depths during an existing `ownership_lease_pickup` action. The session retro already wants a per-second queue-depth-by-frame-type reducer.

**Collapses:** whether renewal-every-≤30 s per vehicle per rider is affordable at all, and whether the Gateway's outbound reliable window needs the same control-frame bypass the mod side got in wall 14. Right now that number is a count-by-reading, not a measurement, and fork F should not be decided on a count-by-reading.

**Sequencing note:** E2 first (free, and it gates E4). E1 next (cheap, and its answer reorders everything downstream). E3 and E5 in parallel. E4 last.

---

## 6. Open questions the source did not settle

**Prefab data (the big one).** The decompiled C# cannot show serialized field values. Unresolved: whether `m_characterParentSync` is set on the Player prefab (default is **false**, `ZSyncTransform.cs:14`) **[V]**; whether the Karve/Longship/Cart even carry a `ZSyncTransform` (established only *by elimination* — the only continuous `ZDO.SetPosition` writer, with `SnapToGround`/`StaticPhysics` being one-shot **[U-lens, plausible]**); `m_syncBodyVelocity`; `Rigidbody.isKinematic`; `m_playerExtraPullMass` on the Cart; whether tamed-creature prefabs set `m_persistent` (which decides whether `ReleaseNearbyZDOS` will ever reclaim an abandoned mount at all — `ZDOMan.cs:631-634` skips non-persistent outright **[V]**); the cart's `ZDO.Type` (`Prioritized` gets special send treatment); and whether the cart's `Container` uses `m_rootObjectOverride` (which decides whether the vehicle lease and the container lease are one lease or two). → **E2**.

**Whether a ridden player is in `_remote`.** `IsCanonicalRemote`/`HasRemote` key on ZDOID (`LumberjacksMotionRunner.cs:1409-1423`) **[V]**, but I did not read every insertion site into `_remote`. If attached players are excluded, H3 is already avoided; if not, it is blocking. This is a 10-minute read that I did not do and that should be done before H3 is escalated.

**Does `ZDO.Set` deduplicate?** The chain is `ZDO.Set(int,int)` → `ZDOExtraData.Set` → `s_ints.InitAndSet` → `BinarySearchDictionary.SetValue`, whose **bool return** decides whether `IncreaseDataRevision()` runs (`ZDO.cs:352-358`, `ZDOExtraData.cs:183-186`, `ZDOHelper.cs:53-57`) **[V]**. `BinarySearchDictionary` is **not in the decomp set** (verified absent) **[V]**. If `SetValue` returns true unconditionally, `Ship.UpdateControlls`' per-FixedUpdate `s_forward`/`s_rudder` writes bump DataRevision ~50×/s on every ship ZDO — which resets the receiver's extrapolation timer (`ZSyncTransform.cs:294-298`, `:348-352`) **[V]** and effectively disables dead reckoning for ships, and changes the AoI cost model. If it dedupes on equality, they are nearly free. **This materially changes the vehicle bandwidth estimate and I could not settle it.**

**Can the dedicated server ever own a vehicle ZDO?** `ReleaseZDOS` calls `ReleaseNearbyZDOS(ZNet.GetReferencePosition(), m_sessionID)` **first**, before the per-peer pass (`ZDOMan.cs:602-606`) **[V]**. I verified every `SetReferencePosition` call site is client-side, but I did not exhaustively rule out a headless bootstrap path. If one exists, a vehicle near world origin could be owned by a server that has no instance to simulate it — nothing would run `OwnerSync`, yet `zdo.HasOwner()` stays true and `SyncPosition`'s parent guard still passes. That is a distinct and worse failure mode ("ghost ship that never moves for anyone") and it is worth ruling in or out explicitly.

**Grant vs. sweep race.** Both `Sadle.RPC_RequestControl` and `ReleaseNearbyZDOS` call `SetOwner` and bump OwnerRevision; reconciliation is highest-revision-wins. Vanilla has no arbitration for "rider granted at T, sweep re-seizes at T+δ". May be benign; the source does not say. Deserves a two-client contention cell rather than reasoning.

**Does `OnTriggerExit` fire when a passenger's GameObject is destroyed** (crash, `ZNetScene` despawn) rather than moved out? If not, `m_players` retains a dangling reference and `HaveValidUser()` keeps returning true for a departed helmsman. Engine behaviour plus destruction ordering; not answerable from this source.

**Cross-lane ordering.** Vanilla guarantees parent-before-child inside one frame by force-calling the parent's `ClientSync` (`ZSyncTransform.cs:284-288`) **[V]**. Under the split, a vehicle arrives via the C3 journal on Unity Update and a rider via the C6 motion lane. No ordering guarantee exists, and no programme document acknowledges the dependency. Whether it manifests as a persistent one-frame offset, jitter, or nothing is empirical.

**Ship top speed in m/s** — a product of prefab-serialized force factors, sail size, mass and damping. Without it I cannot say how often C6's 30 m implausible-target guard would trip for a passenger; only that the guard was calibrated for walking.

**`ArmorStand` / `ItemStand` `RPC_RequestOwn`.** I did not read either file. The audit sweeps them in with `Vagon` and `ItemDrop`; the `Vagon` half of that claim is wrong (§2.4b). Whether the other two are also miscategorised is unknown.

**Audit bookkeeping deltas I noticed but did not chase** (each is small, and each feeds a work queue C10a is meant to consume): the audit says "three `[VERIFY]` flags" (`:139`) but carries four marks — `:36`, `:37`, `:38`, and an orphaned `RPC_SetConnection [VERIFY]` at `:70` **[V]**; the plan's C10a bullet names the vehicle row as "`RequestControl`/`ReleaseControl`", dropping `RequestRespons` from a three-method row (`plan-native-network-final-cutover.md`, Limits section) **[V]**; and priorities for 12 of the 16 vehicle/mount RPC names (`Forward`, `Backward`, `Rudder`, `Stop`, `Controls`, `RemoveSaddle`, `AddSaddle`, `SetSaddle`, `Command`, `SetName`, `RPC_UnSummon`, `RPC_RequestDenied` — all confirmed present in `synthetic_baseline_v2.json` **[V]**) exist only in HEARTH artifact `art_40ef58c9015d9d7a343b48f87f7f2916`, which is not in the repo. C10a cannot schedule what it cannot read.

**Finally, the honest boundary on the evidence itself:** the accepted C8 acceptance scenario (`fieldlab/scenarios/native-20260731-c8-full44.json`, 49 actions across 21 kinds) contains **zero** ship, boat, cart, vagon, sadle, saddle, mount or ride actions **[V — searched, excluding `ownership`/`membership` substring hits]**. "C8 native-zero passed" carries no signal whatsoever about vehicles. A first vehicle contract test should expect failure, not regression.
