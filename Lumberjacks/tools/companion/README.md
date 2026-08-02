# Lumberjacks Companion

The Companion is the local alpha control plane. It preserves the familiar
`http://127.0.0.1:8080` dashboard while adding a client-pulled, hash-verified mod update
path. It never publishes a local port beyond loopback.

`http://127.0.0.1:8080/trace` is the trace-first dashboard. It uses private boundary
diagnostics when the configured Gateway URL can reach them; otherwise it falls back to the
public community live trace. For the full operator dashboard, start the P7 tunnel and set
`LUMBERJACKS_COMPANION_GATEWAY_URL=http://host.docker.internal:14000` for the Docker service.

`http://127.0.0.1:8080/workbench` is the source-aware operator map. It shows the active goal,
milestone hierarchy, feature/source pointers, physical and headless execution lanes, the current
Docker source revision/branch/image, and the reconstruction steps. The local workbench is the
primary operator surface; `/join/update` and its Steam/OpenID callback remain on the public Gateway.
The Companion receives only the installed profile association and redacted status, never Steam
credentials or raw access keys.

The Workbench is the one-stop local control surface. A fresh launch defaults to **Explore**;
`-Profile Admin` enables the claimed owner maintenance lane, while `-Profile Dev` and
`-Profile Lab` additionally start the loopback-only project Dev MCP and SDK tool-runner. Use
`-Profile Production` for a player-facing appliance: the Dev MCP, source checkout mount, and
build runner are absent. The browser Standard/Advanced toggle changes presentation only and
cannot grant a profile capability.

### Current appliance boundary (do not infer it from one green page)

The operating experience is intended to be one Workbench, but the current local runtime is
still launched as three Compose projects: `lumberjacks-companion` for the web surface and
profile-gated Dev tools, `lumberjacks-local` for Gateway/services/Postgres, and
`comfy-valheim-lab` for the active Valheim server. Multiple service images are compatible with
PD-5; multiple competing launch/recovery stories are not. `Start-LocalCompanion.ps1` currently
converges only the first project and selects a Gateway origin. It does not yet prove or launch
the complete local appliance.

The finite convergence work is tracked in
[`plans/workbench-appliance-convergence.md`](../../../plans/workbench-appliance-convergence.md):
one versioned distribution and launcher, one port/mode manifest, explicit Local/Remote/Hybrid
Lab selection, shipped runtime tools, preserved state migration, and clean-machine/human
acceptance. HEARTH remains an independent machine-wide MCP on `8710`; Baseline's Dev/Lab MCP is
the separate project-owned endpoint on loopback `8721`.

## Docker on Windows (preferred for OMEN and i5)

This is the verified alpha path. It uses the repository's .NET 9 SDK container, so the host does
not need a matching .NET SDK installed.

```powershell
cd C:\work\baseline\Lumberjacks
.\tools\companion\Start-LocalCompanion.ps1
Start-Process http://127.0.0.1:8080
```

Developer/lab launch (explicitly opt in):

```powershell
.\tools\companion\Start-LocalCompanion.ps1 -Profile Dev
```

`Lab` favors the local engineering loop: it selects
`http://host.docker.internal:4000` when no Gateway URL is explicitly supplied.
Explore, Admin, Dev, and Production retain the public release Gateway default.
Use `-GatewayUrl <origin>` (or `LUMBERJACKS_COMPANION_GATEWAY_URL`) when a different
verified Gateway is intentional; the selected origin is printed during launch and
projected in Workbench topology.

To admit an already-built package to the local Gateway without contacting GCP or
writing into Valheim, publish it into the persistent local Gateway volume:

```powershell
.\tools\modpack\Publish-LocalModpack.ps1 `
  -ReleaseId m30-rolecontrol-20260723-r1 `
  -ModRelease m30-rolecontrol-20260723-r1 `
  -PackagePath ..\artifacts\modpacks\Comfy-P7-Alpha-Mods-m30-rolecontrol-20260723-r1.zip
