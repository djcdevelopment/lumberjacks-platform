# ADR 0006 — Move repo history to the P7 VM by `git bundle`; keep no credentials on the box

- **Status:** Accepted (2026-07-21)
- **Rung:** baseline cutover step 6; binds how the VM's deployment source is updated

## Context

The P7 VM's deployment source is `/opt/comfy`, which systemd's
`comfy-lumberjacks-p7.service` uses as `WorkingDirectory` for `docker compose up -d`. The m5 v2
manifest recorded that `/opt/comfy` "is not a git checkout, so the running compose has no provenance."

That was false. It **was** a git checkout — of the now-retired `djcdevelopment/comfy.git`, pinned at
`8ca27eda`, the exact `comfy_commit` the same manifest named, with 471 dirty files (the bootstrap had
deleted most of `infra/gcp/p7/scripts/` and modified the terraform). Its `docker-compose.yml` still
carried `build:` stanzas for `eventlog`/`progression`/`operatorapi`, so the five-service release gate
existed in the repo but had **never existed on the VM**.

Re-pointing it at `baseline` required getting `baseline`'s history onto the box. `git fetch` failed:

```
fatal: could not read Username for 'https://github.com': No such device or address
```

The VM has no GitHub credentials. Installing a token or deploy key would have worked, but adds a
long-lived secret to a host whose whole security posture is "no inbound but game UDP and one HTTP
port, no credentials at rest."

## Decision

**Transport history as an incremental `git bundle` over the existing IAP SSH channel; do not place
GitHub credentials on the VM.**

`8ca27eda` proved to be a genuine ancestor of `baseline`'s `main` — 232 commits back — because comfy's
history landed in the monorepo unmodified. That makes an incremental bundle possible:

```
git bundle create baseline-incremental.bundle main --not 8ca27eda…
scp   baseline-incremental.bundle comfy-p7:/tmp/
ssh   comfy-p7 "git -C /opt/comfy fetch /tmp/baseline.bundle main:refs/remotes/origin/main
                git -C /opt/comfy checkout -f -B main refs/remotes/origin/main"
```

24 MB instead of a full clone. Real commits, real history, full provenance. The remote URL is set to
`baseline.git` (accurate for a human), the old branch is retained as local `master` at `8ca27eda` as a
one-command rollback, and a full `opt-comfy.tgz` backup with hashes sits under
`/mnt/comfy-p7/backups/reprovision-20260721/`.

Note the ordering trap encountered: `git bundle create <file> <sha>` refuses to build ("empty bundle")
— bundles carry **refs**, so the ref name (`main`) must be the include and the old sha the `--not`.

## Consequences

- **The VM's compose now comes from a named baseline commit.** Verified byte-identical to the repo
  copy (`sha256 88caf504…` once CRLF is normalized), and the pins survived a real `systemctl restart`.
  The reproducibility gap the v2 manifest recorded is closed.
- **The VM cannot self-update.** Every future change reaches it as a bundle or an OCI archive, pushed
  from a workstation that holds the credentials. This is a deliberate trade: no secret at rest, at the
  cost of no `git pull` on the box and no unattended update path. **Whether to add a deploy key is
  open** → `DECISIONS-PENDING.md`.
- **The incremental form depends on ancestry.** It was cheap only because the retired commit was an
  ancestor of the new tip. A future host whose checkout has diverged needs `git bundle verify` checked
  first; if the prerequisite is absent, the fallback is a full bundle, which is large but still
  credential-free.
- **Backups are the rollback, not the remote.** `git checkout -f master` restores the pre-cutover
  deployment source in one command; the tarball covers the dirty-file state that reset discards.
- **Generalizes.** Any scope-guarded host reachable by SSH but not by authenticated HTTPS can be fed
  real git history this way. Prefer it over `scp`-ing loose files, which is what produced the
  no-provenance state this ADR exists to end.

## Related

ADR 0004 (the VM itself); `Lumberjacks/docs/roadmap/m5-v3-reprovision-receipt.json` (backup paths,
rollback commands, verified digests); `infra/gcp/p7/README.md`;
`retro/SESSION-RETRO-2026-07-21.md` lesson `L-2026-07-21-4`; memory `p7-deployment-topology`.
