# Combined Comfy + Lumberjacks P7 environment

Status: **re-provisioned from `baseline` and re-accepted**, 2026-07-21 UTC. All five
gated services now run from digest pins resolved out of a validated release bundle,
and the VM's deployment source is a `baseline` checkout rather than the retired
`comfy` repo. A real player session then met every §9 acceptance criterion:
75,112/75,112 receipts acknowledged with zero pending, `complete=true`, 100% coverage
over 148,892 ZDOs, and zero native-only, fallback, reject, duplicate, or retry.

The earlier single-client cutover (2026-07-16, 83,220/83,220) remains the origin of
this line of work; it is superseded as the current state but not as history.

P7 runs the real `ComfyEra16` Valheim world and Lumberjacks authority services on
GCP. OMEN is the rendered client and operator workstation.

Canonical evidence (paths are relative to this repo, `C:\work\baseline`):

- `Lumberjacks/docs/roadmap/m5-v3-acceptance-receipt.json` — current acceptance sample
- `Lumberjacks/docs/roadmap/m5-v3-reprovision-receipt.json` — what changed on the VM
- `Lumberjacks/docs/roadmap/m5-recipients-build-candidate-v3.json` — release manifest
- `fieldlab/evidence/p7-primary-v1-authoritative-priority-zdo-20260716-v0531.md` — the
  2026-07-16 victory session (historical)

> Older revisions of this file cited these under `C:\work\comfy\…` and
> `C:\work\lumberjacks\…`. Both are retired checkout roots; the content landed in this
> repo unmodified during the July 2026 consolidation.

## Live deployment

| Item | Value |
|---|---|
| GCP project | `lumberjacks-exp-20260711-djc` |
| VM / zone | `comfy-lumberjacks-p7` / `us-west1-b` |
| Machine | `n2-highmem-2` (deliberate cost downsize from `n2-highmem-8`) |
| SSH target | `comfy-p7` through IAP |
| Deployment source | `/opt/comfy` — a **`baseline` checkout**, branch `main`. Updated by `git bundle` over SSH; the box holds no GitHub credentials (ADR 0006). Pre-cutover `comfy` commit retained as local branch `master` for rollback. |
| Persistent disk | `/mnt/comfy-p7` |
| World / server | `ComfyEra16` / `Comfy Era16 Lab` |
| Valheim join | `8.231.129.249:2456` UDP; Steam-only, unlisted, password-free |
| Player Gateway | `http://8.231.129.249:42317` |
| Mode / window | `lumberjacks-primary` / `p7-primary-v1` |
| Release | `m5-recipients-20260720-r1` (v3 manifest) |
| Mod | ComfyNetworkSense `0.5.31` |
| Mod SHA-256 | `035faa8793114c75ccb4295e219a6c10a91250a3a4f3764e70aed499a32a0dfd` |
| Gateway image | `sha256:69e025e8c13bc7ce01a21a054c9dcb42478415531d727e8b55b4b2d37ca7b38b` |
| EventLog image | `sha256:501537285f89052991a62201969117557005c03ddfd280086bde81ce8e8593e4` |
| Progression image | `sha256:1700513587ae259d7447caba267c90213c5249ce7da50408f65648a2bf4872bc` |
| Operator API image | `sha256:cec85d9272530a13b9c1e5217ef8864f0bffd4dc8fc89a18d2236d75482761a7` |
| Valheim image | `ghcr.io/community-valheim-tools/valheim-server@sha256:e8b13da3c44f54a38511c8ac224f2959a437c0b2626cf916683ca7acc8dfb146` |

All five Lumberjacks images are pinned by digest in `docker-compose.yml` with no `build:`
fallback, and resolve through `/etc/comfy-p7/environment` alone — verified by a real
`systemctl restart`, which is exactly what the reboot path runs.

> **Server restart is not instant.** A `systemctl restart` reloads the ~9.1M-ZDO `ComfyEra16`
> world; the server is not joinable until the log emits `Game server connected`, roughly a
> minute later. Wait for that line before telling a player to join.

Services:

| Service | Exposure | Persistent role |
|---|---|---|
| Valheim | public UDP `2456-2457` | world simulation and native peer connection |
| Gateway | GCP loopback `4000`; pilot public TCP `42317` | priority ZDO queue, acknowledgements, enrollment, telemetry |
| Gateway UDP | public UDP `4005` | session-token-authenticated player motion; UDP preferred with binary WebSocket fallback |
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

