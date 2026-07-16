# Combined Comfy + Lumberjacks P7 environment

Status: **single-client authoritative-priority ZDO cutover passed**, 2026-07-16 UTC.

P7 runs the real `ComfyEra16` Valheim world and Lumberjacks authority services on
GCP. OMEN is the rendered client and operator workstation. The clean victory session
closed 83,220/83,220 priority-tagged ZDO envelopes with zero pending, native-only,
rejects, duplicates, retries, or client transport failures.

Canonical evidence:

- `C:\work\comfy\fieldlab\evidence\p7-primary-v1-authoritative-priority-zdo-20260716-v0531.md`
- `C:\work\comfy\fieldlab\runs\20260716-011112-valheim-lumberjacks-authoritative-priority-cutover\report.md`
- `C:\work\lumberjacks\docs\network\valheim-lumberjacks-p7-overview.md`
- release manifest
  `C:\work\comfy\fieldlab\runs\releases\p7-primary-v1-0.5.31-clean.json`

## Live deployment

| Item | Value |
|---|---|
| GCP project | `lumberjacks-exp-20260711-djc` |
| VM / zone | `comfy-lumberjacks-p7` / `us-west1-b` |
| Machine | `n2-highmem-8` |
| SSH target | `comfy-p7` through IAP |
| Persistent disk | `/mnt/comfy-p7` |
| World / server | `ComfyEra16` / `Comfy Era16 Lab` |
| Valheim join | `8.231.129.249:2456` UDP; Steam-only, unlisted, password-free |
| Player Gateway | `http://8.231.129.249:42317` |
| Mode / window | `lumberjacks-primary` / `p7-primary-v1` |
| Mod | ComfyNetworkSense `0.5.31` |
| Mod SHA-256 | `b31697d2a0cbe47b86c32b33d19fb9445e21af0cfe51687cb5afe871a3d7d77b` |
| Gateway image | `sha256:358f5e11e35b54367a83d4e52ea3d47c0346e62a82ed357c2ff403eafafcd0a2` |
| Valheim image | `ghcr.io/community-valheim-tools/valheim-server@sha256:e8b13da3c44f54a38511c8ac224f2959a437c0b2626cf916683ca7acc8dfb146` |

Services:

| Service | Exposure | Persistent role |
|---|---|---|
| Valheim | public UDP `2456-2457` | world simulation and native peer connection |
| Gateway | GCP loopback `4000`; pilot public TCP `42317` | priority ZDO queue, acknowledgements, enrollment, telemetry |
| Gateway UDP | loopback UDP `4005` | progressive transport infrastructure; not this ZDO consumer path |
| PostgreSQL | loopback `5433` | general Lumberjacks persistence |
| EventLog / Progression / Operator API | loopback `4002` / `4003` / `4004` | internal service and operator surfaces |
| `redirect.wal` | `/mnt/comfy-p7/lumberjacks/zdo-queue/redirect.wal` | durable authoritative ZDO delivery |
| enrollment store | `/mnt/comfy-p7/lumberjacks/enrollment/` | one-time invites and per-player credentials |

The player Gateway is intentionally simple for the volunteer pilot: plain HTTP on a
non-default port. Authoritative Valheim routes require the issued enrollment ID and
token. Dashboard GETs are not access-controlled. Add TLS, rate limits, and surface
separation before treating this as an Internet-hardened service.

## Authority boundary

Lumberjacks owns sequencing, priority ordering, durable delivery, client application
validation, and success-only acknowledgement for the observed ZDO window. Steam
login, the native Valheim connection, server simulation, Valheim's construction of
the candidate relevance list, and non-ZDO RPCs remain native.

The server adapter is primary and fail-closed for redirected ZDOs: loss of the
Gateway/client path leaves durable unacknowledged work. It does not silently count a
native fallback as success.

## Current role configuration

The server's non-secret settings are:

```ini
[Lumberjacks]
lumberjacksGatewayUrl = http://gateway:4000
lumberjacksCutoverMode = lumberjacks-primary
lumberjacksEnrollmentManifestId = p7-primary-v1
zdoAuthoritativeConsumerEnabled = false
zdoRedirectEnabled = true
zdoRedirectPrefabs = *
zdoRedirectEndpoint = http://gateway:4000
zdoRedirectWindowId = p7-primary-v1
zdoRedirectActiveSeconds = 0
```

