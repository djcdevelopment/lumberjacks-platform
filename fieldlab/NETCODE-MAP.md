# Valheim Netcode Map — rung I0 deliverable

Source-grounded map of Valheim's managed replication path, per the I0 gate in
`VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md`. Every row cites a real method signature
and names the mod's intended hook point.

**Build mapped:** `assembly_valheim.dll`, Steam client install, dated 2026-07-01.
**Network protocol version:** `36` (the literal gate value in `RPC_PeerInfo`).
**Decompiler:** ilspycmd 8.2.0.7535 (ICSharpCode.Decompiler) → `scratchpad/decomp/*.decompiled.cs`.
**Corroborating mod source:** ddormer/valheim-serverside @ main, CW-Jesse/valheim-betternetworking @ main.

Citations of the form `ZDOMan:724` refer to line numbers in the decompiled files;
`Core.cs:145` refers to the valheim-serverside source; `BN_...cs` to BetterNetworking.

---

## Cross-cutting finding — the inlining risk is bounded (resolves worklog standing risk #1)

Every inbound RPC handler is installed as a **delegate** via `ZRpc.Register(...)` /
`ZRoutedRpc.Register(...)`:
- `ZDOMan.AddPeer` → `m_rpc.Register<ZPackage>("ZDOData", RPC_ZDOData)` (ZDOMan:495)
- `ZNet.OnNewConnection` → `Register<ZPackage>("PeerInfo", RPC_PeerInfo)`, `Register("ServerHandshake", ...)`, `Register<bool,string>("ClientHandshake", ...)` (ZNet:690-700)
- `ZRoutedRpc.AddPeer` → `Register<ZPackage>("RoutedRPC", RPC_RoutedRPC)` (ZRoutedRpc:73)

A method used as a delegate target **cannot be inlined by the JIT** — the delegate
needs a real method address. So the entire receive / handshake / routed-RPC handler
layer is inlining-proof and Harmony-reachable (prefix/postfix). Inlining risk is
therefore confined to the **send-side helpers** (`SendZDOs`, `CreateSyncList`,
`RouteRPC`) — all private, but large and multi-callsite, so JIT inlining is unlikely.
I1 confirms reachability at runtime; this map de-risks it in advance.

**Native boundary:** all five managed funnels sit ABOVE the `ZSteamSocket` transport
wrapper. The only native leg is inside `ZSteamSocket`'s Steamworks send, which the
transport seam already brackets (see funnel 0). No mapped funnel bottoms out in an
`extern` call Harmony can't reach.

---

## Funnel 0 — Transport seam (byte-level, both directions)

The raw socket layer, below ZDO semantics. Confirmed via BetterNetworking, which
already patches exactly here to compress traffic.

| | Outbound | Inbound |
|---|---|---|
| Method | `ZSteamSocket.SendQueuedPackages()` | `ZSteamSocket.Recv()` → `ZPackage` |
| Payload | `Queue<byte[]>` (`m_sendQueue`) | `ZPackage` |
| Hook | Harmony **Prefix**, `bool` return | Harmony **Postfix**, `ref ZPackage __result` |
| Proof | `BN_Patch_Compression_Steamworks.cs:16-24` | `...:26-47` |

- **Suppressible:** the send Prefix returns `bool`; `return false` skips the native
  send (BetterNetworking uses this in its `!IsConnected()` branch).
- **Replaceable:** the send Prefix rewrites `___m_sendQueue`; the recv Postfix
  reassigns `ref __result`.
- **Use:** wholesale byte-level redirect for I3/I4 if ZDO-semantic interception is
  ever the wrong altitude. Opaque to ZDO identity — funnels 1/2 are the semantic layer.
- With crossplay off (I6), `ZSteamSocket` is the SINGLE transport (no `ZPlayFabSocket`).

---

## Funnel 1 — ZDO send (item a)

**Entry:** `ZDOMan.SendZDOs(ZDOPeer peer, bool flush)` — `private bool`, ZDOMan:724.
Driven round-robin from `ZDOMan.Update(float dt)` at ZDOMan:577
(`SendZDOs(m_peers[m_nextSendPeer], flush: false)`); also flushed by
`SendAllZDOs(ZDOPeer)` at ZDOMan:717.

**Sync selection:** `ZDOMan.CreateSyncList(ZDOPeer peer, List<ZDO> toSync)` — `private void`,
ZDOMan:893. Server path: `FindSectorObjects` → `peer.ShouldSend(zdo)` →
`ServerSortSendZDOS` (the priority sort — this is where the priority-manifest work
attaches) → `AddForceSendZdos`.