```

The script verifies the package SHA-256 inside the Gateway container and atomically
replaces `/data/modpack/current.json`. A Lab **Check admitted mod update** job can
then exercise the real Gateway/Companion manifest path. Publishing changes only the
local release feed; install and rollback remain separate, explicitly confirmed
player-impacting actions.

Before authorizing an install/rollback drill, compare the package with the current
Valheim payload. `-RequireExactMatch` fails if any archive entry is missing or would
change bytes, and always rejects path escapes, duplicate entries, files outside
`Valheim/`, and the personalized credential config:

```powershell
.\tools\modpack\Test-ModpackPayload.ps1 `
  -PackagePath ..\artifacts\modpacks\<package>.zip `
  -RequireExactMatch
```

The browser remains the normal control surface. For a release-engineering
install-to-rollback proof, the bounded verifier drives those same Workbench jobs
and compares the complete installed-state JSON plus every admitted payload byte:

```powershell
# Read-only preflight; exits 2 with authorization_required when all gates are ready.
.\tools\modpack\Invoke-LocalWorkbenchModRollbackDrill.ps1 `
  -PackagePath ..\artifacts\modpacks\<package>.zip `
  -ExpectedRelease <release>

# Player-impacting execution requires the explicit switch.
.\tools\modpack\Invoke-LocalWorkbenchModRollbackDrill.ps1 `
  -PackagePath ..\artifacts\modpacks\<package>.zip `
  -ExpectedRelease <release> `
  -ApprovePlayerImpactingDrill
```

The verifier refuses a dirty Workbench image, a running Windows Valheim process,
manifest/package drift, non-exact live bytes, or pre-existing updater residue.
The host runner independently repeats the Windows process check immediately
before either mutation because the Linux Companion container cannot reliably see
host game processes. The verifier never launches the game, captures player
traffic, or contacts GCP.

Rollback is available only for installs written with the reversible transaction
schema. Older Companion records do not contain a trustworthy prior-release
identity, so the Workbench disables their rollback action and the compatibility
endpoint fails closed rather than applying the same historical backup repeatedly.

The legacy Dev MCP host port is `8720`, but that port is not currently a safe
identity boundary: an enabled `ComfyGatewayBoot` task owns a retired Comfy
checkout there. Baseline Dev/Lab launchers now default to the explicit project
port `8721` (or another explicitly recorded free loopback port). The launcher
checks it after profile convergence and fails clearly if another local MCP owns
it; endpoint identity is then verified by the read-only checker rather than
inferred from reachability. See the
[endpoint provenance audit](../../docs/audit/2026-08-01-mcp-endpoint-provenance-audit.md).
Run `tools\workbench\Test-WorkbenchMcpIdentity.ps1 -Profile Dev -McpPort 8721`
for the authenticated identity receipt, or
`tools\workbench\Test-WorkbenchProfileBoundary.ps1 -Profile Explore` for a
read-only report of any unmanaged listener before normal gameplay.

The host-side `Start-WorkbenchHostRunner.ps1` is a bounded allow-list, not a general shell.
Companion and Dev MCP never receive the Docker socket. The runner is started hidden for the
interactive Windows user and writes durable Workbench job receipts to the retained
`companion-data` volume.

`Start-LocalCompanion.ps1` starts the canonical `lumberjacks-companion` compose project with the
Valheim mount and retires the known legacy read-only project named `companion` when it was created
from this same compose file. Without the Valheim compose override, Docker is a read-only local
dashboard and can start with no Valheim path at all. With it, Docker uses the same updater and
persistent state, but cannot reliably observe the Windows Valheim process. Stop Valheim, then
explicitly check **I have closed Valheim** before selecting **Install latest**.

The launcher stamps the container with the current Git revision, branch, dirty state, and local
image label. This makes a dashboard observation attributable to the source that built it. The
metadata is informational and does not grant the container Git or host control.

For the i5 laptop, do not hand-copy files. Use the documented tailnet deploy lane from
`C:\work\baseline`:

```powershell
.\tools\i5\Test-I5Link.ps1
.\tools\i5\Sync-I5Companion.ps1
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

Use the Workbench **Capture redacted workbench snapshot** action before a meaningful investigation
or reset. It writes an immutable JSON snapshot under the persistent `companion-data` volume. To
reconstruct after rebuilding, keep that volume, run `Start-LocalCompanion.ps1`, then open
`/workbench`, `/trace`, the latest snapshot, and the relevant capture bundle. Do not use
`docker compose down -v` when evidence matters.

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
