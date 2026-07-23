# Containerized build and release runbook

The .NET build, test, and published service images use `Lumberjacks/Dockerfile`
with `mcr.microsoft.com/dotnet/sdk:9.0`. Do not use a host `dotnet build` as the
release check; a host with only SDK 8 will fail with `NETSDK1045` before the
container path is reached.

## Verify the solution

From `Lumberjacks/`:

```powershell
.\scripts\build.ps1 -Target Verify
```

The `verify` target restores the solution, builds all .NET projects and test
projects, then runs the tests. The test source is intentionally included in the
Docker build context; `bin/` and `obj/` remain excluded.

The ordering in `.dockerignore` is load-bearing: `tests/**/bin` and `tests/**/obj`
must be excluded *after* the broad `!tests/**` source include. Otherwise a host-generated
Windows `project.assets.json` can overwrite the container's Linux restore graph and make
the mandatory image gate fail with `NETSDK1064` even though `dotnet restore` succeeded.

## Build a promotable Gateway image

The release scripts remain the authority for release identity and image
verification:

```powershell
& ..\infra\gcp\p7\scripts\New-GatewayReleaseCut.ps1 `
  -ImageReleaseId m1-boundary-20260722-r1 `
  -AdmittedModRelease m1-clean-20260717-r2
```

For a coupled mod and Gateway cut:

```powershell
& ..\infra\gcp\p7\scripts\New-ReleaseCut.ps1 `
  -ReleaseId m1-boundary-20260722-r1
```

These scripts build the shipping image with `--target gateway`. That target
inherits the `verify` stage, so compilation and tests run before publication.
The image is then checked for the baked release identity. `bin/Release` is only
advisory and is not a release artifact.

For a direct image build outside the release-cut scripts:

```powershell
.\scripts\build.ps1 -Target GatewayImage `
  -ReleaseId m1-boundary-20260722-r1
```

## Build the other service images

The same Dockerfile targets are available for `simulation`, `eventlog`,
`progression`, and `operatorapi`. The P7 release script builds and tags those
images alongside Gateway, then the existing release-bundle and promotion
runbooks record and deploy the resulting digests.

## Promote a Gateway-only P7 image

Use this only when the mod stays frozen and only Gateway changes. Do not use the
older source-copy `deploy-gateway.ps1` path for changes that add files, because
that script copies a narrow source-file allowlist and rebuilds on the VM.

1. Cut and verify the image from `main`:

   ```powershell
   & ..\infra\gcp\p7\scripts\New-GatewayReleaseCut.ps1 `
     -ImageReleaseId m3-boundary-20260722-r1 `
     -AdmittedModRelease m5-recipients-20260720-r1
   ```

