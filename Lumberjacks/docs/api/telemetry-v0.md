# Public Telemetry API v0 Reference

The Public Telemetry API v0 (part of Phase 3 of the community telemetry strategy) provides a read-only, versioned surface exposing Gateway tick, replication, session, delivery, and gameplay-event metrics to the public internet. This API is designed to empower community members to build their own custom dashboards, overlays, bots, and analytics tools, ensuring server telemetry is not restricted solely to the operator team.

> **⚠️ Stability Notice: EXPLICITLY UNSTABLE**
> This API is currently at `api_version` "v0" and is explicitly marked as "unstable". This status is reflected on every response both as JSON fields (`"api_version": "v0"`, `"stability": "unstable"`) and via an `X-API-Stability: unstable` HTTP response header. The shape of every endpoint may change without notice until the schema settles. Do not build production integrations against this API yet.

## Privacy

This API strictly adheres to a hard privacy rule: **no player IDs, names, or positions appear in ANY v0 response, ever.** Every endpoint returns only aggregated metrics or static/world-level facts. To ensure absolute compliance, this rule is enforced by an automated test suite that serializes every endpoint's response and asserts that connected players' identifiers never appear anywhere in the output. The gameplay-event feed (`GET /api/v0/telemetry/events`) — the one endpoint that surfaces discrete activity rather than aggregates — carries a second guard: its events are anonymized (no actor at all) and an optional exposure **delay** (`Telemetry:PublicEventsDelaySeconds`) can hold the public feed behind the "aggregated or delayed" line. See that endpoint below.

## CORS Policy

Unlike the rest of the Gateway's explicitly origin-allowlisted CORS policy, the telemetry endpoints are designed as a deliberate public surface. All `/api/v0/*` endpoints (as well as the `/community` page) allow `GET` requests from any origin (`Access-Control-Allow-Origin: *`) with no credentials required. 

## Endpoints

### GET /api/v0/telemetry/server

