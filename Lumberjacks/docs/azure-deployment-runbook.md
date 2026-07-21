# Azure Deployment Runbook

Step-by-step instructions to deploy the game backend to Azure Container Apps, smoke test it, and scale it.

## Prerequisites

- Azure CLI installed and logged in (`az login`)
- Docker Desktop installed (for building images)
- Node.js installed (for smoke tests)
- PowerShell (all commands in section 10+ use PowerShell syntax)
- An Azure subscription with permissions to create resources

> **Note:** Sections 1–9 use bash syntax (for first-time setup, which may be done from Cloud Shell). Section 10+ uses PowerShell for the ongoing build/deploy workflow.

## 1. Set Variables

Every command below uses these. Set them once in your shell.

```bash
RG="game-rg"
LOCATION="eastus"
ACR_NAME="gameacr$(openssl rand -hex 4)"   # must be globally unique
ENV_NAME="game-env"
PG_SERVER="game-pg-$(openssl rand -hex 4)"
PG_ADMIN="gameadmin"
PG_PASSWORD="$(openssl rand -base64 24)"    # save this somewhere safe
PG_DB="game"
```

> Write down `PG_PASSWORD` and `ACR_NAME` — you'll need them later.

## 2. Create Resource Group

```bash
az group create --name $RG --location $LOCATION
```

## 3. Create Azure Container Registry

```bash
az acr create --resource-group $RG --name $ACR_NAME --sku Basic --admin-enabled true
az acr login --name $ACR_NAME
```

Get the login server:

```bash
ACR_LOGIN=$(az acr show --name $ACR_NAME --query loginServer -o tsv)
```

## 4. Build and Push Docker Images

From the repo root:

```bash
# Build all service targets and push (4 services — simulation runs in-process in the Gateway)
for SERVICE in gateway eventlog progression operatorapi; do
  docker build --target $SERVICE -t $ACR_LOGIN/game-$SERVICE:latest .
  docker push $ACR_LOGIN/game-$SERVICE:latest
done
```

This uses the multi-stage Dockerfile. Each target produces a ~120MB image.

## 5. Create PostgreSQL Flexible Server

```bash
az postgres flexible-server create \
  --resource-group $RG \
  --name $PG_SERVER \
  --location $LOCATION \
  --admin-user $PG_ADMIN \
  --admin-password "$PG_PASSWORD" \
  --database-name $PG_DB \
  --sku-name Standard_B1ms \
  --tier Burstable \
  --storage-size 32 \
  --version 16 \
  --yes
```

Allow Azure services to connect:

```bash
az postgres flexible-server firewall-rule create \
  --resource-group $RG \
  --name $PG_SERVER \
  --rule-name AllowAzureServices \
  --start-ip-address 0.0.0.0 \
  --end-ip-address 0.0.0.0
```

### 5a. Initialize the Database Schema

```bash
PG_HOST=$(az postgres flexible-server show --resource-group $RG --name $PG_SERVER --query fullyQualifiedDomainName -o tsv)

psql "host=$PG_HOST dbname=$PG_DB user=$PG_ADMIN password=$PG_PASSWORD sslmode=require" -f infra/docker/init.sql
```

If you don't have `psql` locally, use Azure Cloud Shell or a temporary container:

```bash
docker run --rm -v $(pwd)/infra/docker/init.sql:/init.sql postgres:16-alpine \
  psql "host=$PG_HOST dbname=$PG_DB user=$PG_ADMIN password=$PG_PASSWORD sslmode=require" -f /init.sql
```

## 6. Create Container Apps Environment

```bash
az containerapp env create \
  --resource-group $RG \
  --name $ENV_NAME \
  --location $LOCATION
```

## 7. Build the Connection String

```bash
CONN_STR="Host=$PG_HOST;Database=$PG_DB;Username=$PG_ADMIN;Password=$PG_PASSWORD;Ssl Mode=Require;Trust Server Certificate=true"
```

## 8. Deploy Services