Each player receives unique values after Steam invite redemption:

```ini
[Lumberjacks]
lumberjacksGatewayUrl = http://8.231.129.249:42317
lumberjacksAuthoritativeWindowId = p7-primary-v1
lumberjacksEnrollmentId = <issued enrollment id>
lumberjacksClientAccessKey = <issued secret>
zdoAuthoritativeConsumerEnabled = true
```

Never commit, screenshot, or paste an issued access key into an evidence report.

## Reproduce the current deployment

### 1. Protect the world lineage

Confirm no source/old server can write `ComfyEra16` while P7 is active. Before a
migration, stop the source cleanly, archive the final `.db` and `.fwl`, and verify the
archive manifest byte-for-byte. Historical migration baseline hashes are:

| Artifact | Baseline SHA-256 |
|---|---|
| root BepInEx configuration | `065e942174d0912ca94d108794b4d59bbdec34e2e21a299a31b63efc6a017d01` |
| `ComfyEra16.db` | `4513d0348e9f740cad22032c476c5dd6f5304490dc05912f35b250837e25d49a` |
| `ComfyEra16.fwl` | `5f323fbe7b627fd50520d8f4f6dedd13027a92bfe056013aa52d7306d09a3539` |

World hashes change after a clean save; treat these as migration records, not eternal
expected values.

### 2. Provision or reconcile GCP

Copy `terraform.tfvars.example` to ignored `terraform.tfvars`, set the project,
operator, OMEN CIDR, and pilot port, then review before applying:

```powershell
terraform -chdir=C:\work\comfy\infra\gcp\p7 init
terraform -chdir=C:\work\comfy\infra\gcp\p7 plan
terraform -chdir=C:\work\comfy\infra\gcp\p7 apply
```

Secrets belong only in `/etc/comfy-p7/environment` with mode `0600`, never Terraform
state. Required non-secret runtime declarations include:

```text
LUMBERJACKS_ROOT=/opt/lumberjacks-ed83bd8
COMFY_NETWORKSENSE_VERSION=0.5.31
COMFY_LUMBERJACKS_CUTOVER_MODE=lumberjacks-primary
COMFY_LUMBERJACKS_ENROLLMENT_MANIFEST_ID=p7-primary-v1
LUMBERJACKS_PLAYER_PORT=42317
LUMBERJACKS_PLAYER_GATEWAY_URL=http://8.231.129.249:42317
LUMBERJACKS_ENROLLMENT_PUBLIC_URL=http://8.231.129.249:42317
```

The file also contains database, telemetry, shared fallback, and admin secrets. Do not
print it wholesale.

### 3. Test the code

```powershell
Set-Location C:\work\lumberjacks
& C:\work\dotnet9\dotnet.exe test `
  tests\Game.Gateway.Tests\Game.Gateway.Tests.csproj

dotnet build `
  C:\work\comfy\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj `
  -c Release
```

Victory snapshot: 46 Gateway tests pass; the mod builds with zero warnings and errors.
The Gateway suite currently emits pre-existing Entity Framework version-conflict
warnings.

### 4. Deploy ComfyNetworkSense

Close local Valheim before copying the OMEN DLL. Deploy the server DLL with guarded
backup, restart, readiness, runtime hash, and cold-start hash checks:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\deploy-network-sense.ps1 `
  -ManifestPath `
    C:\work\comfy\fieldlab\runs\releases\p7-primary-v1-0.5.31-clean.json
```

Server paths:

```text
runtime:    /opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll
cold start: /mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll
config:     /mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg
```

The config bind mount must remain UID/GID `1000:1000`, mode `0664`; otherwise
BepInEx can abort the plugin during `ConfigFile.Bind`. The victory backup is
`/mnt/comfy-p7/backups/comfynetworksense/20260716T004955Z`.

The mod build also targets the standard OMEN plugin location. After Valheim is closed,
verify the installed file explicitly:

```powershell
$dll = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\ComfyNetworkSense.dll'
Get-FileHash $dll -Algorithm SHA256
[Reflection.AssemblyName]::GetAssemblyName($dll).Version
```

