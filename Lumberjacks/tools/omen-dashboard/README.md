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

HTML views — `/community`, `/networksense`, `/events`, `/testing`, and `/roadmap`.

Read-only telemetry and stats, all GET-only:

```text
/api/v0/telemetry/valheim | /cutover | /deployment
/live/transport | /live/sessions
/valheim/zdo-redirect/status[/<windowId>]
/api/v0/valheim/zdo-consumers/<windowId>
/valheim/zdo-injection/status[/<windowId>]
```

`/roadmap` is served from the file mounted straight off this checkout, so it updates on
the next browser refresh after `npm run roadmap:render` — no Gateway or GCP redeploy.
Everything else is live from the VM.

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

Note that several forwarded prefixes also contain mutating routes (`/valheim/zdo-redirect`
holds `/reset` and `/compact`; `/valheim/zdo-injection` holds `/stage` and `/reset`). The
`limit_except GET` guard on each location is what stops a prefix match from dragging them
in — keep it if you add routes.

The Operator API is a **separate service** on the VM's `127.0.0.1:4004`, not the Gateway,
so it is not reachable through this tunnel. It needs its own forward and
`LUMBERJACKS_ADMIN_KEY`; see the admin-console section of `infra/gcp/p7/README.md`.
