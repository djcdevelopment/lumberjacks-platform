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
& C:\work\comfy\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
  -ManifestPath C:\work\Lumberjacks\docs\roadmap\m0-clean-build-candidate-r2.json `
  -BundleRoot C:\work\comfy\fieldlab\runs\releases\m0-clean-20260716-r2
```

Verify `drill-plan.json` under `<BundleRoot>\drill\`: candidate image and mod hashes
match section 3, rollback identities are present, and `execute` is `false`.

## 5. Execution

Inside the scheduled window:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
  -ManifestPath C:\work\Lumberjacks\docs\roadmap\m0-clean-build-candidate-r2.json `
  -BundleRoot C:\work\comfy\fieldlab\runs\releases\m0-clean-20260716-r2 `
  -RollbackModBackupPath /mnt/comfy-p7/backups/comfynetworksense/20260716T004955Z `
  -Execute
```

Phase walkthrough:

1. **SNAPSHOT** — stops `valheim-server`, archives `/mnt/comfy-p7/valheim/config`,
   the compose file, and the environment file to
   `/mnt/comfy-p7/backups/promotion-drill/<stamp>` with a SHA-256 manifest, restarts
   `valheim-server`. Verifies the rollback DLL pair exists before mutating anything.
   Writes `snapshot-manifest.json` (hashes only; archives stay on the VM).
2. **COLD START** — uploads and `docker load`s the bundle Gateway OCI archive, pins
   the image in `docker-compose.promotion.yml`, starts with `--no-build`, and
   verifies `/health` plus the exact running image ID against the manifest. Deploys
   the candidate mod DLL to the runtime and fallback paths and verifies its SHA-256
   at both. Writes `cold-start-receipt.json`.
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
3. `docker-compose.promotion.yml` remains on the VM pinning the running release.
   Remove it only when the promotion decision is final and the base compose file has
   been updated to match.
