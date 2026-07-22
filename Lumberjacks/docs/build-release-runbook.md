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

## Failure handling

- `NETSDK1045` from a host command means the host SDK is too old; use the Docker
  commands above.
- A failed `verify` target means no image should be promoted.
- A failed release-identity verifier means the image must not be deployed.
- Do not manually copy a locally built Gateway DLL into a release; the Docker
  image is the artifact that ships.
