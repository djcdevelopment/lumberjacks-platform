# Combined Comfy and Lumberjacks P7 environment

Status: P7/I7 live loopback gate passed on GCP, 2026-07-11 local / 2026-07-12 UTC.

This is the current deployment target for the Valheim x Lumberjacks netcode
replacement proof. It is not the original Godot multiplayer vertical slice. The
deployment runs the migrated `ComfyEra16` Valheim dedicated server with
ComfyNetworkSense 0.5.27 and co-located Lumberjacks authority services on one GCP
VM, while OMEN remains the rendered Valheim client and fieldlab controller.

## Current proven deployment

Project: `lumberjacks-exp-20260711-djc`

VM: `comfy-lumberjacks-p7`

Zone: `us-west1-b`

Machine: `n2-highmem-8`

Public Valheim endpoint: `8.231.129.249`

Valheim join endpoint: `8.231.129.249:2456`

Fieldlab SSH target: `comfy-p7` through IAP

Persistent disk mount: `/mnt/comfy-p7`

Server state:

- world: `ComfyEra16`
- server name: `Comfy Era16 Lab`
- Steam-only: `CROSSPLAY=false`, public listing disabled
- mod: `ComfyNetworkSense 0.5.27` (publishes telemetry and consumes authoritative ZDO envelopes)
- proven live binary SHA-256:
  `cde0d458f2c74bc9b3dba3f73ba5bdce5e4df602bf635c42110bd24b8630d72c`

Compose services after deployment:

- `postgres`: internal PostgreSQL on `127.0.0.1:5433`
- `gateway`: Lumberjacks private control gateway on loopback TCP `4000`, public UDP `4005`
  with its authoritative ZDO write-ahead log on the persistent disk at
  `/mnt/comfy-p7/lumberjacks/zdo-queue/redirect.wal`
- `eventlog`: internal service on `127.0.0.1:4002`
- `progression`: internal service on `127.0.0.1:4003`
- `operatorapi`: internal service on `127.0.0.1:4004`
- `valheim-server`: Valheim UDP `2456-2457`, pinned image digest
  `ghcr.io/community-valheim-tools/valheim-server@sha256:e8b13da3c44f54a38511c8ac224f2959a437c0b2626cf916683ca7acc8dfb146`

Runtime entry points:

- player/client: Valheim direct join `8.231.129.249:2456`
- Lumberjacks gateway health through the OMEN tunnel: `http://127.0.0.1:14000/health`
- fieldlab control: `fieldlab/scripts/set-gcp-p7-target.ps1 -PublicIp 8.231.129.249 -SshHost comfy-p7`
- P7 runner: `fieldlab/scripts/run-loopback-window.ps1`
- systemd wrapper: `comfy-lumberjacks-p7.service`

P7/I7 gate result:

- I5 handshake: Lumberjacks-decided accept, steady state reached
- I2 ownership pin: `held_with_negative_control`, 25 pinned, 106 holds
- I3 redirect: `receipts_match_no_loss`, 6316 suppressed, 6316 received, 0 missing, 0 duplicates
- I4 injection: `rendered_with_lumberjacks_owner`, 1 applied/rendered, owner matched
- client stability: clean, no desync/socket/invalid-message matches
- save integrity: pass; portals/spawned/targets/locations exact, ZDO delta within tolerance
- memory: no OOM pressure; about 51 GiB available and swap unused during the gate
- post-gate state: server disarmed back to observe-only baseline

This Terraform root migrates the memory-starved `am4` dedicated-server role to a
64 GiB native-Linux Compute Engine VM. It runs the real combined product:

- the Steam-only Valheim dedicated server and `ComfyEra16` state;
- the ComfyNetworkSense replacement mod already present in the migrated state;
- Lumberjacks Gateway, EventLog, Progression, Operator API, and PostgreSQL; and
- host/container/OTLP telemetry through the Google Cloud Ops Agent.