2. Save and transfer the image plus the updated compose file:

   ```powershell
   docker save --output $env:TEMP\lumberjacks-gateway-m3-boundary-20260722-r1.oci.tar `
     lumberjacks-gateway:m3-boundary-20260722-r1

   scp $env:TEMP\lumberjacks-gateway-m3-boundary-20260722-r1.oci.tar `
     comfy-p7:/tmp/lumberjacks-gateway-m3-boundary-20260722-r1.oci.tar

   scp ..\infra\gcp\p7\docker-compose.yml `
     comfy-p7:/tmp/docker-compose-m3-boundary-20260722-r1.yml
   ```

3. On the VM, back up compose/env, load the image, update the durable pin, and
   restart only Gateway:

   ```bash
   stamp=20260722T0952Z
   backup=/mnt/comfy-p7/backups/gateway-boundary/$stamp
   compose_root=/opt/comfy/infra/gcp/p7
   env_file=/etc/comfy-p7/environment
   image_tag=lumberjacks-gateway:m3-boundary-20260722-r1
   image_id=sha256:a2bf1856f5e5c7c2e9fae7e969746d4c724138a9bebf915c80429389a1a4c5f5

   sudo install -d -m 0750 "$backup"
   cd "$compose_root"
   sudo cp -a docker-compose.yml "$backup/docker-compose.yml"
   sudo cp -a "$env_file" "$backup/environment"
   sudo install -d -m 0750 /mnt/comfy-p7/lumberjacks/boundary-events
   sudo docker load --input /tmp/lumberjacks-gateway-m3-boundary-20260722-r1.oci.tar
   sudo docker tag "$image_id" "$image_tag"
   sudo install -m 0644 /tmp/docker-compose-m3-boundary-20260722-r1.yml docker-compose.yml
   sudo sed -i 's|^LUMBERJACKS_GATEWAY_IMAGE=.*|LUMBERJACKS_GATEWAY_IMAGE=lumberjacks-gateway:m3-boundary-20260722-r1|' "$env_file"
   sudo sed -i 's|^LUMBERJACKS_VERSION=.*|LUMBERJACKS_VERSION=m3-boundary-20260722-r1|' "$env_file"
   sudo sed -i 's|^LUMBERJACKS_ALPHA_SEAT_GATE=.*|LUMBERJACKS_ALPHA_SEAT_GATE=disabled|' "$env_file"
   sudo docker compose --env-file "$env_file" config >/tmp/docker-compose-m3-boundary-20260722-r1.rendered.yml
   sudo docker compose --env-file "$env_file" up -d --no-build --no-deps gateway
   curl --fail --silent http://127.0.0.1:4000/health
   sudo docker inspect comfy-lumberjacks-p7-gateway-1 --format '{{.Image}} {{index .Config.Image}}'
   ```

   `LUMBERJACKS_ALPHA_SEAT_GATE=disabled` is the preferred durable alpha setting.
   It disables the temporary one-seat Gateway reservation gate; it does not change
   Valheim's native max-player value. Keep `VALHEIM_HANDSHAKE_SEAT_CAPACITY=0` in
   the VM env only while older Gateway images remain valid rollback targets.

4. Confirm boundary events are durable and parse complete rows:

   ```powershell
   ssh comfy-p7 "sudo ls -la /mnt/comfy-p7/lumberjacks/boundary-events"
   ssh comfy-p7 "sudo bash -lc 'head -n -1 /mnt/comfy-p7/lumberjacks/boundary-events/20260722-000001.open.jsonl > /tmp/boundary-events-p7-complete-rows.jsonl; chmod 0644 /tmp/boundary-events-p7-complete-rows.jsonl'"
   scp comfy-p7:/tmp/boundary-events-p7-complete-rows.jsonl $env:TEMP\boundary-events-p7-complete.jsonl
   node .\scripts\boundary-events.mjs check $env:TEMP
   ```

   For the operator dashboard, verify both the basic stream health and the ZDO
   movement counters through the OMEN proxy:

   ```powershell
   Invoke-RestMethod http://127.0.0.1:8080/ops/boundary/summary |
     Select rows, malformed_rows, truncated_rows, proxy_boundary_warnings,
       writer_dropped_rows, writer_faults

   (Invoke-RestMethod http://127.0.0.1:8080/ops/boundary/summary).zdo_totals
   ```

   Expected shape: identity/auth/request rows are always present on an active
   Gateway; `zdo.batch.queued`, `zdo.batch.polled`, `zdo.batch.acknowledged`, and
   `zdo.consumer.heartbeat` appear once the Valheim server and at least one enrolled
   client are actively producing/polling through Lumberjacks.

5. Run the proxy boundary canary:

   ```powershell
   Invoke-WebRequest http://8.231.129.249:42317/api/v0/enrollment -UseBasicParsing
   Invoke-WebRequest https://comfy-p7.duckdns.org/api/v0/enrollment -UseBasicParsing
   ```

   Expected on 2026-07-22: direct public returns `401`; TLS through Caddy returns
   `200`, proving Caddy still makes the request look private to Gateway. This is
   accepted for the known-cohort alpha only and remains a stop-ship before
   widening.

## Download the current player mod pack

Ordinary player updates are Steam-bound and do not rotate the installed access key:

```text
https://comfy-p7.duckdns.org/join/update
```

The same surface exposes a secret-free machine-readable manifest for dashboards and
pull updaters:

```text
https://comfy-p7.duckdns.org/api/v0/valheim/modpack/manifest
```

The update zip omits `djcdevelopment.valheim.comfynetworksense.cfg` and includes
`Install-LumberjacksMod.ps1`, which preserves the local config while copying the
latest files. This is the path for OMEN, i5, and alpha testers who already completed
first install.

## Issue an admin rescue mod pack

Use this when a known alpha tester is already enrolled but the Steam callback,
reissue page, or old local config is blocking recovery. The endpoint is on the
admin enrollment surface and rotates the tester's client credential, so any
previously installed config for that enrollment stops working after the new pack is
issued. Do not use it for ordinary updates.

Prefer `steam_id` when the operator is repairing the tester's current active
enrollment. Use `enrollment_id` only when selecting an exact enrollment record
from the admin list.

```powershell
$admin = '<admin key>'
$body = @{ steam_id = '<steam id 64>' } | ConvertTo-Json
Invoke-WebRequest `
  -Method POST `
  -Uri 'https://comfy-p7.duckdns.org/api/v0/enrollment/pack' `
  -Headers @{ 'X-Lumberjacks-Admin-Key' = $admin } `
  -ContentType 'application/json' `
  -Body $body `
  -OutFile '.\Comfy-P7-Mods.zip'
```

Fallback through the direct player port is equivalent when TLS/Caddy is the
thing being debugged:

```powershell
Invoke-WebRequest `
  -Method POST `
  -Uri 'http://8.231.129.249:42317/api/v0/enrollment/pack' `
  -Headers @{ 'X-Lumberjacks-Admin-Key' = $admin } `
  -ContentType 'application/json' `
  -Body $body `
  -OutFile '.\Comfy-P7-Mods.zip'
```

Operator checks:

- The response must be a zip file, not a JSON error body.
- The tester extracts the `Valheim` folder into the Steam Valheim install
  directory and lets it merge.
- Because the credential rotated, do not expect the tester's older install to
  keep authenticating.

For first install, send the tester an invite URL. If the bootstrap expires before
install, `/join/reissue` redoes Steam sign-in and mints a fresh one-use download.

## Failure handling

- `NETSDK1045` from a host command means the host SDK is too old; use the Docker
  commands above.
- A failed `verify` target means no image should be promoted.
- A failed release-identity verifier means the image must not be deployed.
- Do not manually copy a locally built Gateway DLL into a release; the Docker
  image is the artifact that ships.