**Wire format** (per ZDO, ZDOMan:761-784):
```
int   invalidSectorCount, then that many ZDOID
repeat until ZDOID.None:
  ZDOID   m_uid
  ushort  OwnerRevision
  uint    DataRevision
  long    GetOwner()
  Vector3 GetPosition()
  ZPackage  zdo.Serialize(...)   // the ZDO body
ZDOID.None  // terminator
```
**Dispatch:** `peer.m_peer.m_rpc.Invoke("ZDOData", zPackage)` (ZDOMan:787).

**Hook point:** Prefix `SendZDOs` to suppress/redirect a class of ZDOs, OR Postfix
`CreateSyncList` to filter `toSync`, OR intercept the `"ZDOData"` invoke. Send-side →
carries the bounded inlining risk (confirm at I1).

---

## Funnel 2 — ZDO receive / apply (item b)

**Handler:** `ZDOMan.RPC_ZDOData(ZRpc rpc, ZPackage pkg)` — `private void`, ZDOMan:792.
Registered as a delegate (ZDOMan:495) → **inlining-proof**.

**Apply path** (ZDOMan:822-856): read the wire format above; `GetZDO(id)` — if present
and `DataRevision` newer, apply; if absent, `CreateNewZDO(zDOID, vector)`; then
`zDO.SetOwnerInternal(owner)`, set `OwnerRevision`/`DataRevision`,
`InternalSetPosition`, `zDO.Deserialize(pkg2)`.

**Hook point (I4 inbound injection):** two options —
1. Prefix/replace `RPC_ZDOData` to feed a synthetic `ZDOData` `ZPackage` built in the
   funnel-1 wire format (authoritative state pushed from Lumberjacks), or
2. Call `CreateNewZDO` + `SetOwnerInternal` + `Deserialize` directly to inject one ZDO.
The wire format is fully specified, so a Lumberjacks→client injection is constructible
without a real Steam peer. Malformed-input hardening on this path is the I4 frontier work.

---

## Funnel 3 — Ownership storage + transfer trigger (item c)

**Storage/API:** `ZDO.SetOwner(long)` / `ZDO.GetOwner()`; internal apply variant
`ZDO.SetOwnerInternal(long)` (used by RPC_ZDOData). Server authority id =
`ZNet.GetUID()`. Unowned sentinel = `0L`.

**The vanilla transfer trigger:** `ZDOMan.ReleaseNearbyZDOS(Vector3 refPosition, long uid)`.
Vanilla behavior (per serverside comment, Core.cs:147-153): if a ZDO is no longer near
the peer, release ownership; if it has no owner, assign it to that peer.

**Proven seizure/hold mechanism** (valheim-serverside, Core.cs:145-195):
`[HarmonyPatch(typeof(ZDOMan),"ReleaseNearbyZDOS")]` with
`static bool Prefix(ZDOMan __instance, ref Vector3 refPosition, ref long uid)` that
**returns false** (full replacement). Reimplements the loop so a persistent ZDO with a
player in its active area that is unowned or whose owner is absent →
`zdo.SetOwner(ZNet.GetUID())` (server seizes); server-owned with no player nearby →
`zdo.SetOwner(0L)` (release).

**Hook point (I2):** prefix-return-false on `ReleaseNearbyZDOS`, reasserting
`SetOwner(ZNet.GetUID())` each pass. Directly satisfies I2's "hold against the normal
trigger" invariant. Save-corruption gate applies (writes owner state).

---

## Funnel 4 — ZRoutedRpc dispatch (item d)

**Outbound entry:** `ZRoutedRpc.InvokeRoutedRPC(long targetPeerID, ZDOID targetZDO,
string methodName, params object[] parameters)` — `public`, ZRoutedRpc:120. Builds
`RoutedRPCData`; local target → `HandleRoutedRPC`; remote → `RouteRPC`.

**Outbound send:** `ZRoutedRpc.RouteRPC(RoutedRPCData rpcData)` — `private`, ZRoutedRpc:140.
Server: `peer.m_rpc.Invoke("RoutedRPC", zPackage)` to target peer, or broadcast to all
but sender. Client: sends to all peers (→ server).

