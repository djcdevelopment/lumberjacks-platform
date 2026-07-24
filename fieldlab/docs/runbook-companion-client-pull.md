# Lumberjacks Companion and client-pull modpack releases

This is the alpha update path for OMEN and i5. It deliberately separates frequent mod/config
changes from Gateway image releases.

## Start the local Companion

Native Windows is the normal operator/tester path:

```powershell
cd C:\work\baseline\Lumberjacks
dotnet run --project src\Game.Companion --urls http://127.0.0.1:8080
Start-Process http://127.0.0.1:8080
```

The dashboard, boundary trace, roadmap, and telemetry remain available under the same local origin.
The update screen reads the installed ComfyNetworkSense enrollment config, so an ordinary update
does not ask the tester to re-enter a Steam credential.

The home page's **Moving parts** panel is also the first transport sanity check. Its **Current read**
line intentionally translates live counters into an operator conclusion:

- `P7 is up with no active peers` means the server is ready for a join test.
- `Valheim has N peer(s), but Lumberjacks motion counters are zero` means visible player movement is
  still native Valheim for that run.
- `Lumberjacks motion frames are arriving` means the UDP/WebSocket motion lane is carrying frames
  and the in-game movement result should be compared against the Motion tile and trace.
- `Motion telemetry is unavailable` means the dashboard cannot safely answer the transport question.

The **Capture transport evidence** card is the no-shell path for builders. Click **Capture 60
seconds** before moving two clients. Companion keeps polling while the button is disabled, then shows
`verdict`, `final_current_read`, `sample_count`, `max_peers`, and `motion_received_delta` with direct
downloads for `summary.json` and `samples.jsonl`. Use those downloads when sharing a run in Discord
or attaching evidence to a future issue. The card also lists recent local captures after refresh, so
a tester can recover the download links without finding the Docker volume path.

The **Wave 0 live gate** card also exposes two read-only handoff downloads:

- `/api/v0/companion/wave0/packet.md`
- `/api/v0/companion/wave0/packet`

Those packets are generated from the same local/P7 status as the page. They do not execute shell
commands and they do not embed raw receipts; they summarize the current verdict, ready checks, exact
remaining commands, stop conditions, and the human observations still required for the two-client
apply/observe proof.

## Bootstrap a tester Docker Companion

The normal tester package is a generic zip, not a copied plugin folder and not an image-specific
configuration bundle. Build it from the repository:

```powershell
cd C:\work\baseline\Lumberjacks
.\tools\companion\New-CompanionBootstrap.ps1 -ReleaseId companion-20260723-r1
```

Publish the resulting zip and its adjacent JSON SHA-256 manifest as immutable release artifacts.
For the current private-alpha channel, this command creates the paired GitHub release assets:

```powershell
.\tools\companion\Publish-CompanionBootstrap.ps1 -ReleaseId companion-bootstrap-20260723-r1
```

Use `-DryRun` to generate and inspect the hash-recorded bundle without contacting GitHub.
After a real publish, `Lumberjacks/tools/companion/latest-bootstrap.json` is rewritten as the stable
pointer to the current tester bootstrap. It records the release URL, zip URL, manifest URL, SHA-256,
size, and entrypoint. Treat that file as the canonical answer to "which Companion zip should a tester
download?"
The tester extracts the zip and double-clicks
`bootstrap\Start-LumberjacksCompanion.cmd`. The launcher finds the default Steam Valheim folder,
starts Docker Desktop, starts the loopback-only Companion, and opens `http://127.0.0.1:8080`.
It never carries a Steam credential, access key, or machine-specific config. Their existing
ComfyNetworkSense configuration remains in the Valheim install and is read locally.

The launcher intentionally preserves an existing `docker-compose.valheim.yml` next to the
extracted bundle, so a relaunch cannot silently replace a local override.
It searches the default Steam install plus Steam's configured extra libraries. For an unusual
installation, start `bootstrap\Start-LumberjacksCompanion.ps1` with
`-ValheimPath C:\path\to\Valheim`.

The Companion service runs with Docker Compose `init: true`. This is intentional: on 2026-07-23 the
i5 test laptop produced a zombie Companion container that Docker Desktop could not restart or remove
cleanly. The init process gives future containers normal signal forwarding/reaping behavior and
reduces the chance that a rapid rebuild/recreate loop leaves Docker stuck.

If a remote i5 sync leaves `lumberjacks-companion-companion-1` in `Created` or Docker CLI calls hang,
do not ask the operator to KVM files. Treat it as Docker Desktop recovery:

```powershell
tools\i5\Repair-I5DockerDesktop.ps1
```

The repair script runs over the existing i5 SSH lane, restarts only Docker Desktop
service/processes when required, waits for `docker info --format '{{.OSType}}'` to answer `linux`,
recreates the Companion with both compose files, and verifies the Wave 0 packet endpoint. Use
`-NoCompanionStart` when the desired receipt is Docker-engine health only. Do not retry-loop
indefinitely; the i5 is a roaming optional test client.

## Publish a mod/config package

1. Build and test the package locally. Do not publish a package that has not been exercised on a
   disposable local Valheim install.
   If the running Gateway is intentionally pinned to an already-admitted mod release, keep
   `-ModRelease` equal to the DLL's baked `ComfyNetworkSense.ReleaseId` and use `-ReleaseId` only as
   the package pointer id. This allows frequent client-pull package updates without forcing a
   Gateway image restart or changing release-admission compatibility.
2. Run:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\Publish-Modpack.ps1 `
  -ReleaseId m18-companion-20260723-r1 `
  -ModRelease m18-companion-20260723-r1 `
  -PackagePath C:\path\to\Comfy-P7-Alpha-Mods.zip
```

3. Verify the public manifest has the exact release and SHA-256:

```powershell
Invoke-RestMethod https://comfy-p7.duckdns.org/api/v0/valheim/modpack/manifest
```

4. On a test machine, stop Valheim and open `http://127.0.0.1:8080`. Confirm the three automatic
   readiness checkboxes, explicitly check **I have closed Valheim**, choose **Check for updates**,
   then **Install latest**. The explicit checkbox is required for Docker-backed Companion instances:
   a container cannot reliably observe the Windows host game process. The Companion verifies the
   package hash, preserves the personalized ComfyNetworkSense config, and records a backup/receipt
   under its local data directory. Both install and rollback endpoints reject a request that lacks
   the matching `game_closed_confirmed` confirmation.

## Restart rules

| Change | Gateway restart | Valheim restart |
| --- | --- | --- |
| Published package/config only | No | Usually, if a DLL changed |
| Published static config only | No | Relaunch before testing |
| Gateway code | Yes, through the image promotion path | No, unless the admitted mod changes |

`current.json` is atomically replaced on P7 and read by the Gateway per request. It is the only
runtime pointer for the current client-pull package. `releases.jsonl` is append-only history.