Player motion uses the same enrollment identity. The client first authenticates its
WebSocket, receives a random per-session UDP token, joins the configured Lumberjacks
region, and then sends a fixed 50-byte motion datagram at up to 20 Hz. UDP `4005` uses
the same Terraform source-range policy as player TCP `42317`. A client that cannot
establish UDP carries the same binary motion frame over the WebSocket instead.

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
terraform -chdir=C:\work\baseline\infra\gcp\p7 init
terraform -chdir=C:\work\baseline\infra\gcp\p7 plan
terraform -chdir=C:\work\baseline\infra\gcp\p7 apply
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
Set-Location C:\work\baseline\Lumberjacks
& C:\work\dotnet9\dotnet.exe test `
  tests\Game.Gateway.Tests\Game.Gateway.Tests.csproj

dotnet build `
  C:\work\baseline\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj `
  -c Release
```

Victory snapshot: 46 Gateway tests pass; the mod builds with zero warnings and errors.
The Gateway suite currently emits pre-existing Entity Framework version-conflict
warnings.

### 4. Deploy ComfyNetworkSense

Close local Valheim before copying the OMEN DLL. Deploy the server DLL with guarded
backup, restart, readiness, runtime hash, and cold-start hash checks:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\deploy-network-sense.ps1 `
  -ManifestPath `
    C:\work\baseline\fieldlab\runs\releases\p7-primary-v1-0.5.31-clean.json
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

Gateway deploys now promote a prebuilt local Docker image. Do not copy Gateway source
to the VM and do not run `docker compose build` on P7 for normal alpha UI/API changes.

Cut and verify the image locally:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\New-GatewayReleaseCut.ps1 `
  -ImageReleaseId m19-boundarytrace-20260723-r1 `
  -AdmittedModRelease m15-hudrecover-20260723-r1
```

Then promote the already-verified image to P7:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\Promote-GatewayImage.ps1 `
  -Image lumberjacks-gateway:m19-boundarytrace-20260723-r1 `
  -AdmittedModRelease m15-hudrecover-20260723-r1
```

The promotion saves the local image, verifies the archive SHA-256 after upload, loads
the image on P7, removes duplicate `LUMBERJACKS_GATEWAY_IMAGE` lines before writing
the new durable pin, restarts only `gateway` with `--no-build --no-deps`, and verifies
both `/health` and the exact running image id. The previous environment file is backed
up under `/mnt/comfy-p7/backups/gateway-image-promote/<timestamp>/environment` and is
restored automatically if the remote transaction fails.

`scripts\deploy-gateway.ps1` is retained for historical reference only. It still copies
source into `/opt/lumberjacks-ed83bd8` and builds on the VM, which is the stale path
that this image-promotion lane replaces.

### 6. Enroll the player

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\new-player-invite.ps1
```

The script authenticates locally on GCP over SSH and returns a one-use, 24-hour URL.
The player follows the link, signs in with Steam OpenID, and downloads the
personalized mod pack. For already-installed testers, use `/join/update`; that path
downloads the latest mod files without rotating the existing access key. See
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
& C:\work\baseline\infra\gcp\p7\scripts\start-direct-session.ps1
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

### 10. Run the player-motion canary

Use [VALHEIM-MOTION-CANARY.md](VALHEIM-MOTION-CANARY.md) for the two-client,
observe-first A/B. Do not begin with `APPLY` enabled. First prove both clients send,
the Gateway receives, and the peer receives; then compare UDP to WebSocket fallback;
only then enable apply on one observer. The in-game truth strip and the community
dashboard trace are the two acceptance surfaces.

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

The mod rolls back by restoring its backed-up DLL pair:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\rollback-network-sense.ps1 `
  -BackupPath /mnt/comfy-p7/backups/comfynetworksense/<timestamp>
```

**The gateway rolls back by re-pinning its image, never by rebuilding.** Use phase 3
of the promotion drill, which re-pins the historical image already on the VM, brings
it up with `--no-build`, and verifies both `/health` and the exact image id:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
  -BundleRoot <bundle> -Execute `
  -RollbackImageId <sha256:...> -RollbackModSha256 <sha256> `
  -RollbackModBackupPath /mnt/comfy-p7/backups/comfynetworksense/<timestamp>
```

There used to be a standalone `rollback-gateway.ps1` here. It was deleted rather than
repaired: it copied source onto the VM and ran `docker compose build gateway`, which
this stack structurally forbids — the compose file pins every service by image and
carries no `build:` stanza — so it could only fail, and it failed *after* the copy had
already mutated `/opt`. Its `-SourceRoot` also defaulted to `/opt/lumberjacks-ed83bd8`,
a frozen historical commit. `configure-player-gateway.sh` was deleted for the same
reason and a hardcoded public IP besides.

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
