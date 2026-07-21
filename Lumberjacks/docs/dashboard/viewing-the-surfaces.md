# Viewing the Telemetry & Admin Surfaces (local runbook)

How to run the stack locally and view each surface — both the **player / community**
view and the **dev-admin operator** view. Every command below is verified against the
Docker Compose stack.

## The three "dashboards"

| Surface | Where | Who it's for |
|---|---|---|
| **Strategy ledger** — `docs/dashboard/index.html` | static file, open directly in a browser (no server) | you, planning — §03 signal grades, backlog, changelog. *Not* a live view. |
| **Community / telemetry pages** | Gateway **`:4000`** | players + community — the live "everyone is an alpha tester" surface |
| **Admin-web operator console** | Vite **`:5173`** → Operator API **`:4004`** | you, dev admin — full server overview + management |

## 1. Start the backend stack

Builds in-container, so the local .NET-SDK version doesn't matter (repo targets net9.0):

```bash
docker compose -f infra/docker/docker-compose.yml up --build -d
```

Services and ports:

| Service | Port | Role |
|---|---|---|
| postgres | 5435 | world + progression + event store |
| **gateway** | **4000** (HTTP), 4005/udp | unified host: simulation, tick loop, WS/UDP, **all community pages + v0 telemetry API** |
| eventlog | 4002 | authoritative event log (full events, with actor) |
| progression | 4003 | challenge / guild / reward engine (separate process — see D-19) |
| operatorapi | 4004 | admin API — proxies/fans out to the above |

Verify it's up:

```bash
curl -s http://localhost:4000/api/v0/telemetry/server        # tick counter, uptime, replication config
docker compose -f infra/docker/docker-compose.yml ps         # all services "running"
```

## 2. Player / community surface (Gateway `:4000`)

Open in a browser — no login, served straight from the game Gateway. All data is
**anonymized and aggregated** (no player id/name/position ever):

| URL | What it is |
|---|---|
| `http://localhost:4000/roadmap` | **Valheim volunteer roadmap** — living milestone gates, validated proof, known no-go findings, and append-only commit notes |
| `http://localhost:4000/networksense` | **G3 NetworkSense HUD** — glanceable overlay: tick health vs 50 ms budget, sessions, delivery mix |
| `http://localhost:4000/events` | **G4 Gameplay Event feed** — live, anonymized event stream (structure/inventory/region/interest events) |
| `http://localhost:4000/community` | **Live Community View** — server overview: uptime, tick perf, sessions, delivery, regions |
| `http://localhost:4000/testing` | **G5 Local Testing Tools** — scenario cards (simulated) |

Raw API behind them (same-origin, `GET` from any origin, DB-less):
`http://localhost:4000/api/v0/telemetry/{server,tick,sessions,delivery,regions,events,valheim,cutover}`

`/valheim` reports the sanitized Valheim heartbeat. `/cutover` reports the declared
Lumberjacks mode (`native`, `mirrored`, or `lumberjacks-primary`) and coverage counters.
An absent coverage value means the mod is reporting status but has not yet installed the
full-traffic coverage probe; it must not be read as 100% cutover.
`/api/v0/valheim/enrollment/{manifestId}` exposes the read-only enrollment manifest and
the same coverage gate. It advertises progressive transport; it does not authorize
Lumberjacks-primary mode by itself.

The pages poll every 2 s. If a poll fails they show a "reconnecting / stale" chip and keep
the last good values — they never fabricate data.

## 2a. Live GCP P7 deployment

The trusted-pilot Gateway is available directly at `8.231.129.249:42317`. These URLs
show the deployed GCP data, not local images or a local Docker simulation:

```text
http://8.231.129.249:42317/community
http://8.231.129.249:42317/networksense
http://8.231.129.249:42317/events
http://8.231.129.249:42317/testing
```

On OMEN, the loopback-only dashboard proxy exposes the page at
`http://127.0.0.1:8080/roadmap`. It mounts the generated local
`src/Game.Gateway/Community/roadmap.html`, so a browser refresh sees every committed
roadmap update without waiting for a GCP deployment. Its sibling dashboard routes
proxy live GCP `:42317` data. The source also opens directly without a server and
remains useful while GCP is between deployments.

The Gateway build now includes `http://8.231.129.249:42317/roadmap` as well; that
direct GCP URL becomes available with the next Gateway deployment. The OMEN-mounted
page above is available immediately and does not depend on that deployment.

Verify the target and cutover state before a session:

```powershell
$gateway = 'http://8.231.129.249:42317'
Invoke-RestMethod "$gateway/health"
Invoke-RestMethod "$gateway/api/v0/telemetry/deployment"
Invoke-RestMethod "$gateway/api/v0/telemetry/cutover" |
  ConvertTo-Json -Depth 20
```

