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

## Publish a mod/config package

1. Build and test the package locally. Do not publish a package that has not been exercised on a
   disposable local Valheim install.
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
