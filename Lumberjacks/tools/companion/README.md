# Lumberjacks Companion

The Companion is the local alpha control plane. It preserves the familiar
`http://127.0.0.1:8080` dashboard while adding a client-pulled, hash-verified mod update
path. It never publishes a local port beyond loopback.

`http://127.0.0.1:8080/trace` is the trace-first dashboard. It uses private boundary
diagnostics when the configured Gateway URL can reach them; otherwise it falls back to the
public community live trace. For the full operator dashboard, start the P7 tunnel and set
`LUMBERJACKS_COMPANION_GATEWAY_URL=http://host.docker.internal:14000` for the Docker service.

## Docker on Windows (preferred for OMEN and i5)

This is the verified alpha path. It uses the repository's .NET 9 SDK container, so the host does
not need a matching .NET SDK installed.

```powershell
cd C:\work\baseline\Lumberjacks
.\tools\companion\Start-LocalCompanion.ps1
Start-Process http://127.0.0.1:8080
```

`Start-LocalCompanion.ps1` starts the canonical `lumberjacks-companion` compose project with the
Valheim mount and retires the known legacy read-only project named `companion` when it was created
from this same compose file. Without the Valheim compose override, Docker is a read-only local
dashboard and can start with no Valheim path at all. With it, Docker uses the same updater and
persistent state, but cannot reliably observe the Windows Valheim process. Stop Valheim, then
explicitly check **I have closed Valheim** before selecting **Install latest**.

For the i5 laptop, do not hand-copy files. Use the documented tailnet deploy lane from
`C:\work\baseline`:

```powershell
.\tools\i5\Test-I5Link.ps1
.\tools\i5\Deploy-ToI5.ps1 -Path .\Lumberjacks\src\Game.Companion\CompanionPage.cs -Dest C:/deploy/baseline/i5-companion/src/Game.Companion
.\tools\i5\Start-I5Companion.ps1
```

## Native Windows (only when .NET 9 SDK is installed)

```powershell
cd C:\work\baseline\Lumberjacks
dotnet run --project src\Game.Companion --urls http://127.0.0.1:8080
Start-Process http://127.0.0.1:8080
```

The native process discovers the default Steam Valheim directory. Set
`LUMBERJACKS_VALHEIM_PATH` when Steam is installed elsewhere. The updater refuses to write while
Valheim is running, preserves the ComfyNetworkSense config, verifies the release SHA-256, and
keeps overwritten files under `%LOCALAPPDATA%\Lumberjacks\Companion\backups`.

## Generic Windows bootstrap bundle

This is the tester-facing Docker path: extract the published bundle, then double-click
`bootstrap\Start-LumberjacksCompanion.cmd`. It finds the default Steam Valheim installation,
starts Docker Desktop if needed, preserves an existing local compose override and Valheim config,
then opens `http://127.0.0.1:8080`. It does not contain a credential or a personalized config.
It also scans Steam's additional libraries. If Valheim is installed somewhere unusual, launch the
PowerShell entry point with `-ValheimPath C:\path\to\Valheim`.

Build a release bundle from this checkout with:

```powershell
cd C:\work\baseline\Lumberjacks
.\tools\companion\New-CompanionBootstrap.ps1 -ReleaseId companion-20260723-r1
```

The command writes a zip and adjacent SHA-256 manifest under `tools\companion\dist`. Publish
those immutable artifacts through the chosen release channel; do not embed them in a Gateway image.

For the current private-alpha GitHub release channel, build and publish both artifacts together:

```powershell
.\tools\companion\Publish-CompanionBootstrap.ps1 -ReleaseId companion-bootstrap-20260723-r1
```

Use `-DryRun` to inspect the generated package, hash manifest, and GitHub command first. GitHub
release assets are the operator archive; private GitHub releases are not a clean alpha-tester
download path.

Publish the same credential-free zip to P7 for testers:

```powershell
cd C:\work\baseline
.\infra\gcp\p7\scripts\Publish-CompanionBootstrap.ps1 -ReleaseId companion-bootstrap-20260723-r18
```

That writes a hash-verified runtime pointer under the existing Gateway artifact mount, without
embedding the zip in a Gateway image. Once a Gateway image with the public bootstrap endpoints is
deployed, testers use:

- `/join/update` for the human-facing download box.
- `/api/v0/companion/bootstrap/manifest` for the public manifest.
- `/api/v0/companion/bootstrap/package` for the public zip.

Frequent Valheim DLL/config updates remain on the authenticated Gateway client-pull lane inside
Companion.

## Transport evidence captures

The Companion home page exposes three capture presets:

- **15s smoke**: quick validation that Gateway, Valheim, cutover, and motion telemetry are readable.
- **60s movement**: normal two-client movement test window.
- **180s session**: longer alpha-test sample when a tester is intentionally exercising portals,
  terrain, combat, or sustained movement.

Each capture writes local `summary.json` and `samples.jsonl` files. The summary includes release
identity, observed player names, counter deltas, and an interpretation block with the next operator
action. A `native_motion_only` verdict means Valheim peers were present but Lumberjacks motion
counters did not advance during that window; visible movement should be treated as native Valheim
for that capture.

`latest-bootstrap.json` is the stable machine-readable pointer for the current tester bootstrap.
The P7 publisher rewrites it after a successful public upload with the `/join/update` page, public
zip URL, public manifest URL, SHA-256, size, and entrypoint. The GitHub publisher still writes the
same shape for operator archives, but private GitHub URLs are not the tester path. If a tester asks
"which Companion zip do I download?", use that file first; if it points at an older release, publish
a new bootstrap or commit the corrected pointer.

## Current scope

- Client-pulled package checks and installs use the existing installed enrollment credential.
- Existing installs can claim their local enrollment as a Companion profile without storing or
  displaying the access key. The existing browser enrollment remains the source of first-install
  credentials; Steam callback-to-localhost pairing is the next identity increment.
- Gateway images are not the normal delivery lane for mod/config updates once the runtime manifest
  is published.