**Inbound handler:** `ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg)` — `private void`,
ZRoutedRpc:175. Registered as delegate (ZRoutedRpc:73) → **inlining-proof**.
Deserialize → if target me/0 → `HandleRoutedRPC`; if server and not me → relay via `RouteRPC`.

**Dispatch:** `HandleRoutedRPC(RoutedRPCData)` ZRoutedRpc:189 — targetZDO none →
`m_functions[methodHash].Invoke(senderPeerID, parameters)`; else find `ZNetView` for the
ZDO → `zNetView.HandleRoutedRPC(data)`.

**Wire format** (`RoutedRPCData.Serialize`, ZRoutedRpc:20-38):
`long m_msgID, long m_senderPeerID, long m_targetPeerID, ZDOID m_targetZDO,
int m_methodHash (= name.GetStableHashCode()), ZPackage m_parameters`.

**Hook point:** Prefix `RouteRPC` (outbound suppress/redirect) or `RPC_RoutedRPC`
(inbound). Method-name identity is a stable hash, so specific RPCs are targetable.

---

## Funnel 5 — Connection handshake sequence (item e)

Re-derived from source — the research had refuted the exact sequence. Ordered exchange:

1. **Client → server:** on `ZNet.OnNewConnection` (client branch) registers Kicked/Error/
   ClientHandshake, then `peer.m_rpc.Invoke("ServerHandshake")` (ZNet:701).
2. **Server `RPC_ServerHandshake(ZRpc rpc)`** ZNet:727: `ClearPlayerData`, then
   `Invoke("ClientHandshake", needPassword(bool), ServerPasswordSalt())`.
3. **Client `RPC_ClientHandshake(ZRpc rpc, bool needPassword, string serverPasswordSalt)`**
   ZNet:749: if needPassword → password dialog → `OnPasswordEntered` → `SendPeerInfo(rpc, pwd)`;
   else `SendPeerInfo(rpc)`.
4. **`SendPeerInfo(ZRpc rpc, string password = "")`** ZNet:783 → `Invoke("PeerInfo", pkg)`.
   PeerInfo `ZPackage` layout:
   ```
   long    GetUID()
   string  Version.CurrentVersion
   uint    36            // network version
   Vector3 m_referencePosition
   string  playerName
   IF server: string worldName, int seed, string seedName, long worldUid,
              int worldGenVersion, double m_netTime
   IF client: string passwordHash (HashPassword(pwd,salt) or ""),
              byte[] steamSessionTicket (ZSteamMatchmaking.RequestSessionTicket)
   ```
5. **`RPC_PeerInfo(ZRpc rpc, ZPackage pkg)`** ZNet:818 — the gate (delegate → inlining-proof).
   Reads uid/version/netVersion. **`if (num != 36)` → Error 3 (server) / ErrorVersion (client).**
   Server-side checks, in order, each `rpc.Invoke("Error", code)` on failure:
   - `IsAllowed(hostName, name)` → **Error 8** (blacklist/whitelist)
   - Steamworks: `ZSteamMatchmaking.VerifySessionTicket(ticket, peerID)` → **Error 8**
   - PlayFab: crossplay privilege / auth → **Error 10 / 5** (skipped when Steam-only, I6)
   - `GetNrOfPlayers() >= 10` → **Error 9** (full)
   - `m_serverPassword != passwordHash` → **Error 6** (wrong password)
   - `IsConnected(uid)` → **Error 7** (duplicate)
   On success: register steady-state RPCs; server sends its own `SendPeerInfo`, `VersionMatch`,
   PlayerList, AdminList; then **`m_zdoMan.AddPeer(peer)` + `m_routedRpc.AddPeer(peer)`**
   (ZNet:975-976) — the transition into steady-state ZDO replication (which registers
   `"ZDOData"` and `"RoutedRPC"`).

**Hook point (I5):** a Lumberjacks-fronted "server" must answer `ServerHandshake` with
`ClientHandshake(needPassword, salt)`, receive `PeerInfo`, satisfy the `num == 36` gate,
and reply with a server-shaped `PeerInfo` (world name/seed/seedName/uid/worldGenVersion/
netTime), then drive `AddPeer` to enter steady state. Error-code contract enumerated above.

---

## I0 gate status

Send / receive / ownership rows are mapped with citations and carry **no TODO/unknown** —
gate satisfied. Routed-RPC and handshake funnels are additionally mapped. Every hook point
is named with its prefix/postfix/transpiler intent and its inlining exposure. Ready for I1
(interception reachability) and I6 (Steam-only), which are the two unblocked next rungs.
