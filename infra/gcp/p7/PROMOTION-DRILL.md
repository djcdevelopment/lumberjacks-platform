# M0/A4 Promotion Drill Runbook

## 1. Purpose

This runbook guides the operator through the M0/A4 promotion drill: prove that the
manifest-tied release cold-starts cleanly from prebuilt artifacts without any VM-side
source rebuild, rolls back artifact-only to the historical validated runtime, and
restores the candidate state. The drill is fail-closed end to end and produces the A4
exit receipts (`snapshot-manifest.json`, `cold-start-receipt.json`,
`rollback-receipt.json`, `restored-state-receipt.json`) plus the pre-flight
`drill-plan.json`, all under `<BundleRoot>\drill\`.

## 2. Preconditions

- [ ] The scheduled GCP mutation window is open and the Valheim server is empty of players.
- [ ] No volunteer session or strict evidence window is active.
- [ ] The release bundle `m0-clean-20260716-r2` passed local validation
      (`validate-release-bundle.ps1`).
- [ ] `/mnt/comfy-p7/backups/comfynetworksense/20260716T004955Z` exists on the VM and
      contains the `runtime.dll`/`fallback.dll` pair matching the historical mod
      SHA-256 (`b31697d2a0cbe47b86c32b33d19fb9445e21af0cfe51687cb5afe871a3d7d77b`).
      This is the recorded victory backup (see README).
- [ ] The IAP SSH target `comfy-p7` is reachable.

## 3. Identities under test

| Artifact role | Identifier / SHA-256 | Source |
| :--- | :--- | :--- |
| Candidate Gateway image | `sha256:141bd9e5a2ce8bd95f1bd93a9f123637cbc1cffcb0795594fae94e28d7fe86fb` | release manifest `m0-clean-20260716-r2` |
| Candidate mod DLL | `94a3843ef8042adceaca6bc4d5c0c38c7c8dc5a1aa05b5f2a3019879840ba3a8` | bundle `mod/ComfyNetworkSense.dll` |
| Rollback Gateway image | `sha256:358f5e11e35b54367a83d4e52ea3d47c0346e62a82ed357c2ff403eafafcd0a2` | live P7 validated runtime |
| Rollback mod DLL | `b31697d2a0cbe47b86c32b33d19fb9445e21af0cfe51687cb5afe871a3d7d77b` | live P7 validated runtime |

## 4. Plan-only rehearsal (no VM contact)

Run without `-Execute` to validate the bundle, resolve every identity, and write the
drill plan. This never opens an SSH connection:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
  -ManifestPath Lumberjacks\docs\roadmap\m0-clean-build-candidate-r2.json `
  -BundleRoot C:\work\baseline\fieldlab\runs\releases\m0-clean-20260716-r2 `
  -RollbackImageId sha256:358f5e11e35b54367a83d4e52ea3d47c0346e62a82ed357c2ff403eafafcd0a2 `
  -RollbackModSha256 b31697d2a0cbe47b86c32b33d19fb9445e21af0cfe51687cb5afe871a3d7d77b
```

`-RollbackImageId`/`-RollbackModSha256` have no script default (fixed 2026-07-21: they used to
default to these exact M0 values, which silently kept applying to every later drill). Section 3's
table is the source for THIS drill; a later drill must pull the equivalent pair from the release
being superseded, never copy these forward unchanged.

Verify `drill-plan.json` under `<BundleRoot>\drill\`: candidate image and mod hashes
match section 3, rollback identities are present, and `execute` is `false`.

## 5. Execution

Inside the scheduled window:

```powershell
& C:\work\baseline\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
  -ManifestPath Lumberjacks\docs\roadmap\m0-clean-build-candidate-r2.json `
  -BundleRoot C:\work\baseline\fieldlab\runs\releases\m0-clean-20260716-r2 `
  -RollbackImageId sha256:358f5e11e35b54367a83d4e52ea3d47c0346e62a82ed357c2ff403eafafcd0a2 `
  -RollbackModSha256 b31697d2a0cbe47b86c32b33d19fb9445e21af0cfe51687cb5afe871a3d7d77b `
  -RollbackModBackupPath /mnt/comfy-p7/backups/comfynetworksense/20260716T004955Z `
  -Execute
```

Phase walkthrough:

1. **SNAPSHOT** — stops `valheim-server`, archives `/mnt/comfy-p7/valheim/config`,
   the compose file, and the environment file to
   `/mnt/comfy-p7/backups/promotion-drill/<stamp>` with a SHA-256 manifest, restarts
   `valheim-server`. Verifies the rollback DLL pair exists before mutating anything.
   Writes `snapshot-manifest.json` (hashes only; archives stay on the VM).