### 5. Deploy Gateway changes

Back up the changed source under `/mnt/comfy-p7/backups/gateway/<timestamp>`, compact
only while no client is draining, rebuild only the Gateway service, wait for
`/health`, and verify deployed source hashes. The final victory deployment backup is
`/mnt/comfy-p7/backups/gateway/20260716T005900Z`.

`scripts\deploy-gateway.ps1` automates that transaction, but Windows `gcloud` IAP can
emit a benign `stdin ReadFile failed` traceback after a successful SSH command. Judge
the remote transaction by its explicit health/hash result, not by stderr alone. Until
the script has its own recorded acceptance run, retain the backup path and verify the
container image, health, source hashes, and empty queue manually after it returns.

### 6. Enroll the player

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\new-player-invite.ps1
```

The script authenticates locally on GCP over SSH and returns a one-use, 24-hour URL.
The player follows the link, signs in with Steam OpenID, and copies the returned four
Lumberjacks values into the local BepInEx config. See
[VOLUNTEER-ENDPOINT.md](VOLUNTEER-ENDPOINT.md).

### 7. Establish the preflight baseline

```powershell
$gateway = 'http://8.231.129.249:42317'
Invoke-RestMethod "$gateway/health"
Invoke-RestMethod "$gateway/api/v0/telemetry/cutover" |
  ConvertTo-Json -Depth 20
```

Require fresh heartbeat, version `0.5.31`, `lumberjacks-primary`, 100% coverage,
native-only zero, persistence healthy, and an empty P7 window before admitting a new
test. Also verify disk space and both server DLL hashes.

### 8. Launch without a tunnel

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\start-direct-session.ps1
```

This health-checks the direct Gateway and launches Valheim with
`+connect 8.231.129.249:2456`. No OMEN forwarding process is required; the poller is
inside ComfyNetworkSense. The old `127.0.0.1:14000` IAP tunnel remains an operator
fallback only.

### 9. Accept and preserve the window

Exercise spawn, dense construction, and rapid travel into an uncached area. Capture
`/api/v0/telemetry/cutover` before disconnect. Pass only when the same primary window
has 100% coverage, zero native-only/fallback, receipts equal acknowledgements, zero
pending, `complete=true`, healthy persistence, and zero reject/duplicate/retry/client
transport failures.

The live API can reset receipt counters after a consumer leaves or a window rolls;
save the coherent closure sample rather than reconstructing it from later totals.

## Dashboards

These report the deployed GCP Gateway:

```text
http://8.231.129.249:42317/community
http://8.231.129.249:42317/networksense
http://8.231.129.249:42317/events
http://8.231.129.249:42317/testing
```

For the private admin console, forward Operator API and run Vite locally:

```powershell
gcloud compute ssh comfy-lumberjacks-p7 `
  --project lumberjacks-exp-20260711-djc `
  --zone us-west1-b --tunnel-through-iap -- `
  -L 14004:127.0.0.1:4004
```

Then start `admin-web` with `API_TARGET=http://127.0.0.1:14004`.

## Rollback and recovery

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\rollback-network-sense.ps1 `
  -BackupPath /mnt/comfy-p7/backups/comfynetworksense/<timestamp>

& C:\work\comfy\infra\gcp\p7\scripts\rollback-gateway.ps1 `
  -BackupPath /mnt/comfy-p7/backups/gateway/<timestamp>
```

After rollback, do not resume primary traffic until Gateway health, mod/server
readiness, runtime/cold-start hashes, WAL health, and empty-window state all pass.
Gateway restart and client reconnect were exercised during the earlier efficiency
audit. Continue retaining raw samples for restart, network interruption, malformed
envelope, disk-full/permission, replay, and two-client recipient-isolation tests.

## Next correctness gate

The P7 queue is currently a shared authoritative window. Before adding simultaneous
volunteers, make pending delivery and acknowledgement recipient-scoped, then run two
real Steam clients and prove that neither can consume or acknowledge the other's
relevant ZDOs. Only after that gate should the project broaden capacity, automate WAL
compaction, harden transport exposure, and right-size the VM.
