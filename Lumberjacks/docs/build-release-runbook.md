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
   sudo docker compose --env-file "$env_file" config >/tmp/docker-compose-m3-boundary-20260722-r1.rendered.yml
   sudo docker compose --env-file "$env_file" up -d --no-build --no-deps gateway
   curl --fail --silent http://127.0.0.1:4000/health
   sudo docker inspect comfy-lumberjacks-p7-gateway-1 --format '{{.Image}} {{index .Config.Image}}'
   ```

4. Confirm boundary events are durable and parse complete rows:

   ```powershell
   ssh comfy-p7 "sudo ls -la /mnt/comfy-p7/lumberjacks/boundary-events"
   ssh comfy-p7 "sudo bash -lc 'head -n -1 /mnt/comfy-p7/lumberjacks/boundary-events/20260722-000001.open.jsonl > /tmp/boundary-events-p7-complete-rows.jsonl; chmod 0644 /tmp/boundary-events-p7-complete-rows.jsonl'"
   scp comfy-p7:/tmp/boundary-events-p7-complete-rows.jsonl $env:TEMP\boundary-events-p7-complete.jsonl
   node .\scripts\boundary-events.mjs check $env:TEMP
   ```

5. Run the proxy boundary canary:

   ```powershell
   Invoke-WebRequest http://8.231.129.249:42317/api/v0/enrollment -UseBasicParsing
   Invoke-WebRequest https://comfy-p7.duckdns.org/api/v0/enrollment -UseBasicParsing
   ```

   Expected on 2026-07-22: direct public returns `401`; TLS through Caddy returns
   `200`, proving Caddy still makes the request look private to Gateway. This is
   accepted for the known-cohort alpha only and remains a stop-ship before
   widening.

## Failure handling

- `NETSDK1045` from a host command means the host SDK is too old; use the Docker
  commands above.
- A failed `verify` target means no image should be promoted.
- A failed release-identity verifier means the image must not be deployed.
- Do not manually copy a locally built Gateway DLL into a release; the Docker
  image is the artifact that ships.
