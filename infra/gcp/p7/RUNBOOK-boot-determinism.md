# P7 boot determinism — diagnosis and next-boot verification

**Status:** fixes staged in the repo, **unverified on the VM**. The VM is STOPPED and stays
stopped; everything below is written to be executed on the next legitimate boot.

**Incident:** 2026-07-29/30, two boots of `comfy-lumberjacks-p7`, same VM, ~40 min apart.

| | Boot 1 | Boot 2 (after `gcloud compute instances stop/start`) |
|---|---|---|
| Containers | all 7 running on restart policies | 6 × `Created`, postgres `Exited (128)` |
| Public TLS | `comfy-p7.duckdns.org/health` → 200 | nothing serving |
| Valheim | `ComfyEra16` loaded, "Game server connected" ~2 min | never started |
| SSH | fine | **fine** — this is the trap |

`Created` means the container was created and never started. It produces no logs and no
error. The box answers SSH, `docker ps -a` looks populated, and nothing is serving.

---

## What actually brings the stack up

Two independent starters, which is the root of the problem:

1. **`comfy-lumberjacks-p7.service`** — `WantedBy=multi-user.target`, runs
   `docker compose up -d` from `/opt/comfy/infra/gcp/p7`, env from `/etc/comfy-p7/environment`.
2. **The docker daemon itself** — every service carries `restart: unless-stopped`, so dockerd
   revives containers on its own, with no reference to the unit.

They are not coordinated, and only the unit was ordered against the state disk mount.

## Findings

Confidence is labelled. Only the first four are provable from the repo alone; the trigger for
this specific boot is not, and the next-boot checks below are what settle it.

### 1. Nothing in the repo ever installed the unit — **verified**

`scripts/bootstrap.sh.tftpl` is the GCE `metadata_startup_script` and re-runs on every boot. It
set up the disk, swap, docker and the ops agent — and never touched
`comfy-lumberjacks-p7.service`. The unit file was committed to the repo, but getting it into
`/etc/systemd/system/` and enabled was hand-made state on the box. A rebuilt VM would have come
up with docker running and nothing enabled to start the stack.

This directly contradicts [`RUNBOOK-cost-and-cycle.md`](RUNBOOK-cost-and-cycle.md)'s basis for
treating stop/start as safe: *"no hand-built state to lose."* The enablement **was** the
hand-built state.

### 2. A failed `Condition*` skips the unit silently — **verified**

The unit gated on `ConditionPathExists=` for the compose file and the env file. In systemd a
failed `Condition*` is not an error: the unit is skipped, `systemctl status` reads
`inactive (dead)`, and nothing is logged. That is precisely "the box looks alive over SSH while
serving nothing", and it is indistinguishable from "hasn't booted yet".

Changed to `AssertPathExists=`, which marks the unit **failed** and logs the missing path.

### 3. `up -d` reports success at `Created` — **verified**

`docker compose up -d` returns as soon as containers are created. systemd's `Type=oneshot` +
`RemainAfterExit=yes` then marks the unit **active** — the exact state observed, where the unit
looks healthy and six containers have never started. Fixed with `--wait`, so exit 0 means every
service is running and every healthcheck passes.

### 4. One transient failure parks the stack permanently — **verified**

`Type=oneshot`, no `Restart=`. Nothing retried. Every service except valheim-server hard-depends
on `postgres: condition: service_healthy`, so postgres is a single point of fan-in: if it does
not go healthy, compose aborts the start phase and **every dependent stays in `Created`** —
matching the observation exactly. Fixed with `Restart=on-failure`, `RestartSec=30s`, and
`StartLimitIntervalSec=0` so it retries indefinitely instead of giving up after systemd's
default 5-in-10s burst.

> The `service_healthy` fan-in itself is **kept as-is**. The .NET services genuinely need the
> database; loosening it would trade one visible failure for four silent crash-loops. The fix is
> retry and a clean slate, not weaker dependencies.

### 5. Shutdown poisons the next boot — **inferred, consistent with the evidence**

