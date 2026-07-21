# Deployment Strategy

## Goal

Get the backend stack reachable from the public internet so real players (friends with a test client, or simulated clients from different machines) can validate the platform under real-world network conditions.

## Architecture

```
                    Internet
                       │
              ┌────────┴────────┐
              │  Azure Container │
              │  Apps / VM       │
              │                  │
              │  ┌─────────┐    │
     ws:4000──┤  │ Gateway │    │
              │  │ (+ Sim) │    │
              │  └────┬────┘    │
              │       │ http    │
              │  ┌────┴────┐   ┌┴──────────┐
   http:4002──┤  │EventLog │   │ Postgres  │
              │  └─────────┘   │ (Flexible │
              │  ┌─────────┐   │  Server)  │
   http:4003──┤  │Progress │   └───────────┘
              │  └─────────┘    │
              │  ┌─────────┐    │
   http:4004──┤  │Operator │    │
              │  └─────────┘    │
              └─────────────────┘
```

> The Gateway runs the simulation in-process (WorldState, TickLoop, all handlers). There is no separate Simulation service to deploy.

## Option A: Azure Container Apps (recommended for first deploy)

**Why:** Managed container hosting, built-in ingress with TLS, scales to zero when idle (cheap during testing), supports WebSocket natively.

**Steps:**
1. Create Azure Container Registry (ACR)
2. Push Docker images: `docker tag game-gateway <acr>.azurecr.io/game-gateway && docker push`
3. Create Azure Container Apps Environment
4. Deploy each service as a Container App within the environment
   - Internal services (EventLog, Progression) get internal ingress only
   - Gateway gets external ingress (public WebSocket endpoint, simulation in-process)
   - OperatorApi gets external ingress (admin dashboard, proxies to Gateway for simulation data)
5. Create Azure Database for PostgreSQL Flexible Server (cheapest tier: Burstable B1ms ~$13/mo)
6. Run the DB init script against Azure Postgres to create tables
7. Set connection strings via Container App secrets/env vars

**Estimated cost:** ~$25-30/month during testing (Postgres + minimal container usage). Scale-to-zero means containers cost near $0 when nobody's connected.

**Networking:**
- Gateway exposed via HTTPS (Container Apps provides TLS termination)
- WebSocket upgrade works over HTTPS — `wss://gateway.azurecontainerapps.io`
- No NAT/firewall issues for clients — standard HTTPS port 443
- Internal services communicate via the Container Apps environment's internal DNS

**CORS:** Set `CORS_ORIGINS` environment variable (comma-separated origins, e.g. `https://admin.azurecontainerapps.io,http://localhost:5173`). Already implemented in `ServiceDefaultsExtensions.cs`.

## Option B: Single Azure VM

**Why:** Simpler to reason about, can run docker-compose directly, full control.

**Steps:**
1. Create Azure VM (B2s ~$30/mo, or B1s ~$15/mo)
2. Install Docker, copy docker-compose.yml
3. Open ports 4000 (WebSocket) and 4004 (admin) in NSG
4. `docker compose up -d`
5. Test with public IP: `node scripts/test-multiplayer.js 10 ws://<vm-ip>:4000`

**Trade-offs vs Container Apps:**
- Simpler setup, but always-on cost (doesn't scale to zero)
- Must manage TLS yourself (Let's Encrypt / Caddy reverse proxy)
- Must manage VM updates, Docker updates
- Good enough for friend-testing, not for production

## Option C: Distribute test .exe to friends (no cloud)

**Why:** Zero cloud cost, tests real global latency.

**Requires:** One person runs the backend on their machine with port forwarding, others connect. Fragile but free.

**Steps:**
1. Host exposes port 4000 via router port forwarding or ngrok/Cloudflare Tunnel
2. Friends run the Node.js test script: `node test-multiplayer.js 1 ws://<host-ip>:4000`
3. Or build a Godot client .exe that connects to the endpoint

## DB Schema Init

The Postgres tables must exist before .NET services start. For a fresh Azure Postgres, run the init script:

```
infra/docker/init.sql   ← Full schema including regions table. Ready for fresh deployments.
```

Regenerate if schema changes: `docker exec game-postgres pg_dump -U game -d game --schema-only > infra/docker/init.sql`

The local Docker compose auto-mounts `init.sql` via `/docker-entrypoint-initdb.d/` so fresh volumes get the schema automatically.

## CORS Configuration

**Implemented.** Set the `CORS_ORIGINS` environment variable with comma-separated origins:

```
CORS_ORIGINS=https://admin.yourdomain.com,http://localhost:5173
```

Falls back to `localhost:5173` and `localhost:5174` when not set (dev defaults).

For WebSocket connections, CORS is not enforced by the browser the same way — the `Origin` header is sent but the server decides. The Gateway middleware accepts all WebSocket connections currently, which is fine for testing.

## Test Commands

All smoke test scripts accept an optional gateway URL argument for remote testing:

```bash
# Against local stack
node scripts/test-multiplayer.js 10
node scripts/test-vertical-slice.js
node scripts/test-challenges.js
node scripts/test-resume.js

# Against Azure deployment
node scripts/test-multiplayer.js 10 wss://$GATEWAY_URL
node scripts/test-vertical-slice.js wss://$GATEWAY_URL
node scripts/test-challenges.js wss://$GATEWAY_URL
node scripts/test-resume.js wss://$GATEWAY_URL

# Against a friend's machine
node scripts/test-multiplayer.js 1 ws://friend-ip:4000
```

## What to validate in remote testing

1. **Latency**: Do entity_update broadcasts arrive within acceptable time? (< 200ms for building, < 50ms for combat)
2. **Ordering**: Do concurrent structure placements from different continents resolve correctly?
3. **Challenge progress**: Does the atomic upsert hold under real network jitter?
4. **Reconnection**: What happens when a player's connection drops and reconnects?
5. **Persistence**: Stop and restart the server — do structures and progress survive?
