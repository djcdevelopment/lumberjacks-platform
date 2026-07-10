# Multiplayer Network Setup — Valheim lab (server + real player + headless netcode probe)

**What this is:** the hard-won runbook for standing up a working two-machine Valheim
multiplayer session AND a headless server-side ComfyNetworkSense netcode probe. Two separate
multi-hour ratholes fed this doc: (1) getting a second real GPU player online at all, and
(2) getting the server-side probe to actually fire once the player was connected. Both had a
single obvious-in-hindsight root cause. Read this before touching the lab topology or
deploying the mod to the dedicated server.

Related: [HANDOFF-PLAYER-ON-NETWORK-AM4-SERVER](handoffs/HANDOFF-PLAYER-ON-NETWORK-AM4-SERVER.md)
· [HANDOFF-I1-INTERCEPTION-REACHABILITY](handoffs/HANDOFF-I1-INTERCEPTION-REACHABILITY.md)
· [VALHEIM-NETCODE-REPLACEMENT-WORKLOG](VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md) · NETCODE-MAP.md

---

## TL;DR — the working topology

| Role | Machine | How it runs | Why |
|---|---|---|---|
| Dedicated server | **am4** (`100.116.82.60`, tailnet) | **native Linux Docker** | Only native Linux Docker publishes UDP to host/external peers. |
| Player two (of record) | **OMEN** (RTX 5070) | **native Windows Valheim** (a real GPU + a screen) | Per Derek 2026-07-09: player two runs on OMEN — no Docker, nothing fancy. Containers with no discrete GPU → llvmpipe; unusable. |
| Optional extra seat | i5 (`100.125.141.110`) | native Windows Valheim | The emergency reference client that produced the I1 run; optional only now. |

- World `ComfyEra16`, pass `comfytest`, connect to `100.116.82.60:2456`.
- **Steam accounts — mind the near-collision (it caused real doc conflicts):** account
  **`waryfool`** carries persona **Zephar410** (associated with am4/server side); account
  **`floooooobcakes`** carries persona **wary.fool** (character `Durracktu`, SteamID
  `76561198088711642`). **To put wary.fool on OMEN, log OMEN's Steam into `floooooobcakes`.**
  One license per account, single-session each.
- Server container: `comfy-valheim-server-am4-valheim-server-1`,
  image `ghcr.io/community-valheim-tools/valheim-server`, compose at `~/comfy-valheim-lab/server-compose.yml`.

---

## THE TWO TRAPS (both cost hours; both obvious in hindsight)

### Trap 1 — Docker Desktop / Windows does not publish UDP
The server ran fine on OMEN's Docker Desktop, clients could reach TCP, firewall rules were
added — and clients **still** could never join. **Docker Desktop on Windows does not publish
`2456/udp` to the host or external peers.** The firewall was a red herring. Fix: run the UDP
server on **am4 (native Linux Docker)**. Verify real listeners with `ss -ulnp | grep 245`.
(Memory: `docker-desktop-windows-no-udp-publish`, `valheim-lab-working-topology`.)

### Trap 2 — the mod reads a DIFFERENT config file than the "obvious" one  ⚠️
This is the one that ate the I1 probe session. **In this container `Paths.ConfigPath` resolves
to `/config/bepinex` — NOT the standard `/config/bepinex/config/` subdir.** So:

- The `.cfg` the mod **actually reads/writes** is at the **bepinex root**:
  `/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg`
  (host: `~/comfy-valheim-lab/server-state/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg`)
- There is a **DECOY** copy at `/config/bepinex/config/djcdevelopment.valheim.comfynetworksense.cfg`
  that the mod **never reads**. Editing it does nothing.
- The mod's telemetry lands at `/config/bepinex/comfy-network-sense/` (host:
  `~/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/`), one level *above*
  where you'd guess — this is how you confirm where `Paths.ConfigPath` really points.

**Symptom:** you flip a `[Netcode]` flag to `true`, restart, the mod loads, `Update()` ticks,
telemetry writes a 30 MB `telemetry-server.jsonl`, a peer connects for 5 minutes — and the
feature never fires and writes zero of its own files. **Diagnosis:** you edited the decoy.
**Always confirm the live path from inside the container:**

```bash
docker exec comfy-valheim-server-am4-valheim-server-1 sh -c \
  'find / -name comfy-network-sense -type d 2>/dev/null; \
   find / -name "djcdevelopment.valheim.comfynetworksense.cfg" 2>/dev/null'
```

The dir that gets created is `Paths.ConfigPath/comfy-network-sense`; the real cfg is
`Paths.ConfigPath/<GUID>.cfg`. Edit the one whose parent matches the telemetry dir's parent.

---

## Deploy the mod to the am4 server (build → scp → configure → restart → verify)

The mod builds against the **local (OMEN) Valheim client** DLLs; the same DLL runs on the am4
dedicated server (the ZDOMan funnel methods are shared networking core — proven).