`ExecStop=docker compose down` asked for `TimeoutStopSec=300`, but `gcloud compute instances
stop` gives the guest a far shorter budget (tens of seconds) and then hard-powers-off. So `down`
can be interrupted mid-teardown, leaving the project half-removed — and containers stopped by an
explicit `docker stop` are flagged as intentionally stopped, which **suppresses
`restart: unless-stopped`** on the next daemon start. Starting `up` on top of that wreckage is a
strong candidate for how boot 2 wedged.

Fixed by making every start begin from a clean slate
(`ExecStartPre=-docker compose down --remove-orphans`), changing `ExecStop` to `stop`, and
cutting `TimeoutStopSec` to a value the shutdown budget can actually honour.

### 6. dockerd could win a race against the state disk — **verified as a hazard, not as this incident's cause**

`/mnt/comfy-p7` is mounted from `/etc/fstab` with `nofail`, so systemd does not block boot on it.
The unit was protected (`RequiresMountsFor=`); **the docker daemon was not**. If dockerd starts
first, its `unless-stopped` containers resolve their bind mounts against the still-empty
mountpoint on the **root disk** — postgres initdb's into `/`, the Valheim world reads blank, and
the real data hides under the later mount. Fixed with a `docker.service` drop-in carrying
`RequiresMountsFor=/mnt/comfy-p7`.

This is also a candidate explanation for the root filesystem being at 79%.

### 7. `COMPOSE_PROFILES=tls` was missing from the committed template — **verified**

`caddy` sits behind the `tls` profile, so it starts only under `COMPOSE_PROFILES=tls`. The live
VM has it (boot 1 served TLS); [`environment.example`](environment.example) did not. Rebuilding
`/etc/comfy-p7/environment` from the template would have produced a stack with no TLS terminator
— nothing on 80/443, the volunteer endpoint dark — and **no error**, because an unselected
profile is not a failure. Added to the template.

### 8. `LUMBERJACKS_ROOT` is a boot-critical path that looks like garbage — **verified as fragile**

postgres bind-mounts `${LUMBERJACKS_ROOT}/infra/docker/init.sql`, and `LUMBERJACKS_ROOT` is
`/opt/lumberjacks-ed83bd8` — a sha-suffixed directory, i.e. exactly what a disk-pressure cleanup
deletes as "an old build root". With root at 79% that is a live risk, and because postgres is the
fan-in point it takes the whole stack down rather than just the database.

---

## Staged changes (in the repo, not on the VM)

| File | Change |
|---|---|
| [`comfy-lumberjacks-p7.service`](comfy-lumberjacks-p7.service) | `Assert*` over `Condition*`; `--wait`; `Restart=on-failure`; clean-slate `ExecStartPre`; `ExecStop=stop`; realistic `TimeoutStopSec` |
| [`scripts/bootstrap.sh.tftpl`](scripts/bootstrap.sh.tftpl) | installs + `enable --now`s the unit from the deployed checkout; adds the `docker.service` mount-ordering drop-in |
| [`environment.example`](environment.example) | `COMPOSE_PROFILES=tls`, real DNS name, `LUMBERJACKS_ROOT` warning |

> **The bootstrap change does not reach the running VM by itself.** It is the
> `metadata_startup_script`, and applying it needs Terraform — which is off the table from this
> checkout (a plan here would destroy the VM and four live resources; see
> [`RECONCILE-GAP.md`](RECONCILE-GAP.md)). Treat the bootstrap edit as the durable fix for a
> future rebuild, and apply step 2 below by hand on the next boot.

---

## Next-boot procedure

Read steps 0–2 before starting the VM. Steps 0 and 1 capture evidence that is **destroyed** by
the fix, so do not skip ahead.

### 0a. Before starting — check the ComfyEra16 save

Boot 1 loaded the world and reached "Game server connected", then took a
`gcloud compute instances stop`. That is the graceful-stop hazard: the stop can orphan a
`.db.new` beside the real save. Boot 2 never started Valheim, so whatever state that left is
still frozen on the disk — **check it before anything loads the world again**, because a stack
that now comes up deterministically will load it automatically.

