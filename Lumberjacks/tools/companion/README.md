# Lumberjacks Companion

The Companion is the local alpha control plane. It preserves the familiar
`http://127.0.0.1:8080` dashboard while adding a client-pulled, hash-verified mod update
path. It never publishes a local port beyond loopback.

## Native Windows (preferred for OMEN and i5)

```powershell
cd C:\work\baseline\Lumberjacks
dotnet run --project src\Game.Companion --urls http://127.0.0.1:8080
Start-Process http://127.0.0.1:8080
```

The native process discovers the default Steam Valheim directory. Set
`LUMBERJACKS_VALHEIM_PATH` when Steam is installed elsewhere. The updater refuses to write while
Valheim is running, preserves the ComfyNetworkSense config, verifies the release SHA-256, and
keeps overwritten files under `%LOCALAPPDATA%\Lumberjacks\Companion\backups`.

## Docker

```powershell
$env:LUMBERJACKS_VALHEIM_HOST_PATH = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
Copy-Item C:\work\baseline\Lumberjacks\tools\companion\docker-compose.valheim.yml.example `
  C:\work\baseline\Lumberjacks\tools\companion\docker-compose.valheim.yml
docker compose -f C:\work\baseline\Lumberjacks\tools\companion\docker-compose.yml `
  -f C:\work\baseline\Lumberjacks\tools\companion\docker-compose.valheim.yml up --build -d
Start-Process http://127.0.0.1:8080
```

Without the optional second compose file, Docker is a read-only local dashboard and can start with
no Valheim path at all. With it, Docker uses the same updater and persistent state, but cannot
reliably observe the Windows Valheim process; stop Valheim before selecting **Install latest**.

## Current scope

- Client-pulled package checks and installs use the existing installed enrollment credential.
- Existing installs can claim their local enrollment as a Companion profile without storing or
  displaying the access key. The existing browser enrollment remains the source of first-install
  credentials; Steam callback-to-localhost pairing is the next identity increment.
- Gateway images are not the normal delivery lane for mod/config updates once the runtime manifest
  is published.
