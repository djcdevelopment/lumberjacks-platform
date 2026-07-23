# P7 Valheim player-motion canary

This is the small, reversible two-player test for the first real Lumberjacks motion
lane. It proves transport before presentation. Native Valheim remains connected for
the entire canary and `FULL NETCODE` remains `NO`.

## Preconditions

- Both clients carry the same promoted mod release. A Gateway-only hotfix may have
  a different image release ID, but its baked admitted-mod release must match the clients.
- TCP `42317` and UDP `4005` are published by the Gateway host; both GCP firewall
  rules use `lumberjacks_player_source_ranges`.
- Each player has their own enrollment ID and access key in the local BepInEx config.
- The community view is open at `http://8.231.129.249:42317/community` (or the OMEN
  Docker dashboard URL that proxies that surface).
- Both clients show `Native Valheim [x]`, `LJ ZDO [x]`, and `LJ Motion [x]` before
  changing presentation.

The motion defaults are:

```ini
[Lumberjacks]
lumberjacksMotionEnabled = true
lumberjacksMotionApplyEnabled = false
lumberjacksMotionSendHz = 20
lumberjacksMotionSmoothing = 18
lumberjacksMotionFreshSeconds = 0.5
```

## Run

| Step | Operator action | In-game prediction | Dashboard prediction |
|---|---|---|---|
| 1 | Join one enrolled client and leave `APPLY` off. | WebSocket and UDP become checked; sent rises; received stays near zero with no peer. | Motion trace shows received over UDP and zero relays. |
| 2 | Join the second enrolled client; stand in the same region. | Both sent counters rise; each received counter rises; applied remains zero. Native presentation is unchanged. | UDP received and relayed counts climb continuously; WS fallback remains zero. |
| 3 | Walk, sprint, and stutter-step on one client while watching the other. | Gameplay still exhibits native Valheim presentation because `APPLY` is off. | Motion counts climb at roughly the configured sampling rate; drops should remain flat except occasional stale/reordered datagrams. |
| 4 | Turn `UDP` off on the moving client only. | The client remains connected; sent continues through WebSocket. | `received_websocket` rises. Delivery to a UDP-ready observer still raises `relayed_udp`. |
| 5 | Turn `UDP` off on the observer too. | Both remain connected and counters continue. | Relay shifts to `relayed_websocket`; this is the deliberate fallback proof. |
| 6 | Restore UDP on both, then turn `APPLY` on only for the observer. Repeat sprint and stutter-step. | Observer's applied counter rises. Fresh snapshots smooth toward measured positions with no velocity prediction; stale snapshots yield to native after 0.5 s. | UDP receive/relay continues; apply is a local client decision and is not claimed by Gateway counters. |
| 7 | Turn the moving client's WebSocket off. | Lumberjacks motion stops and native Valheim remains connected. The observer falls back to native presentation after the freshness window. | Motion receive/relay counters stop advancing for that source; no disconnect from Valheim is expected. |

Record the before/after motion trace text and each client truth strip. Qualitative notes
should distinguish continuous sprint, rapid direction changes, stutter-step, portal,
and a correction over 30 metres.

## Pass and rollback

Pass the transport slice when UDP carries motion for two enrolled players, disabling
UDP moves the same frame to WebSocket without disconnecting, and disabling the
Lumberjacks WebSocket leaves native Valheim playable. The presentation slice is a
separate pass based on the observer's A/B notes.

Immediate rollback is the in-game `APPLY` button. A stronger rollback is `WebSocket`
off, which stops the motion runner while preserving native Valheim and the existing
HTTP/JSON ZDO consumer. Restarting the client restores the configured defaults.

If UDP never advances, verify the effective host binding and firewall without printing
the environment file:

```powershell
gcloud compute firewall-rules describe comfy-lumberjacks-p7-player-udp
gcloud compute ssh comfy-lumberjacks-p7 --zone us-west1-b --tunnel-through-iap `
  --command "sudo docker compose -f /opt/comfy/infra/gcp/p7/docker-compose.yml ps; sudo ss -lunp | grep ':4005'"
```

If WebSocket fallback never advances, inspect the client's BepInEx log and Gateway
container log for the release-admission or enrollment decision; never paste access
keys into Discord or a public issue.

## Public ingress proof before asking two people to join

The transport proof has two separate gates:

1. One enrolled TLS/WebSocket session must receive `session_started` with
   `valheim_motion_available=true`, then its token-prefixed 50-byte UDP fixture must
   increment `received` and `received_udp` with no drop counter increase.
2. A *different enrolled recipient* in the same region must make `relayed_udp` or
   `relayed_websocket` advance. Two sockets using one enrollment are deliberately not
   a substitute: the relay suppresses same-recipient echo.

On the Gateway, confirm the first gate without exposing credentials:

```powershell
Invoke-RestMethod https://comfy-p7.duckdns.org/live/valheim-motion
```

The first gate was exercised on 2026-07-22 against the public TLS endpoint:
`valheim_motion_available=true`, UDP port `4005`, one 50-byte packet received over
UDP, and zero invalid, unauthorized, or stale drops. Relay remained zero because a
second distinct enrolled recipient was not connected.

If an enrolled `/api/v0/valheim/enrollment/me` request succeeds but
`valheim_motion_available` is false, inspect middleware ordering before changing
credentials. `UseWebSockets()` must run before `ValheimClientAccessMiddleware`:
ASP.NET does not populate `HttpContext.WebSockets.IsWebSocketRequest` until the
WebSocket feature is installed. Putting the identity gate first makes an upgrade
look like an ungated ordinary GET and leaves the session without an enrollment
principal.