Deploy in order: internal services first, then services that depend on them.

> **Note:** The Gateway runs the simulation in-process (WorldState, TickLoop, handlers). There is no separate Simulation container to deploy.

### 8a. EventLog (internal only)

```bash
az containerapp create \
  --resource-group $RG \
  --environment $ENV_NAME \
  --name eventlog \
  --image $ACR_LOGIN/game-eventlog:latest \
  --registry-server $ACR_LOGIN \
  --registry-username $(az acr credential show --name $ACR_NAME --query username -o tsv) \
  --registry-password $(az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv) \
  --target-port 4002 \
  --ingress internal \
  --min-replicas 1 \
  --max-replicas 1 \
  --env-vars \
    "Urls=http://+:4002" \
    "ConnectionStrings__GameDb=$CONN_STR"
```

### 8b. Progression (internal only)

```bash
az containerapp create \
  --resource-group $RG \
  --environment $ENV_NAME \
  --name progression \
  --image $ACR_LOGIN/game-progression:latest \
  --registry-server $ACR_LOGIN \
  --registry-username $(az acr credential show --name $ACR_NAME --query username -o tsv) \
  --registry-password $(az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv) \
  --target-port 4003 \
  --ingress internal \
  --min-replicas 1 \
  --max-replicas 1 \
  --env-vars \
    "Urls=http://+:4003" \
    "ConnectionStrings__GameDb=$CONN_STR"
```

### 8c. Gateway (external — public WebSocket endpoint, runs simulation in-process)

```bash
az containerapp create \
  --resource-group $RG \
  --environment $ENV_NAME \
  --name gateway \
  --image $ACR_LOGIN/game-gateway:latest \
  --registry-server $ACR_LOGIN \
  --registry-username $(az acr credential show --name $ACR_NAME --query username -o tsv) \
  --registry-password $(az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv) \
  --target-port 4000 \
  --ingress external \
  --transport http \
  --min-replicas 1 \
  --max-replicas 1 \
  --env-vars \
    "Urls=http://+:4000" \
    "ConnectionStrings__GameDb=$CONN_STR" \
    "ServiceUrls__EventLog=https://eventlog.internal.$ENV_NAME.azurecontainerapps.io" \
    "ServiceUrls__Progression=https://progression.internal.$ENV_NAME.azurecontainerapps.io"

GATEWAY_URL=$(az containerapp show --resource-group $RG --name gateway --query "properties.configuration.ingress.fqdn" -o tsv)
echo "Gateway: wss://$GATEWAY_URL"
```

### 8d. OperatorApi (external — admin dashboard)

```bash
az containerapp create \
  --resource-group $RG \
  --environment $ENV_NAME \
  --name operatorapi \
  --image $ACR_LOGIN/game-operatorapi:latest \
  --registry-server $ACR_LOGIN \
  --registry-username $(az acr credential show --name $ACR_NAME --query username -o tsv) \
  --registry-password $(az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv) \
  --target-port 4004 \
  --ingress external \
  --min-replicas 1 \
  --max-replicas 1 \
  --env-vars \
    "Urls=http://+:4004" \
    "ConnectionStrings__GameDb=$CONN_STR" \
    "ServiceUrls__Gateway=https://gateway.internal.$ENV_NAME.azurecontainerapps.io" \
    "ServiceUrls__EventLog=https://eventlog.internal.$ENV_NAME.azurecontainerapps.io" \
    "ServiceUrls__Progression=https://progression.internal.$ENV_NAME.azurecontainerapps.io" \
    "CORS_ORIGINS=https://$GATEWAY_URL,http://localhost:5173"

OPERATOR_URL=$(az containerapp show --resource-group $RG --name operatorapi --query "properties.configuration.ingress.fqdn" -o tsv)
echo "Operator API: https://$OPERATOR_URL"
```

## 9. Smoke Tests

### 9a. Health Checks

Verify every service is responding:

