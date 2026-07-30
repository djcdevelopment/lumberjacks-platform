# ADR 0014 — Boot must converge on its own, or say so loudly

- **Status:** Accepted (2026-07-30)
- **Rung:** cross-cutting — P7 stack lifecycle, any systemd-managed compose service

## Context

Two boots of the P7 VM the same evening behaved differently. The first brought all seven
containers up and served public TLS. The second, after a stop/start cycle, left six containers in
docker's `Created` state — created but never started — with `postgres` showing the prior run's
`Exited (128)`. Nothing served for fifteen minutes. **SSH answered normally the whole time.**

Reading the repo (no VM access) found the boot path was not one mechanism but two uncoordinated
ones, plus three separate ways for a failure to look like a success:

- **Nothing in the repo ever installed or enabled `comfy-lumberjacks-p7.service`.** The unit file
  was committed; the GCE startup script set up disk, swap, docker and the ops agent and stopped.
  Getting the unit into `/etc/systemd/system/` and enabled was hand-made state on the box — while
  [`RUNBOOK-cost-and-cycle.md`](../../../infra/gcp/p7/RUNBOOK-cost-and-cycle.md) justified
  stop/start as safe precisely because there was "no hand-built state to lose".
- **`ConditionPathExists=` skips a unit silently.** No error, no log, `systemctl status` reads
  `inactive (dead)` — indistinguishable from "hasn't booted yet".
- **`docker compose up -d` returns at `Created`.** With `Type=oneshot`, systemd then marks the
  unit **active** while nothing is running.
- **No `Restart=`.** One transient failure parked the stack permanently.
- **The docker daemon was unordered against `/mnt/comfy-p7`.** Its own `restart: unless-stopped`
  policies are a second, independent starter that could resolve bind mounts against the empty
  mountpoint on the root disk.

Every service except `valheim-server` hard-depends on `postgres: condition: service_healthy`, so
postgres is a single fan-in point: when it does not go healthy, compose aborts the start phase and
every dependent stays in `Created`. That is the observed signature exactly.

## Decision

**A service that manages other services must fail loudly, must not report success before it has
converged, and must retry. Where two mechanisms can start the same thing, one of them is
authoritative and the other is only for crash recovery.**

Concretely, for this stack:

1. **`Assert*` over `Condition*`** for anything whose absence means "we cannot serve". A silent
   skip is the worst available outcome because it is indistinguishable from healthy-but-early.
2. **Success means converged, not created.** `docker compose up -d --wait`, so exit 0 asserts every
   service is running and every healthcheck passes — which in turn makes `Restart=on-failure`
   meaningful.
3. **Retry indefinitely at boot** (`Restart=on-failure`, `StartLimitIntervalSec=0`). Boot-time
   failures are disproportionately transient; parking the stack forever for one is the wrong trade.
4. **Start from a clean slate.** `ExecStartPre=-docker compose down --remove-orphans`, because a
   hard power-off can interrupt teardown and leave a half-removed project that `up` will wedge on.
5. **The systemd unit is the authoritative starter.** `restart: unless-stopped` is retained for
   crash recovery *while running*, not for boot convergence — and the docker daemon is ordered
   after the state-disk mount so the two cannot disagree about what is mounted.
6. **The startup script installs and enables the unit** from the deployed checkout, on every boot,
   idempotently. Enablement stops being hand-made state.

**Deliberately not decided:** the `service_healthy` fan-in on postgres stays. The .NET services
genuinely need the database; loosening it would trade one visible failure for four silent
crash-loops. The fix for a fan-in point is retry and a clean slate, not a weaker dependency.

## Consequences

- A boot that cannot serve now **says so** — `systemctl status` shows `failed` with the missing
  path named, instead of a unit that looks fine next to six dead containers.
- The stack self-heals across transient boot failures instead of needing a human.
- **Cost:** an infinite retry can mask a genuine persistent failure by churning quietly. Accepted,
  because the churn is visible in the journal and the alternative — silent permanent death — is
  strictly worse. Retries are ~15 minutes apart in the worst case (`--wait-timeout 900`), not a
  hot loop.
- **This ADR is reasoned from repo files and is UNVERIFIED against the VM**, which remains stopped
  by policy. `Restart=on-failure` with `Type=oneshot` in particular is accepted by systemd but has
  not been exercised here — [`RUNBOOK-boot-determinism.md`](../../../infra/gcp/p7/RUNBOOK-boot-determinism.md)
  makes `systemd-analyze verify` a step for exactly that reason. Do not upgrade this to "boot is
  fixed" until that runbook's cold stop/start passes.
- Generalizes beyond P7: any future compose-on-systemd deployment inherits points 1–5.

## Related

- [0009](0009-verify-against-an-independent-source.md) — a check that reads its own output is not a
  check. Same family: here, a unit that reports its own success without checking convergence.
- [0015](0015-pin-line-endings-for-load-bearing-bytes.md) — the other "the gate was lying" finding
  from the same session.
- [0006](0006-git-bundle-transport-no-vm-credentials.md) — why `/opt/comfy` is a checkout at all.