Returns server identity and uptime along with the active replication policy and its configured knobs (including `policy`, `near_radius`, `mid_radius`, `mid_tick_interval`, the effective post-auto-resolve `send_workers` count, `deadline_ms`, and `adaptive`). Note that `tick_rate_hz` is currently a fixed 20 (the simulation's fixed tick rate, which is not yet configurable).

```json
{
  "api_version": "v0",
  "stability": "unstable",
  "current_tick": 986,
  "tick_rate_hz": 20,
  "uptime_seconds": 49,
  "started_at": "2026-07-12T16:39:00.1462837+00:00",
  "replication": {
    "policy": "tiered",
    "near_radius": 100,
    "mid_radius": 300,
    "mid_tick_interval": 4,
    "send_workers": 1,
    "deadline_ms": 0,
    "adaptive": false
  }
}
```

### GET /api/v0/telemetry/tick

Returns timing and replication data for the most recent ~5-second tick-timing window. This includes p50/p99/max durations per phase (total, interval, sim, hash, broadcast, interest, send, housekeeping), the overrun count against the 50ms tick budget, and replication counters (sent/culled entity updates, effective `send_workers`, `deadline_aborts`, and `degraded_ticks`) for that window. Clients should note that `tick_timing` will be `null` (which is not an error) until the first ~5-second window closes after server startup—a legitimate "warming up" state that integrations must handle gracefully.

```json
{
  "api_version": "v0",
  "stability": "unstable",
  "tick_timing": {
    "window_end_tick": 900,
    "sample_count": 100,
    "overruns": 0,
    "budget_ms": 50,
    "phases": {
      "total": { "p50_ms": 0.0133, "p99_ms": 0.0877, "max_ms": 0.0905 },
      "interval": { "p50_ms": 49.9483, "p99_ms": 55.4382, "max_ms": 55.5047 },
      "sim": { "p50_ms": 0.0051, "p99_ms": 0.0411, "max_ms": 0.065 },
      "hash": { "p50_ms": 0.0077, "p99_ms": 0.0598, "max_ms": 0.0621 },
      "broadcast": { "p50_ms": 0, "p99_ms": 0, "max_ms": 0.0001 },
      "interest": { "p50_ms": 0, "p99_ms": 0, "max_ms": 0 },
      "send": { "p50_ms": 0, "p99_ms": 0, "max_ms": 0 },
      "housekeeping": { "p50_ms": 0, "p99_ms": 0.0176, "max_ms": 0.0701 }
    },
    "replication": {
      "policy": "tiered",
      "sent": 0,
      "culled": 0,
      "send_workers": 1,
      "deadline_aborts": 0,
      "degraded_ticks": 0
    },
    "captured_at": "2026-07-12T16:39:45.1420153+00:00"
  }
}
```

### GET /api/v0/telemetry/sessions

Returns session aggregates only: the total connected session count, broken down by wire protocol (json/binary) and by region ID. It never includes a session ID, player ID, or per-session breakdown. *Note: The captured JSON sample below reflects an idle local server with no connected clients; on an active server, `by_protocol` and `by_region` would contain string-keyed counts (e.g., `"by_protocol": {"json": 30, "binary": 12}`).*

```json
{
  "api_version": "v0",
  "stability": "unstable",
  "total": 0,
  "by_protocol": {},
  "by_region": {}
}
```

### GET /api/v0/telemetry/delivery

Returns cumulative networking counters since server start. The `delivery` map contains message delivery-path outcomes (e.g., udp, binary_ws, json_ws), where keys appear once at least one message has taken that path. The `transitions` map counts session lifecycle events (created, resumed, detached).

The `udp_packets` block tallies the outcome of every inbound UDP packet the server processed (plus outbound send failures), with a derived `reject_rate`:

- `received` — valid packet mapped to a known session.
- `invalid` — packet too small to be a valid framed datagram.
- `unknown_session` — packet carried a bad/unknown UDP token (dropped).
- `send_error` — an outbound datagram send threw.
- `total` — sum of the four outcomes above.
- `reject_rate` — `(invalid + unknown_session + send_error) / total`, guarded so 0 packets yields `0.0` (not `NaN`).

> **Honesty note:** `reject_rate` is a **server-side reject/error rate over packets the server actually received** — it is **NOT** network packet loss. A UDP server cannot observe datagrams that never arrived, so true packet loss is unobservable server-side; it is measured client-side and reported as `loss_rate` by the synthclient probe (see `tools/synthclient`).

*Note: As with the sessions endpoint, the captured JSON sample below is from an idle local run where no clients have connected since startup, hence the empty `delivery`/`transitions` objects and all-zero `udp_packets`.*

```json
{
  "api_version": "v0",
  "stability": "unstable",
  "delivery": {},
  "transitions": {},
  "udp_packets": {
    "received": 0,
    "invalid": 0,
    "unknown_session": 0,
    "send_error": 0,
    "total": 0,
    "reject_rate": 0.0
  }
}
```

### GET /api/v0/telemetry/regions

Returns static and world-level facts about active regions, including their ID, name, live `player_count`, spatial bounds (min/max for x, y, and z), and `tick_rate`. This is safe to expose publicly as it contains strictly world-level data and no per-player information.

```json
{
  "api_version": "v0",
  "stability": "unstable",
  "regions": [
    {
      "id": "region-spawn",
      "name": "Spawn Island",
      "player_count": 0,
      "bounds": {
        "min": { "x": -500, "y": -10, "z": -500 },
        "max": { "x": 500, "y": 200, "z": 500 }
      },
      "tick_rate": 20
    }
  ]
}
```

### GET /api/v0/telemetry/events

Returns a DB-less feed of recent, anonymized gameplay events (currently `structure_placed`, `item_picked_up`, `item_stored`, `interest_subscription_changed`, `region_activated`, `region_deactivated`, and `player_entered_region`) captured from the in-process gameplay handlers to support goal G4 (gameplay events as first-class telemetry). The capture layer is a bounded ring buffer (capacity 200, newest-first) that enforces a strict allow-list of public-safe event types, refusing identity, social, and connection events by construction. The response tracks ring `capacity`, the `count` of returned events, and a `dropped_since_start` honesty counter for events that have aged out of the ring since server start. Each event carries a `provenance` tier (server-derived events are `observed`; the other tiers are `reconstructed`, `verified`, and `community_awarded`). Only event types that have a live producer appear — today that is the seven listed above; the feed grows as more producers are added.

The feed is doubly privacy-guarded. First, events carry no actor data: there is no player ID, name, or position — only the event type, region, timestamp, provenance, and a non-identifying `detail` category. Second, an exposure **delay** lets the feed satisfy the strategy's rule that unauthenticated public feeds be aggregated or delayed: when `Telemetry:PublicEventsDelaySeconds` (env `Telemetry__PublicEventsDelaySeconds`) is positive, the endpoint returns only events at least that many seconds old. The repository currently ships `0` (live, no delay) for development and building; **public or unauthenticated deployments should set a delay (e.g. `30`).** The returned `delay_seconds` field reports the effective delay so clients know whether the feed is live or intentionally behind.

```json
{
  "api_version": "v0",
  "stability": "unstable",
  "events": [
    { "event_id": "evt-000004", "event_type": "interest_subscription_changed", "occurred_at": "2026-07-13T11:58:12.4+00:00", "region_id": "region-spawn", "detail": "+2/-1", "provenance": "observed" },
    { "event_id": "evt-000003", "event_type": "item_stored", "occurred_at": "2026-07-13T11:57:40.1+00:00", "region_id": "region-spawn", "detail": "wood", "provenance": "observed" },
    { "event_id": "evt-000002", "event_type": "item_picked_up", "occurred_at": "2026-07-13T11:57:05.9+00:00", "region_id": "region-spawn", "detail": "stone", "provenance": "observed" },
    { "event_id": "evt-000001", "event_type": "structure_placed", "occurred_at": "2026-07-13T11:56:30.0+00:00", "region_id": "region-spawn", "detail": "wall", "provenance": "observed" }
  ],
  "count": 4,
  "capacity": 200,
  "dropped_since_start": 0,
  "delay_seconds": 0
}
```

### GET /api/v0/telemetry/valheim

Returns the latest sanitized heartbeat from the ComfyNetworkSense dedicated-server mod.
The response is aggregate-only: it contains no Steam IDs, player names, positions, or
object identifiers. `stale` is true until a heartbeat arrives and again after 15 seconds
without one. Heartbeat ingestion is `POST /valheim/telemetry/heartbeat` and is protected
by `X-Lumberjacks-Telemetry-Key` when `VALHEIM_TELEMETRY_KEY` is configured on the Gateway.

```json
{
  "stability": "unstable",
  "stale": false,
  "last_seen": "2026-07-13T12:00:19.4942635+00:00",
  "heartbeat": {
    "mod_version": "0.5.24",
    "server_role": "dedicated",
    "server_state": "ready",
    "peer_count": 0,
    "sample_timestamp_utc": "2026-07-13T12:00:19.4930280Z"
  }
}
```

### Authoritative ZDO cutover telemetry

`GET /api/v0/telemetry/cutover` includes `authoritative_window`, and
`GET /api/v0/valheim/zdo-consumers/{windowId}` exposes the consumer-only view.
`applied` counts envelopes whose exact owner/data revisions were observed after
`RPC_ZDOData`; `superseded` counts obsolete envelopes safely reconciled because the
same ZDO was already at a strictly newer owner or data revision. Promotion requires
`applied + superseded == distinct_seq`, all sequences acknowledged, a drained queue,
one fresh enrolled consumer, zero terminal rejects, and zero sequence gaps.
When `VALHEIM_ZDO_QUEUE_PATH` is configured, Gateway fsyncs length-prefixed receipt,
acknowledgement, and reset records before changing its in-memory view. Startup replays
that WAL and truncates an incomplete crash-torn tail. `durable_queue`,
`persistence_healthy`, and `wal_bytes` expose the active durability state. Transport
duplicates are suppressed by sequence and do not block promotion; saturation of the
bounded sequence index does.

## Live Community View

As part of Phase 4 of the telemetry strategy, a Live Community View is available at `GET /community`. This is a single, self-contained HTML page served directly by the Gateway that polls the API endpoints listed above every 2 seconds to provide a real-time, out-of-the-box telemetry dashboard for community members.

## Versioning

Future breaking changes to this telemetry surface will ship under new versioned paths (e.g., `/api/v1/...`). When this happens, the `/api/v0/...` endpoints will either be left in place or removed with notice. However, because the v0 API is explicitly unstable, it carries no deprecation SLA.

## Availability

Every v0 endpoint works without a database connection — they read `WorldState`, `TickMetrics`,
`SessionManager`, and the in-process `LumberjacksTelemetry` and `GameplayEventFeed` tallies,
none of which touch Postgres. The samples above were captured from exactly that: a `docker build --target gateway`
image run with no `ConnectionStrings__GameDb` set, verifying the Gateway's existing
graceful-degrade behavior (falls back to in-memory defaults, logs a warning) extends cleanly to
this API.

## Source

- Server / tick / delivery / regions / events: `src/Game.Simulation/Endpoints/TelemetryV0Endpoints.cs`
- Sessions (needs Gateway-only `SessionManager`): `src/Game.Gateway/Endpoints/TelemetryV0SessionsEndpoints.cs`
- Gameplay-event feed (in-process ring, allow-list, exposure delay): `src/Game.ServiceDefaults/GameplayEventFeed.cs`
- Shared envelope, CORS policy, `/api/v0/telemetry` route group: `src/Game.ServiceDefaults/PublicTelemetryV0.cs`
- Community view: `src/Game.Gateway/Endpoints/CommunityViewEndpoints.cs`, `src/Game.Gateway/Community/community.html`
- Events page (G4): `src/Game.Gateway/Endpoints/GameplayEventsEndpoints.cs`, `src/Game.Gateway/Community/events.html`
- Privacy tests: `tests/Game.Simulation.Tests/TelemetryV0EndpointsTests.cs`, `tests/Game.Gateway.Tests/TelemetryV0SessionsEndpointsTests.cs`