```bash
# External services (direct)
curl -s https://$GATEWAY_URL/health | jq .
curl -s https://$OPERATOR_URL/health | jq .

# Internal services (via operator proxy)
curl -s https://$OPERATOR_URL/api/status | jq .
```

Expected: `{"status":"ok","service":"gateway",...}` for each.

### 9b. WebSocket Connection Test

Quick manual validation:

```bash
# Requires wscat: npm install -g wscat
wscat -c "wss://$GATEWAY_URL"
# Should receive: {"version":1,"type":"session_started",...}
```

### 9c. Tick Loop

```bash
curl -s https://$OPERATOR_URL/api/tick | jq .
# Should show: current_tick > 0, tick_rate_hz: 20, uptime_seconds > 0
```

### 9d. Multiplayer Test (automated)

Run the multi-player test script against the live deployment:

```bash
node scripts/test-multiplayer.js 5 wss://$GATEWAY_URL
```

This spawns 5 concurrent WebSocket players who join a region, place structures, and verify broadcasts. All assertions should pass.

### 9e. Resume Test

```bash
node scripts/test-resume.js wss://$GATEWAY_URL
```

Validates disconnect/reconnect with resume token works through Azure's load balancer.

### 9f. Challenge Test

```bash
node scripts/test-challenges.js wss://$GATEWAY_URL
```

Validates guild challenge creation, progress increment, and auto-completion.

> **Note:** The multiplayer and challenge tests automatically route HTTP calls through the OperatorApi proxy when targeting Azure URLs. The vertical-slice test still hits EventLog/Progression directly and only works locally.

### 9g. Full Validation Checklist

| Check | Command | Expected |
|-------|---------|----------|
| Gateway health | `curl https://$GATEWAY_URL/health` | `{"status":"ok"}` |
| Operator health | `curl https://$OPERATOR_URL/health` | `{"status":"ok"}` |
| Service status | `curl https://$OPERATOR_URL/api/status` | All 4 services `up` |
| Tick running | `curl https://$OPERATOR_URL/api/tick` | `current_tick > 0` |
| WS connects | `wscat -c wss://$GATEWAY_URL` | `session_started` envelope |
| Multiplayer | `node scripts/test-multiplayer.js 5 wss://$GATEWAY_URL` | All pass |
| Resume | `node scripts/test-resume.js wss://$GATEWAY_URL` | All pass |
| DB persistence | Restart gateway, reconnect — structures still there | Data survives restart |

## 10. Updating Services (Build & Deploy)

After code changes, rebuild the Docker image(s), push to ACR, and force a new Container Apps revision.

### Current Deployment Details

These are the values from the current Azure deployment. If you created fresh resources, substitute your own.

```powershell
$RG         = "game-rg"
$ACR_LOGIN  = "gameacr8c23e1c1.azurecr.io"
$ACR_NAME   = "gameacr8c23e1c1"
$ENV_NAME   = "game-env"
$GATEWAY_URL = "gateway.wittyplant-6c0ca715.eastus2.azurecontainerapps.io"
$OPERATOR_URL = "operatorapi.wittyplant-6c0ca715.eastus2.azurecontainerapps.io"
```

### Step-by-step: Deploy a Single Service

Example: deploying `operatorapi` after a code change.

```powershell
# 1. Kill any running local .NET processes (they lock DLLs and break Docker builds)
taskkill /F /IM Game.OperatorApi.exe 2>$null
taskkill /F /IM Game.Gateway.exe 2>$null
taskkill /F /IM dotnet.exe 2>$null

# 2. Rebuild the Docker image — use --no-cache to ensure code changes are picked up
#    The multi-stage Dockerfile builds from source, so stale layer cache can skip your changes.
docker build --no-cache --target operatorapi -t game-operatorapi .

# 3. Tag with a UNIQUE version tag (not just "latest")
#    Azure Container Apps won't pull a new image if the tag hasn't changed.
$TAG = "v" + (Get-Date -Format "yyyyMMdd-HHmmss")
docker tag game-operatorapi "${ACR_LOGIN}/game-operatorapi:${TAG}"

# 4. Push to ACR (you must be logged in: docker login $ACR_LOGIN)
docker push "${ACR_LOGIN}/game-operatorapi:${TAG}"

# 5. Update the container app with the new tag AND force a new revision
az containerapp update `
  --resource-group $RG `
  --name operatorapi `
  --image "${ACR_LOGIN}/game-operatorapi:${TAG}"