Expected deployment identity is `environment=gcp-p7`. The `/cutover` response is the
authority source of truth. A passing single-client primary window requires 100%
coverage, zero native-only/fallback traffic, equal receipts and acknowledgements,
zero pending, and `complete=true` in one coherent sample.

The enrolled ComfyNetworkSense client also uses this direct endpoint for authoritative
polling, acknowledgements, and telemetry. Control paths require its per-enrollment
credential; the dashboard GET surfaces currently do not. The endpoint is plain HTTP
and is intended only for the limited volunteer pilot until TLS, rate limiting, and
dashboard access control are added.

No OMEN tunnel or standalone forwarding process is required for gameplay. The legacy
`127.0.0.1:14000` SSH/IAP tunnel remains an operator fallback if the public pilot port
is intentionally closed. Primary mode is fail-closed: a lost Gateway route leaves
work durable and unacknowledged; it is not evidence of a successful native fallback.

For the admin console, forward Operator API through IAP and keep the Vite app local:

```bash
gcloud compute ssh comfy-lumberjacks-p7 --project lumberjacks-exp-20260711-djc \
  --zone us-west1-b --tunnel-through-iap -- -L 14004:127.0.0.1:4004
API_TARGET=http://127.0.0.1:14004 npm run dev -w @game/admin-web
```

## 3. Dev-admin operator console (`:5173`)

A separate Vite app (not in Compose). Start it alongside the stack:

```bash
cd clients/admin-web
npm install
npm run dev          # → http://localhost:5173, proxies /api/* to the Operator API on :4004
```

It surfaces player lookup, guild inspection, challenge setup, tick diagnostics,
transport/session live metrics, achievements history, and region create/delete — via
`operatorapi` (`:4004`), which fans out to gateway/eventlog/progression.

### The privacy split (why the two views differ)

- **Player view** (`/events`, Gateway): each event is `type + region + timestamp + non-identifying detail + provenance`. **No actor.** Optionally delayed (`Telemetry__PublicEventsDelaySeconds`, default `0`/live locally; set `30` for a public deployment).
- **Admin view** (console event log, `operatorapi → eventlog /api/events`): the **full** authoritative record, **including `actor_id`**.

Same events, two trust levels — this is the telemetry privacy invariant made concrete.

## Seeding activity (so the pages aren't empty)

The HTTP wire format is **snake_case** (`region_id`, not `regionId`). A few `curl`s
generate real events that show up on `/events`:

```bash
# structure_placed  (detail = structure type)
curl -s -X POST http://localhost:4000/structures/place -H "Content-Type: application/json" \
  -d '{"region_id":"region-spawn","player_id":"seed-demo","structure_type":"cabin","position":{"x":10,"y":0,"z":10},"rotation":0,"tags":["demo"]}'

# region_activated  (create a region)
curl -s -X POST http://localhost:4000/regions -H "Content-Type: application/json" \
  -d '{"id":"region-demo","name":"Demo Meadow","bounds_min":{"x":-100,"y":-10,"z":-100},"bounds_max":{"x":100,"y":100,"z":100},"tick_rate":20}'

# region_deactivated
curl -s -X DELETE http://localhost:4000/regions/region-demo

curl -s http://localhost:4000/api/v0/telemetry/events        # see them, newest-first
```

To generate **sessions, RTT, delivery, and `player_entered_region`** you need a real
WS/UDP client — the `tools/synthclient` harness (`SYNTH_TARGET`, `SYNTH_MODE=json|binary|udp`,
`SYNTH_CLIENTS`, `SYNTH_DURATION_S`). It targets net9.0, so run it via a `dotnet/sdk:9.0`
container attached to the Compose network (`--network` the compose net, target
`ws://gateway:4000`). Those sessions then populate `/community`, `/networksense`, and the
`/sessions` + `/delivery` (incl. the `udp_packets.reject_rate`) endpoints.

## Stopping

```bash
docker compose -f infra/docker/docker-compose.yml down          # keep the DB volume
docker compose -f infra/docker/docker-compose.yml down -v       # also wipe postgres data
```

## Notes / gotchas

- **No graphical game client.** The Godot client was dropped (PR #5); "in game as a player"
  today means a browser tab (the `/networksense` page is *styled* as an overlay but isn't
  embedded in a client) or simulated players via `synthclient`. A Valheim bridge exists
  (`Valheim*` endpoints on the Gateway) if that's the intended client.
- **No auth yet** (backlog D-09) — the Operator API / admin console isn't access-controlled;
  "admin" is by-network, not by-login.
- The community pages are **DB-less** and degrade gracefully, so `/community` etc. work even
  before Postgres is fully warm.
