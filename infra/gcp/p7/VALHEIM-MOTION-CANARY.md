# P7 Valheim player-motion canary

This is the small, reversible two-player test for the first real Lumberjacks motion
lane. It proves transport before presentation. Native Valheim remains connected for
the entire canary and `FULL NETCODE` remains `NO`.

## Preconditions

- P7 Gateway and both clients carry the same promoted release identity.
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
