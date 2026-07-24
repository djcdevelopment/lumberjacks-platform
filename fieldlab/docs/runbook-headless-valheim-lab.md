# Disposable headless Valheim lab lane

This is the unattended local lane for M7 authority experiments. It is not the
alpha-player install path and it must not be copied to OMEN/i5 physical clients.

## Preconditions

- Docker Desktop is running.
- The selected `fieldlab/autonomous/state/clientNN/games` volume already contains
  a Steam login and Valheim installation. The first Steam login/game install is a
  one-time lab seeding operation; the agent cannot infer credentials.
- The lab server/gateway are reachable on the Compose network.
- The client has an existing Valheim character profile. Lab autojoin never creates
  one.

The repository records this as a machine-readable operator-touch gate. Run it
before a client start so a missing install, stale payload, dead server, or dead
Gateway is reported before anyone is asked to open Valheim:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action preflight
```

The receipt is written to
`fieldlab/autonomous/state/client01/lab-preflight.json`. A blocked result is a
real prerequisite failure, not an invitation to retry or log in again.

If the gate reports the one-time seed is missing, use the explicit escape hatch
once for the disposable volume only:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action start -AllowUnseeded
```

Use the client VNC endpoint (`http://127.0.0.1:8081`) only to complete the Steam
install/login and create or copy one ordinary Valheim character. Then stop the
client and rerun `preflight`. This is the only expected human interaction in
the lane; physical OMEN/i5 installs are not modified.

## Lifecycle

From `C:\\work\\baseline`:

```powershell
# Build and stage the current DLL; optionally add an explicit lab config with -ConfigPath.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action refresh

# Re-check all prerequisites immediately before the run.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action preflight

# Start one disposable rendered/headless client.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action start

# Inspect container lifecycle and recent startup/Valheim logs.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action status

# Stop and verify the client container is no longer running.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action stop

# After stop, normalize and replay the retained native probe automatically.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action capture
```

For a smoke-only proof that launch and the existing-character selector work, use
`-Action smoke`. It runs the gate, starts the client, waits for the
`Lab auto-join started existing character` marker, holds briefly, and always
stops the container. It does not replace the MCP loop below.

The script stages a writable, SHA-256-verified copy of the client-init watcher in
the client's home volume. That is required by `josh5/steam-headless:debian`, whose
entrypoint normalizes and chowns user-init scripts. The watcher stages the shared
DLL/config, launches Valheim with `+connect`, and leaves character selection to the
bounded `[LabAutoJoin]` patch.

## Two-client lab run

The thin coordinator is the preferred entry point once both disposable volumes
are seeded. It refreshes every selected client, preflights every client, and only
then starts any of them. A failure in either gate prevents all starts; a failure
after a partial start stops the clients that were already started.

```powershell
# Check both clients without starting either one.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimScenario.ps1 -Action preflight -Clients '01,02'

# Refresh both clients and start them only if both gates are green.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimScenario.ps1 -Action start -Clients '01,02' -NoBuild

# After the agent has observed and commanded both clients through MCP:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimScenario.ps1 -Action stop -Clients '01,02'

# Normalize/replay each retained probe after closure.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimScenario.ps1 -Action capture -Clients '01,02'
```

Each client retains its own `clientNN/lab-preflight.json`; the coordinator writes
an aggregate receipt under `fieldlab/autonomous/state/multi-*.json`. The coordinator
does not issue gameplay commands. MCP remains the bounded control plane between
start and stop.

## Agent/MCP loop after the client is up

The gateway receives `COMFY_AUTONOMOUS_STATE=/lab/state` and exposes:

- `valheim_mcp_health()` — confirms the gateway and lab state surface;
- `valheim_swarm_clients(max_clients=2)` — discovers client paths and VNC URLs;
- `valheim_lab_motion_status(client="client01")` — reads pending command and
  JSONL receipts;
- `valheim_lab_motion_test(client="client01", action="start", pattern="stutter_north", duration_seconds=10)` — writes one atomic, allow-listed command;
- `valheim_tail_swarm_client(client="client01")` — reads the resulting telemetry.

Allowed motion patterns are `straight_north`, `straight_east`, `stutter_north`,
and `circle`, with durations limited to 1–60 seconds. The mailbox is consumed by
`MotionTestController` on Unity's main thread. There is no arbitrary console,
shell, teleport, or model-output execution path.

The intended agent-owned sequence is:

```text
refresh -> preflight -> start -> MCP observe/command -> MCP telemetry -> stop -> capture
```

The agent can keep the client open while it issues bounded mailbox commands and
reads JSONL. The final `stop` is mandatory before file refresh or evidence
capture, and the capture step invokes the same AuthorityLab Docker build lane
used by the synthetic experiments. No file needs to be copied between machines.

## Evidence and shutdown

Keep the mod's JSONL, MCP receipt, and container lifecycle output together for a
run. A green preflight is the permission to start; a green stop result is the
closure receipt; and the capture output contains the raw native source,
normalization receipt, and observation-only Lumberjacks replay. Missing native
probe evidence remains inconclusive rather than being reported as a failed
authority comparison.
