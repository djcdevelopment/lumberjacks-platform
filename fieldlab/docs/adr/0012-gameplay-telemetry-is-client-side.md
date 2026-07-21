# ADR 0012 — Gameplay telemetry is captured client-side and relayed to the server by routed RPC

- **Status:** Accepted (2026-07-21)
- **Rung:** Community telemetry surface (G4 gameplay events); armed as normal-play behaviour on P7

## Context

The community-telemetry surface needs first-class **gameplay events** (`first_hit`, `killing_blow`,
`weapon_used`) flowing into the public dashboard feed. The natural instinct — and the first build
(`6dc6031`) — put a `GameplayEventProducer` **server-side**: hook `Character.RPC_Damage`/`Character.Damage`
on the dedicated server, classify, and POST to the gateway ingress (`/valheim/events`) on the private
plane. That kept the gateway auth trivial (the server already reaches the gateway as the private plane,
which holds the `Producer` capability).

It does not work, and the reason is domain, not code. On a Valheim dedicated server, the creatures a
connected player fights are **owned and simulated by that player's client** — combat damage and death
are processed client-side, and the server's combat hooks fire on nothing. This was proven live: both
server hooks applied cleanly (success-logged in the BepInEx log), yet a player's boar kill produced
**zero** hook invocations. The working precedent had been in hand the whole time — the pruned quest
vertical slice (comfy backup, `handoffs/comfy-control-surface/`, deleted from baseline in `d75ffb2`)
ran its trigger hooks **client-side** with "only local-player actions count," and worked for weeks.

A client cannot reach the gateway directly: `gateway:4000` is Docker-internal / VM-loopback (only
player routes are public), and a public client is denied the `Producer` capability regardless. So a
direct client POST is both unreachable and unauthorized.

## Decision

**Gameplay telemetry is captured on the client and relayed to the server over a ComfyNetworkSense
routed RPC; only the server POSTs to the gateway.**

- **Client** (where it owns the creature): `Character.Damage`/`RPC_Damage` postfixes record the last
  player-attributed hit; `Character.OnDeath` is the kill signal (`IsDead()` is false at a damage
  postfix — death is processed after damage). On a killing blow the client sends the event via
  `ZRoutedRpc.InvokeRoutedRPC(0L, "ComfyNetworkSense_GameplayEvent", …)` — a client's RouteRPC always
  reaches the server, which handles a `target==0` packet (`GetServerPeerID` is **not** a public
  `ZRoutedRpc` member — verified against `assembly_valheim`).
- **Server** (the sole peer on the gateway's private plane): the RPC handler, gated on
  `ZNet.IsServer()`, POSTs the event to `/valheim/events`. The ingress is `Producer`-gated in
  `ValheimAccessPolicy` — only the private-plane server may post, so a public client cannot spoof
  kills. The public feed projection strips actor identity; the durable EventLog keeps it.
- The pure decision logic (`GameplayEventClassifier`: first-hit vs. killing-blow, per-creature dedup)
  stays Unity-free and unit-tested; the Unity/Valheim reads live in the producer/patches.

Same DLL on both ends (the mod's `PluginOutputPath` copies the client build; `deploy-network-sense`
scps the server one). The hook naturally fires only where a creature is owned; only the server POSTs.
Behind `gameplayEventProducerEnabled` (default false).

## Consequences

- **Capture is correct and complete** for a connected player's kills — and captures *all* players'
  kills, not just an operator's, which a client-local file approach (the original quest slice) could
  not. The gateway ingress + public feed (built server-first) were right all along; only the
  producer's vantage was wrong — this was a reroute, not a teardown.
- **Auth stays clean:** the spoofing surface is off the public net. A public client physically cannot
  post gameplay events; it can only ask the server (via the game connection it already holds) to.
- **Cost:** the mod must be current on the client too (it is — the HUD runs there), so a wire-format
  change means a paired client+server deploy and a client relaunch. Accepted for now.
- **Related:** supersedes the server-side approach in the approved plan; the trailing quest-evaluator
  (Increment 4+) restores the pruned comfy quest slice as a *consumer* of these events. Reinforces
  ADR 0009 (a check that reads its own output is not a check) — the fix came from reading the backup,
  not theorizing. Memory: `gameplay-capture-is-client-side`.
