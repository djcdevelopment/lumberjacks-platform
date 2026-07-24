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

## Lifecycle

From `C:\\work\\baseline`:

```powershell
# Build and stage the current DLL; optionally add an explicit lab config with -ConfigPath.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action refresh

# Start one disposable rendered/headless client.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action start

# Inspect container lifecycle and recent startup/Valheim logs.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action status

# Stop and verify the client container is no longer running.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\fieldlab\\scripts\\Invoke-HeadlessValheimLab.ps1 -Client 01 -Action stop
```

The script stages a writable, SHA-256-verified copy of the client-init watcher in
the client's home volume. That is required by `josh5/steam-headless:debian`, whose
entrypoint normalizes and chowns user-init scripts. The watcher stages the shared
DLL/config, launches Valheim with `+connect`, and leaves character selection to the
bounded `[LabAutoJoin]` patch.

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

## Evidence and shutdown

Keep the mod's JSONL, MCP receipt, and container lifecycle output together for a
run. If the install is missing, the watcher reports that prerequisite and waits;
do not treat that as a network failure. Stop the client before refreshing files or
restarting a run. The script's green stop result is the closure receipt.