```bash
ssh comfy-p7 "ls -la --time-style=long-iso /mnt/comfy-p7/valheim/config/worlds_local/ | grep -i comfyera16"
```

A `ComfyEra16.db.new` newer than `ComfyEra16.db` is an interrupted save, not a good one. Do not
let the server start on top of it until it is resolved — take a copy of both first.

### 0b. Before starting — reclaim disk

Root was at 79% (30G/38G, 8.1G avail) and `/mnt/comfy-p7` at 67% (21G/32G). Docker's data root
is on the **root** disk, so a boot-time image pull needs headroom there. Do not blind-prune:
`docker image prune -a` would evict the digest-pinned release images and force a re-pull at the
worst possible moment.

```bash
ssh comfy-p7 "sudo du -xh --max-depth=1 / 2>/dev/null | sort -rh | head -20; echo ---; sudo du -sh /opt/lumberjacks-* /var/lib/docker 2>/dev/null"
```

Reclaim only what you have identified, and **confirm `/opt/lumberjacks-ed83bd8/infra/docker/init.sql`
still exists afterwards** (finding 8).

### 1. Capture the wedged-boot evidence first

This is the one chance to settle whether finding 5 or finding 6 was the trigger. Run these
**before** installing the new unit.

```bash
ssh comfy-p7 "sudo journalctl -u comfy-lumberjacks-p7 -b --no-pager | tail -80"
```

```bash
ssh comfy-p7 "sudo docker ps -a --format 'table {{.Names}}\t{{.Status}}\t{{.RunningFor}}'; echo ---; sudo docker inspect comfy-lumberjacks-p7-postgres-1 --format '{{.State.Status}} exit={{.State.ExitCode}} err={{.State.Error}} oom={{.State.OOMKilled}} restart={{.HostConfig.RestartPolicy.Name}}'"
```

Then the two discriminators:

```bash
ssh comfy-p7 "findmnt /mnt/comfy-p7; echo '--- did dockerd beat the mount? ---'; sudo journalctl -u docker -b --no-pager | head -20; echo '--- mount unit ---'; sudo systemctl status 'mnt-comfy\x2dp7.mount' --no-pager"
```

```bash
ssh comfy-p7 "sudo systemctl is-enabled comfy-lumberjacks-p7; echo '--- unit on disk? ---'; ls -l /etc/systemd/system/comfy-lumberjacks-p7.service; echo '--- guarded paths ---'; ls -l /opt/comfy/infra/gcp/p7/docker-compose.yml /etc/comfy-p7/environment"
```

**Expected if finding 1 is the whole story:** `is-enabled` returns `disabled` or the unit file is
absent — meaning boot 1 only ever worked because dockerd's restart policies happened to fire, and
boot 2 lost that when the interrupted `down` flagged the containers as intentionally stopped.
That single result would explain both boots, and it is the first thing to check.

**Expected if finding 6 is in play:** `docker.service` start timestamp precedes the mount, and
there is data directly on the root disk underneath the mountpoint. Check with:

```bash
ssh comfy-p7 "sudo systemctl stop docker; sudo umount /mnt/comfy-p7 && sudo du -sh /mnt/comfy-p7 && sudo ls -la /mnt/comfy-p7; sudo mount -a; sudo systemctl start docker"
```

Anything larger than a few empty directories there is data written to the root disk while the
mount was absent — that is the 79%, and it must be removed before it is re-hidden.

### 2. Apply the fixes by hand

```bash
ssh comfy-p7 "cd /opt/comfy && git pull 2>/dev/null || echo 'deploy via git bundle first'; ls -l infra/gcp/p7/comfy-lumberjacks-p7.service"
```

```bash
ssh comfy-p7 "sudo install -d -m 0755 /etc/systemd/system/docker.service.d && printf '[Unit]\nRequiresMountsFor=/mnt/comfy-p7\n' | sudo tee /etc/systemd/system/docker.service.d/10-comfy-p7-state-mount.conf"
```