OMEN remains the rendered Valheim client and fieldlab controller. The Comfy MCP
gateway remains a development observability surface on OMEN and reaches this VM by
SSH; it is not placed in the gameplay path.

The checked-in Compose file pins the exact Valheim image digest observed on `am4`.
The migration procedure must also preserve these source invariants:

| Artifact | Source SHA-256 |
|---|---|
| `ComfyNetworkSense.dll` 0.5.27 | `cde0d458f2c74bc9b3dba3f73ba5bdce5e4df602bf635c42110bd24b8630d72c` |
| root BepInEx configuration | `065e942174d0912ca94d108794b4d59bbdec34e2e21a299a31b63efc6a017d01` |
| `ComfyEra16.db` baseline | `4513d0348e9f740cad22032c476c5dd6f5304490dc05912f35b250837e25d49a` |
| `ComfyEra16.fwl` baseline | `5f323fbe7b627fd50520d8f4f6dedd13027a92bfe056013aa52d7306d09a3539` |

The world hashes are migration baselines, not permanent expected values: after a
clean save during cutover, record a new manifest and require the source and target
archives to match that manifest byte-for-byte.

Copy `terraform.tfvars.example` to ignored `terraform.tfvars`, replace the project,
operator email, and OMEN CIDR, then run `terraform init`, `terraform plan`, and
`terraform apply`. Secrets never belong in Terraform state. Create
`/etc/comfy-p7/environment` on the VM with mode `0600`:

```text
GOOGLE_CLOUD_PROJECT=<project>
LUMBERJACKS_VERSION=<commit-sha>
COMFY_NETWORKSENSE_VERSION=0.5.27
POSTGRES_PASSWORD=<random-stage-password>
VALHEIM_SERVER_PASSWORD=
LUMBERJACKS_ROOT=/opt/lumberjacks
COMFY_LUMBERJACKS_CUTOVER_MODE=mirrored
COMFY_LUMBERJACKS_ENROLLMENT_MANIFEST_ID=i7-live-w2
```

The Compose file passes the two `COMFY_LUMBERJACKS_*` values into the Valheim
container. Keep the mode at `mirrored` until `/api/v0/telemetry/cutover` reports
complete coverage with zero native-only traffic. The enrollment manifest is visible
at `/api/v0/valheim/enrollment/{manifestId}`; it is an advertisement and does not
authorize primary cutover on its own.

Deploy ComfyNetworkSense from OMEN with the guarded script:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\deploy-network-sense.ps1
```

Start the private Gateway/dashboard tunnel on OMEN before launching Valheim:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\start-gateway-tunnel.ps1
```

For the normal password-free primary session, start both the tunnel and Valheim with:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\start-primary-session.ps1
```

Gateway TCP 4000 is bound only to GCP loopback. The tunnel exposes it on OMEN at
`http://127.0.0.1:14000`; `/community`, `/networksense`, health, telemetry, and the
authoritative client poller use that local endpoint. This avoids exposing the HTTP
control/data surface publicly while HTTPS/authentication are still out of scope.

The live server plugin path is
`/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll`. The configuration is
the bind-mounted root BepInEx file at
`/mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg`
(container path `/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg`).
Do not copy the config into the container without restoring UID/GID `1000:1000` and
mode `0664`; otherwise BepInEx aborts the plugin during `ConfigFile.Bind`. The guarded
script enforces ownership, checks the runtime DLL hash, rejects startup access
exceptions, waits for both `Telemetry scaffold ready` and `Game server connected`,
then reconciles the cold-start `COMFY_NETWORKSENSE_VERSION` fallback. Gateway reads
the running version from Valheim's heartbeat, so plugin deployment does not restart
Gateway or discard an in-flight authoritative queue.

Do not start the GCP Valheim container until the source server is cleanly stopped and
the final state archive has been verified. Never allow `am4` and GCP to write the same
world lineage concurrently.