```powershell
# 1. Build 0.5.6+ locally (auto-copies to OMEN's local plugins too)
dotnet build -c Release  C:\work\comfy\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj

# 2. Ship the DLL to am4
scp C:\work\comfy\network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll `
    derek@am4:'~/comfy-valheim-lab/server-state/config/bepinex/plugins/ComfyNetworkSense.dll'
```

```bash
# 3. Edit the REAL cfg (bepinex root, NOT the config/ subdir — see Trap 2)
#    e.g. enable the headless netcode probe:
ssh derek@am4 "sed -i \
  's/^netcodeProbeAutoStartEnabled = false/netcodeProbeAutoStartEnabled = true/' \
  ~/comfy-valheim-lab/server-state/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg"

# 4. Restart to reload (a container restart reloads the world; ~1 min for ComfyEra16 / 9M ZDOs)
ssh derek@am4 "docker restart comfy-valheim-server-am4-valheim-server-1"

# 5. Confirm the intended version loaded
ssh derek@am4 "docker logs comfy-valheim-server-am4-valheim-server-1 2>&1 | grep 'Loading \[ComfyNetworkSense'"
```

Deploying a new server DLL does **not** require the client to match — ComfyNetworkSense is not
enforced as a required mod (a 0.5.1 server accepted an unmodded/old i5 client fine).

---

## The `[Netcode]` probe config (headless server-side I1)

The probe is config-driven and **needs no console** on the server — it arms on
`ZNet.GetPeerConnections() > 0` and works headless. Keys (in the REAL cfg):

```ini
[Netcode]
netcodeProbeAutoStartEnabled = true      # default false
netcodeProbeAutoStartDelaySeconds = 25   # wait after first peer connects
netcodeProbeAutoStopSeconds = 150        # 0 = run until shutdown (no authoritative stop-row counters)
netcodeProbeMaxDetailRows = 5000         # per-ZDO detail cap; aggregate counters keep counting past it
```

**Gotcha — trapped counters:** with `autostop = 0` the true aggregate counts
(`recv_zdo_rows`, parse errors, etc.) are only emitted on a **stop/status row**, which never
fires until shutdown — and container restart/`Dispose` does **not** write one. For an
authoritative "nothing dropped over the full run" number, set a finite `autostop` (e.g. 90–150)
and a high `maxDetailRows`, so the auto-stop row carries the real uncapped totals. Otherwise you
only have the pre-cap sample (a floor, not the total); the verifier's `parseErr=0` is then a
**fallback default, not a measurement**.

### Verify it fired
```bash
# log line + the probe's own jsonl growing with BOTH directions
ssh derek@am4 "docker logs comfy-valheim-server-am4-valheim-server-1 2>&1 | grep 'Netcode probe auto-started'"
ssh derek@am4 "P=~/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/netcode-probe.jsonl; \
  wc -l \$P; grep -m1 '\"dir\":\"recv\"' \$P; grep -m1 '\"dir\":\"send\"' \$P"
```
Then pull `netcode-probe.jsonl` to `fieldlab/runs/i1/` and gate with
`scripts/verify-netcode-probe.ps1 -LogDir fieldlab\runs\i1 -OutSummary ...`.

---

## Full failure chain (getting a real player online) — for the record

| Attempt | Why it failed | Lesson |
|---|---|---|
| OMEN steam-headless container | no discrete GPU → llvmpipe ("hand-drawn") | GPU-less container = software render; unusable |
| am4 steam-headless container (GPU passthrough) | container Mesa **25.0.7** too old for Arc B70 Battlemage (PCI `0xe223`) → llvmpipe | new GPU in a container: **container Mesa ≥ host Mesa** or it falls back (`battlemage-needs-mesa-26`) |
| Vulkan renderer on GPU-less box | crashes on load | launch with **"Play Valheim using OpenGL"** on weak/no-GPU boxes |
| Fresh Steam account | `+connect` silently no-ops | **create a character first**, then join |
| Server on OMEN (Docker Desktop) | **no UDP publish** (Trap 1) | run UDP server on am4 native Linux Docker |
| i5 laptop, native Windows, Iris Xe | — | **WORKS** — real GPU + screen + native drivers = reference client |

---

## Standing gotchas checklist (paste-ready sanity list)
- [ ] UDP server on **native Linux Docker** (am4), not Docker Desktop. `ss -ulnp | grep 245` shows real listeners.
- [ ] Editing the **real** cfg (bepinex root), confirmed via `docker exec ... find` — not the `config/` decoy.
- [ ] Intended mod version confirmed in the boot log (`Loading [ComfyNetworkSense X.Y.Z]`).
- [ ] Player client is **native GPU + screen**; weak GPU → OpenGL launch option.
- [ ] Fresh Steam account has a **character created** before `+connect`.
- [ ] For authoritative probe counters: finite `autostop`, high `maxDetailRows` — don't leave `autostop = 0`.
- [ ] i5 sometimes shows tailnet-offline — wake it.
