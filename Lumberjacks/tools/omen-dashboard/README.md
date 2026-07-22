# OMEN live GCP dashboard

A loopback-only proxy that lets OMEN view the live GCP P7 surfaces — including admin and
dev telemetry — without publishing anything new from the VM.

## Start the tunnel first

The proxy reaches the Gateway through the SSH/IAP tunnel, **not** the public player port.
Nothing works until the tunnel is up:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\gateway-tunnel.ps1 -Action start   # local 14000 -> VM 127.0.0.1:4000
& C:\work\baseline\infra\gcp\p7\scripts\gateway-tunnel.ps1 -Action status  # healthy: true
```

`-Action watch` keeps it alive across network blips. Then, from this directory:

```powershell
docker compose up -d
Start-Process http://127.0.0.1:8080/community
```

## Surfaces

HTML views — `/community`, `/networksense`, `/events`, `/testing`, `/roadmap`, and
`/ops/boundary`.

`/ops/boundary` is the builder/hacker workbench for the append-only JSONL stream. It
now shows identity/auth/request rows plus ZDO queue movement: queued, polled,
acknowledged, consumer heartbeat/applied counters, window/recipient partitions,
per-stage durations, and recent rows.

Volunteer credential routes such as `/join/reissue` are deliberately not proxied
through this container. Open those through the normal public enrollment URL so Steam
callback URLs and one-time download posts stay on the player-facing origin.

Read-only telemetry and stats, all GET-only:

```text
/api/v0/telemetry/valheim | /cutover | /deployment
/live/transport | /live/sessions
/valheim/zdo-redirect/status[/<windowId>]
/api/v0/valheim/zdo-consumers/<windowId>
/valheim/zdo-injection/status[/<windowId>]
/ops/boundary/summary
```

`/roadmap` is served from the file mounted straight off this checkout, so it updates on
the next browser refresh after `npm run roadmap:render` — no Gateway or GCP redeploy.
Everything else is live from the VM.

## Verify it's live (no browser needed)

After the tunnel is up and `docker compose up -d`, confirm the proxy is serving **live** P7
telemetry — not a stale cache — straight from PowerShell:

```powershell
# 1. Tunnel reaches the live gateway (uptime/tick prove liveness):
Invoke-RestMethod http://127.0.0.1:14000/api/v0/telemetry/server | Format-List current_tick,uptime_seconds

# 2. Dashboard proxy forwards it (run twice — current_tick must advance):
Invoke-RestMethod http://127.0.0.1:8080/api/v0/telemetry/server | Select current_tick, @{n='policy';e={$_.replication.policy}}

# 3. community.html is served and wired to poll the API:
(Invoke-WebRequest http://127.0.0.1:8080/community -UseBasicParsing).Content -match '/api/v0/telemetry'  # -> True

# 4. Gameplay-event feed (empty until the mod producer is armed — see the plan's Increment 1):
Invoke-RestMethod http://127.0.0.1:8080/api/v0/telemetry/events | Select count, capacity, dropped_since_start

# 5. Boundary diagnostics: identity/auth/request/ZDO JSONL summary:
Invoke-RestMethod http://127.0.0.1:8080/ops/boundary/summary | Select rows, proxy_boundary_warnings, writer_dropped_rows

# 6. ZDO movement counters from the same JSONL stream:
(Invoke-RestMethod http://127.0.0.1:8080/ops/boundary/summary).zdo_totals
```

A `current_tick` that advances between calls in step 2 is the live-vs-stale proof. Then open
`http://127.0.0.1:8080/community`. Verified live against P7 (`gcp-p7`, mod 0.5.31) on 2026-07-21.

## Why the tunnel, and why widening the allowlist is not widening exposure

This proxy used to point at `8.231.129.249:42317`, the VM's **public** player endpoint.
That capped what it could ever show. Admin and dev surfaces are bound to the VM's loopback
deliberately (`infra/gcp/p7/docker-compose.yml` binds eventlog, progression and operatorapi
to `127.0.0.1`), so they are not on `:42317` at all — no allowlist change here could reach
them. Serving them over the public port would have meant *publishing* them, destroying the
isolation the compose file exists to preserve.

Pointing at the tunnel instead inverts that. The proxy still binds `127.0.0.1` only; the
tunnel authenticates with the operator's own IAM identity through IAP; the VM publishes
nothing it did not publish before. The allowlist got wider, the attack surface did not.

Deliberately still not forwarded, though the tunnel could now reach them:

- `/api/v0/enrollment` — lists SteamIDs. That is a roster, not a statistic.
- `/valheim/handshake/*` — admission control, not observation.
- `/join/*` — the volunteer credential flow.
- every non-GET method — this is a viewing surface.

`/ops/boundary` is included because it is a read-only summary of the append-only boundary
event stream. The Gateway also refuses requests with a public `X-Forwarded-For` claim,
so this route works over the operator tunnel but not through the public TLS proxy.

Note that several forwarded prefixes also contain mutating routes (`/valheim/zdo-redirect`
holds `/reset` and `/compact`; `/valheim/zdo-injection` holds `/stage` and `/reset`). The
`limit_except GET` guard on each location is what stops a prefix match from dragging them
in — keep it if you add routes.

The Operator API is a **separate service** on the VM's `127.0.0.1:4004`, not the Gateway,
so it is not reachable through this tunnel. It needs its own forward and
`LUMBERJACKS_ADMIN_KEY`; see the admin-console section of `infra/gcp/p7/README.md`.