```bash
ssh comfy-p7 "sudo install -m 0644 /opt/comfy/infra/gcp/p7/comfy-lumberjacks-p7.service /etc/systemd/system/comfy-lumberjacks-p7.service && sudo systemctl daemon-reload && sudo systemd-analyze verify /etc/systemd/system/comfy-lumberjacks-p7.service && sudo systemctl enable comfy-lumberjacks-p7.service && sudo systemctl is-enabled comfy-lumberjacks-p7"
```

> `systemd-analyze verify` is not ceremony. `Restart=on-failure` with `Type=oneshot` is accepted
> by systemd (`Restart=always` is not), but that combination has not been exercised on this box —
> verify it parses before trusting it to recover a boot. Silence means it is good.

Confirm `COMPOSE_PROFILES=tls` is really in the live env file (finding 7 says the template was
missing it; check the direction of the drift):

```bash
ssh comfy-p7 "sudo grep -c COMPOSE_PROFILES /etc/comfy-p7/environment; sudo grep -o '^[A-Z_]*=' /etc/comfy-p7/environment | sort > /tmp/live.keys; grep -o '^[A-Z_]*=' /opt/comfy/infra/gcp/p7/environment.example | sort > /tmp/tmpl.keys; diff /tmp/tmpl.keys /tmp/live.keys"
```

### 3. Prove it with a real stop/start cycle

The README's claim was verified by `systemctl restart` on an already-running box — which is
**not** the reboot path: it never re-tests the mount race, the daemon's restart policies, or the
shutdown teardown. Only a cold cycle proves this.

**Save the Valheim world and verify the save before stopping.** `gcloud compute instances stop`
counts as a graceful-stop hazard for the world; the shutdown path is not a save mechanism.

```bash
gcloud compute instances stop comfy-lumberjacks-p7 --zone us-west1-b
```

```bash
gcloud compute instances start comfy-lumberjacks-p7 --zone us-west1-b
```

Then, with no manual intervention whatsoever:

```bash
ssh comfy-p7 "systemctl is-active comfy-lumberjacks-p7; sudo docker ps -a --format 'table {{.Names}}\t{{.Status}}'"
```

**Pass:** unit `active`, all 7 containers `Up` (postgres `Up (healthy)`), **zero** in `Created`.

```bash
curl -sS -o /dev/null -w '%{http_code}\n' https://comfy-p7.duckdns.org/health
```

**Pass:** `200`.

```bash
ssh comfy-p7 "sudo docker logs --tail 40 comfy-lumberjacks-p7-valheim-server-1 | grep -F 'Game server connected'"
```

**Pass:** the line is present. The ~9.1M-ZDO `ComfyEra16` world takes ~a minute to load — wait
for that line before telling anyone to join.

### 4. Prove the retry actually recovers

The whole point of `Restart=on-failure` is untested until something fails. Force it:

```bash
ssh comfy-p7 "sudo docker stop comfy-lumberjacks-p7-postgres-1 && sudo systemctl restart comfy-lumberjacks-p7; sleep 90; systemctl is-active comfy-lumberjacks-p7; sudo docker ps -a --format 'table {{.Names}}\t{{.Status}}'"
```

**Pass:** the stack converges to all-`Up` on its own. **Fail:** anything left in `Created` — the
clean-slate `ExecStartPre` did not do its job, and the fix is incomplete.

---

## Doc corrections owed once this is verified

Both of these currently state, as settled fact, a reliability that boot 2 falsified:

- [`README.md`](README.md) — "verified by a real `systemctl restart`, which is exactly what the
  reboot path runs." It is not: a restart skips the mount race, the daemon restart policies, and
  the shutdown teardown.
- [`RUNBOOK-cost-and-cycle.md`](RUNBOOK-cost-and-cycle.md) — "a stopped VM re-enters service
  predictably — no hand-built state to lose." The unit's enablement was hand-built state, and
  boot 2 did not re-enter service.

Correct them from the results of step 3 rather than from this document — the point is to record
what a cold cycle proved, not to replace one untested claim with another.