# 6. Verify the new revision is active
az containerapp revision list --name operatorapi --resource-group $RG --output table
```

### Deploy All Services at Once

```powershell
$TAG = "v" + (Get-Date -Format "yyyyMMdd-HHmmss")

foreach ($svc in @("gateway", "eventlog", "progression", "operatorapi")) {
    Write-Host "=== Building $svc ===" -ForegroundColor Cyan
    docker build --no-cache --target $svc -t "game-${svc}" .

    docker tag "game-${svc}" "${ACR_LOGIN}/game-${svc}:${TAG}"
    docker push "${ACR_LOGIN}/game-${svc}:${TAG}"

    az containerapp update `
      --resource-group $RG `
      --name $svc `
      --image "${ACR_LOGIN}/game-${svc}:${TAG}"

    Write-Host "=== $svc deployed ===" -ForegroundColor Green
}
```

### ACR Login

If `docker push` fails with auth errors, re-authenticate:

```powershell
# Option A: az acr login (if az is on PATH)
az acr login --name $ACR_NAME

# Option B: manual docker login
$ACR_PASSWORD = az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv
docker login $ACR_LOGIN -u $ACR_NAME -p $ACR_PASSWORD
```

### Common Gotchas

| Problem | Cause | Fix |
|---------|-------|-----|
| Docker build succeeds but old code runs on Azure | Docker layer cache served stale build output | Always use `--no-cache` when deploying |
| `az containerapp update` succeeds but old revision still serves traffic | Same image tag — Azure doesn't re-pull | Use a unique tag per deploy (timestamp-based) |
| `dotnet build` fails with "file in use" errors | Local .NET processes lock DLLs | Kill `dotnet.exe` and `Game.*.exe` processes first |
| `docker push` access denied | ACR login expired | Run `az acr login` or `docker login` again |

### Verify Deployment

After deploying, confirm the new code is live:

```powershell
# Health check
Invoke-RestMethod "https://$OPERATOR_URL/api/status"

# Quick smoke test
node scripts/test-resume.js "wss://$GATEWAY_URL"

# Full multiplayer test
node scripts/test-multiplayer.js 10 "wss://$GATEWAY_URL"
```

### View Admin Dashboard Against Azure

The admin dashboard runs locally and proxies API calls to the remote OperatorApi:

```powershell
$env:API_TARGET = "https://$OPERATOR_URL"
npm run dev -w @game/admin-web
# Open http://localhost:5173
```

## 11. Scaling

### Scale-to-Zero (save money when idle)

Set min replicas to 0 for services that don't need to run continuously. Fine for EventLog and Progression during testing:

```bash
az containerapp update --resource-group $RG --name eventlog --min-replicas 0 --max-replicas 3
az containerapp update --resource-group $RG --name progression --min-replicas 0 --max-replicas 3
```

Gateway must stay at min 1 (runs tick loop + holds WebSocket sessions):

```bash
az containerapp update --resource-group $RG --name gateway --min-replicas 1 --max-replicas 5
```

### HTTP Scaling Rules

Container Apps scales based on concurrent HTTP requests (default: 10 concurrent per replica). Override:

```bash
# Gateway: scale at 50 concurrent connections per replica
az containerapp update \
  --resource-group $RG \
  --name gateway \
  --scale-rule-name http-scaling \
  --scale-rule-type http \
  --scale-rule-http-concurrency 50 \
  --min-replicas 1 \
  --max-replicas 10
```

### Manual Scaling

For load tests or events where you know the expected player count:

```bash
# Set exact replica count
az containerapp update --resource-group $RG --name gateway --min-replicas 3 --max-replicas 3
```

### Scaling Constraints

| Service | Min | Max | Notes |
|---------|-----|-----|-------|
| Gateway | 1 | 10 | Runs simulation in-process. Stateful WebSocket sessions — scaling requires sticky sessions or session migration. Keep at 1 replica until session handoff is implemented. |
| EventLog | 0 | 5 | Stateless, scales freely. |
| Progression | 0 | 5 | Stateless, atomic DB upserts handle concurrency. |
| OperatorApi | 0 | 3 | Stateless proxy, low traffic. |

### Vertical Scaling (more CPU/RAM per replica)

```bash
# Give Gateway more resources for WebSocket connections + simulation
az containerapp update \
  --resource-group $RG \
  --name gateway \
  --cpu 1.0 \
  --memory 2Gi
```

### PostgreSQL Scaling

Upgrade the Postgres tier when you outgrow Burstable B1ms:

```bash
# Check current tier
az postgres flexible-server show --resource-group $RG --name $PG_SERVER --query "sku" -o json

# Scale up (causes brief downtime)
az postgres flexible-server update \
  --resource-group $RG \
  --name $PG_SERVER \
  --sku-name Standard_B2ms \
  --tier Burstable
```

| Tier | vCores | RAM | Cost/mo | Good for |
|------|--------|-----|---------|----------|
| B1ms | 1 | 2 GB | ~$13 | Testing, <20 concurrent players |
| B2ms | 2 | 4 GB | ~$26 | Friend testing, <100 concurrent |
| B2s  | 2 | 4 GB | ~$30 | Sustained friend testing |
| D2s_v3 | 2 | 8 GB | ~$100 | Alpha launch |

## 12. Monitoring

### View Logs

```bash
# Stream live logs from a service
az containerapp logs show --resource-group $RG --name gateway --follow

# Query recent logs
az containerapp logs show --resource-group $RG --name gateway --tail 100
```

### Check Replica Status

```bash
az containerapp replica list --resource-group $RG --name gateway -o table
```

### Postgres Metrics

```bash
az postgres flexible-server show --resource-group $RG --name $PG_SERVER --query "{cpu:sku.name,storage:storage.storageSizeGb,state:state}" -o json
```

## 13. Teardown

Remove everything when done testing:

```bash
# Delete the resource group (removes ALL resources inside it)
az group delete --name $RG --yes --no-wait
```

This deletes the Container Apps, ACR, Postgres, and all associated resources. Data in Postgres is **permanently lost**.

## Cost Estimate

| Resource | Tier | Monthly Cost |
|----------|------|-------------|
| Postgres Flexible Server | B1ms | ~$13 |
| Container Apps (4 services, always-on) | Consumption | ~$5-10 |
| Container Registry | Basic | ~$5 |
| **Total (testing)** | | **~$25-30/mo** |

With scale-to-zero on idle services, costs drop to ~$15-20/mo during periods of no activity.

## Troubleshooting

**Container won't start:**
```bash
az containerapp logs show --resource-group $RG --name <service> --tail 50
```
Usually a bad connection string or missing env var.

**WebSocket won't connect:**
- Container Apps requires `--transport http` for WebSocket support (set in step 8c)
- Client must use `wss://` (TLS), not `ws://`
- Check Gateway logs for connection errors

**DB connection refused:**
- Verify firewall rule allows Azure services (step 5)
- Verify connection string has `Ssl Mode=Require;Trust Server Certificate=true`

**Internal service-to-service calls fail:**
- Internal ingress URLs follow the pattern: `https://<app-name>.internal.<env-name>.azurecontainerapps.io`
- Verify the env vars use the correct internal URLs
- Check that the target service has `--ingress internal` set

**Resume test fails:**
- Gateway must be single-replica — session state is in-memory
- If running multiple replicas, the reconnecting client may hit a different instance