2. **COLD START** — uploads and `docker load`s all four gated OCI archives. The
   Gateway is pinned in `docker-compose.promotion.yml`; `eventlog`, `progression`
   and `operatorapi` are pinned in `docker-compose.release.yml`. Both files join the
   `-f` chain, everything starts with `--no-build`, and the exact running image ID of
   all four is verified against the manifest (`/health` is checked for the Gateway
   only — the other three expose no such endpoint). Deploys the candidate mod DLL to
   the runtime and fallback paths and verifies its SHA-256 at both. Writes
   `cold-start-receipt.json`.

   The two override files have deliberately different lifetimes. The release pins are
   fixed for the release, so phases 3 and 4 inherit `docker-compose.release.yml`
   untouched; only the Gateway pin moves, because only the Gateway has a rollback
   identity (there is no `-RollbackEventlogImageId`, by design). Rolling the Gateway
   back does not roll the stack back.
3. **ROLLBACK** — re-pins the Gateway to the historical rollback image (already on
   the VM; nothing is rebuilt), verifies health plus exact image ID, restores the
   historical mod DLL pair, restarts `valheim-server`, and verifies the historical
   SHA-256 at both paths. Writes `rollback-receipt.json`.
4. **RESTORE** — re-pins the candidate image, redeploys the candidate mod DLL, and
   repeats the health/identity/hash verification, leaving the promoted release
   running. Writes `restored-state-receipt.json`.

## 6. Abort and recovery

Any identity, hash, or health mismatch stops the drill immediately (fail-closed). To
recover to the pre-drill baseline manually:

1. Re-pin the Gateway to the rollback image
   (`sha256:358f5e11e35b54367a83d4e52ea3d47c0346e62a82ed357c2ff403eafafcd0a2`) in
   `docker-compose.promotion.yml` and `docker compose up -d --no-build --no-deps gateway`.
2. Restore the historical DLL pair from
   `/mnt/comfy-p7/backups/comfynetworksense/20260716T004955Z` (runtime + fallback
   paths) and restart `valheim-server`.
3. If world/config state is in doubt, recover it from
   `/mnt/comfy-p7/backups/promotion-drill/<stamp>` and verify against
   `snapshot.sha256` before restart.

## 7. After the drill

1. Collect the receipts from `<BundleRoot>\drill\` into the A4 exit evidence.
2. Close A5 — the publication set is already staged at Comfy revision `433f1cc`
   (receipt: Lumberjacks `docs/roadmap/m0-a5-publication-receipt.json`):
   1. Push Comfy `main` (sanitized gold-run evidence set + hash-bound `-text`
      pinning); the receipt's permalink becomes resolvable.
   2. Push Lumberjacks `master` (A5 staging receipt + roadmap note).
   3. Flip roadmap `golden_proof.publication` to published with the permalink and
      append the closing roadmap note (`node scripts/roadmap.mjs note ...`).
   Do not rewrite Comfy history before pushing, or the staged revision and the
   receipt must be re-recorded.
3. When the promotion decision is final, retire the overrides: set all four gated
   image variables in `/etc/comfy-p7/environment` — `LUMBERJACKS_GATEWAY_IMAGE`,
   `LUMBERJACKS_EVENTLOG_IMAGE`, `LUMBERJACKS_PROGRESSION_IMAGE`,
   `LUMBERJACKS_OPERATORAPI_IMAGE` — to their promoted tags, install the base
   `docker-compose.yml` that pins each `image` from its variable (no `build:`
   fallback), verify `up -d --no-build --no-deps gateway eventlog progression
   operatorapi` leaves the containers untouched, then delete both
   `docker-compose.promotion.yml` and `docker-compose.release.yml`. `-Finalize` does
   all of this and verifies every running image ID afterwards.

   Retiring the overrides while leaving the three sibling variables at their
   pre-cutover values would silently resolve the reboot path back to whatever those
   still name — the same silent-revert this step exists to prevent, three services
   wider.

   Done for `m0-clean-20260716-r2` on 2026-07-16: the systemd reboot path
   (`docker compose up -d`, base file only) now resolves the promoted pin, and VM-side
   gateway builds fail closed. Pre-change copies are on the VM under
   `/mnt/comfy-p7/backups/retire-promotion-override-20260716/`. Rollback is now an
   env-var re-pin: set `LUMBERJACKS_GATEWAY_IMAGE` to the rollback reference and
   `docker compose --env-file /etc/comfy-p7/environment up -d --no-build --no-deps gateway`.
