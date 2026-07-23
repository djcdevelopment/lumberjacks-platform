# Alpha transport truth-strip runbook

Use this runbook to answer one question with direct evidence: **which network plane is carrying
this Valheim session right now?**

## Architectural baseline

The current P7 result is a Lumberjacks ZDO-delivery cutover, not a full netcode cutover.

| Plane | Current owner / path |
|---|---|
| Steam login, native peer, simulation | Valheim |
| Candidate relevance / peer sync list | Valheim |
| Declared ZDO ordering, durable queue, delivery, ACK | Lumberjacks HTTP/JSON |
| ZDO receive/application semantics | ComfyNetworkSense through Valheim `RPC_ZDOData` |
| Non-ZDO RPCs and movement presentation/interpolation | Valheim |
| Lumberjacks reliable WebSocket lane | Enrolled alpha motion session and binary fallback; observe-first unless local motion apply is enabled |
| Lumberjacks UDP datagram lane | Enrolled alpha motion datagram lane; token-bound and observe-first unless local motion apply is enabled |
| MCP/Raven | Local builder sidecar on `127.0.0.1:8720`; not gameplay transport |

## Start observation

On OMEN, start the Gateway tunnel and loopback dashboard:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\gateway-tunnel.ps1 -Action start
docker compose -f C:\work\baseline\Lumberjacks\tools\omen-dashboard\docker-compose.yml up -d
Start-Process http://127.0.0.1:8080/community
```

Join P7 with the matching ComfyNetworkSense release. As of `m14-hudtoggle-20260723-r1`, the
transport strip starts collapsed so it does not cover lower-resolution menu buttons. Use the side
`NET SHOW` tab to expand it after joining; `NET HIDE` collapses it again. As of
`m15-hudrecover-20260723-r1`, the side tab remains visible even when an older local config has
`transportStripEnabled = false`; clicking `NET SHOW` re-enables the strip for that process. The tab
side is controlled by `[HUD] transportStripToggleSide = Right|Left`.

The strip should show native Valheim and LJ ZDO active, HTTP/JSON active, WebSocket/UDP motion status
when the enrolled motion lane is connected, and MCP active only where the local helper is running.

## Updating a tester machine

For an already-enrolled alpha tester, use the config-preserving update download:

```text
https://comfy-p7.duckdns.org/join/update
```

Sign in with the same Steam account, download the update zip, extract it, and run
`Install-LumberjacksMod.ps1`. The installer replaces DLL/mod files but restores the existing
ComfyNetworkSense config so the enrollment credential is not rotated or erased.

## Fault sequence and predicted evidence

| Action | In-game prediction | Dashboard prediction |
|---|---|---|
| Join | side tab visible; after `NET SHOW`, `Native Valheim [x]`, then `LJ ZDO [x]`; state becomes `polling`/`draining` | player appears; ZDO receipts/apply/ACK advance |
| Click `HTTP [x]` | HTTP/JSON and LJ ZDO go off; native Valheim stays connected; state becomes `fault-paused` | `transport_control_changed: lumberjacks_http=off via native_valheim_rpc`; queue/pending grows or world delivery stops advancing |
| Move while HTTP is off | local movement may continue; remote/world state can become stale | no client poll/ACK progress; native peer remains visible |
| Click `HTTP [ ]` | HTTP/JSON and LJ ZDO return; consumer drains backlog | matching `lumberjacks_http=on` trace; poll/apply/ACK resume |
| Click `MCP` | Raven actions stop/start independently; gameplay path unchanged | local JSONL control row; server trace may show the control if native peer is still connected |
| Click `DISCONNECT` | control is relayed, then client returns to menu | `native_valheim_peer=off via disconnect_requested`, followed by peer-count drop |

The first HTTP-off experiment may falsify the predicted shape. Preserve that result; the point of
the switch is to reveal coupling, not to make the table come true.

## Local evidence

The client appends switch rows under:

```text
BepInEx\config\comfy-network-sense\transport-controls.jsonl
```

Rows use snake_case fields: `schema_version`, `timestamp_utc`, `session_id`, `event_type`,
`component`, `enabled`, and `observed_path`.

## Recovery

The switches are process-local and reset to enabled whenever the mod starts. If UI input or a
fault run behaves unexpectedly, restart Valheim. Do not convert these alpha fault controls into
durable config until a test specifically needs restart persistence.
