# P7 Terraform reconcile gap (deferred)

**Status:** OPEN. Recorded 2026-07-24 during the state-disk right-size.
**Owner decision:** disk right-size proceeded via out-of-band GCP ops; the code↔state↔live
reconciliation below is deferred to a separate, reviewed effort.

## ⚠️ Do NOT `terraform apply` from `infra/gcp/p7` until this is closed

A `terraform plan` of the **baseline** code against the **live** state (comfy state, serial 31)
currently reports **`2 to add, 1 to change, 5 to destroy`**, including a **destroy-and-recreate of
the VM** and deletion of **4 live resources**. Applying it would take down production. The Phase 0
gate of the disk task caught this; nothing was applied.

## Why the drift exists

The authoritative Terraform state is a **local file in the retired `C:\work\comfy` checkout**
(`infra/gcp/p7/terraform.tfstate`, serial 31), while the canonical code now lives in
`C:\work\baseline`. The two `.tf` trees differ only by the UDP firewall (+ cosmetic wording), but
**both** have drifted from what the state/live actually contain: several resource blocks were
deleted from the code in an edit that was **never applied**, so state + live still carry them.

## The drift, itemized

### 1. VM would be replaced (most dangerous)
`google_compute_instance.p7` — two attributes force replacement:
- **Boot image:** live is `ubuntu-2404-noble-amd64-v20260707`; code
  ([main.tf:212](main.tf#L212)) says `ubuntu-os-cloud/ubuntu-minimal-2404-lts-amd64`. Different
  image family → replacement.
- **`metadata_startup_script`:** the rendered `bootstrap.sh.tftpl` differs from what's in state.

The code comment at [main.tf:210](main.tf#L210) already warns image changes force replacement "only
in a planned rebuild window." The live VM was created from a different image than the code declares.

**Fix options (reconcile effort):** set the code image to the family the VM actually runs, and/or add
`lifecycle { ignore_changes = [boot_disk[0].initialize_params[0].image, metadata_startup_script] }`
so routine plans never propose a VM rebuild. Reconcile the startup script content deliberately.

### 2. Four resources live + in state but absent from BOTH code trees (would be destroyed)
| Address | Live resource | Action if applied |
|---|---|---|
| `google_compute_firewall.gateway_uptime` | `comfy-lumberjacks-p7-gateway-uptime` (tcp:4000) | destroy |
| `google_compute_firewall.lumberjacks_control` | `comfy-lumberjacks-p7-lumberjacks-control` (tcp:4000, udp:4005) | destroy |
| `google_monitoring_alert_policy.gateway_unavailable` | gateway-unavailable alert | destroy |
| `google_monitoring_uptime_check_config.gateway` | gateway uptime check | destroy |

**Fix (reconcile effort):** decide keep-vs-remove per resource. If keeping (these back live
monitoring + an ingress lane), **re-add their definitions to baseline code** so state matches config.
If genuinely superseded, remove from live + state deliberately. Do not let a blanket apply delete them.

### 3. UDP firewall tracked in code but not state
`google_compute_firewall.lumberjacks_player_udp` (live `comfy-lumberjacks-p7-player-udp`, udp:4005)
exists live and in **baseline** code, but is **not in state** → plan wants to *create* it (409).
**Fix:** `terraform import google_compute_firewall.lumberjacks_player_udp comfy-lumberjacks-p7-player-udp`.

### 4. State-disk resources now stale (from the 2026-07-24 right-size)
The right-size replaced the disk out-of-band. State still references the **old** disk:
- `google_compute_disk.state` → `comfy-lumberjacks-p7-state` (150 GB, **detached, pending deletion**).
- `google_compute_disk_resource_policy_attachment.state_snapshot` → attached to the old disk.
- The **new** `comfy-lumberjacks-p7-state-v2` (32 GB) is **unmanaged**; its daily-snapshot policy
  attachment was created via `gcloud`, not Terraform.
- Instance `attached_disk` in state points at the old disk id (device-name `comfy-p7-state` preserved).

**Fix (reconcile effort):**
```
terraform state rm google_compute_disk.state
terraform state rm google_compute_disk_resource_policy_attachment.state_snapshot
# edit code: main.tf disk name -> "${local.name}-state-v2"; variables.tf data_disk_size_gb default -> 32
terraform import google_compute_disk.state <self-link of comfy-lumberjacks-p7-state-v2>
terraform import google_compute_disk_resource_policy_attachment.state_snapshot \
    projects/lumberjacks-exp-20260711-djc/zones/us-west1-b/disks/comfy-lumberjacks-p7-state-v2/comfy-lumberjacks-p7-daily-snapshot
```
`prevent_destroy = true` on the disk is fine — `state rm` forgets without destroying.

## Recommended close-out sequence (the deferred reconcile)
1. Seed a working root from baseline code + comfy `terraform.tfvars` + a **copy** of the comfy state
   (never edit the original). `terraform init` (google v7.40.0, local cache).
2. Import #3 (UDP firewall). Resolve #2 (re-add or remove each of the 4). Resolve #1 (image +
   `ignore_changes`). Resolve #4 (disk adopt). Re-run `terraform plan` after each until **no-op**.
3. Only when plan is a clean no-op: migrate to a GCS backend (new versioned bucket
   `lumberjacks-p7-tfstate-djc`), `terraform init -migrate-state`, making **baseline** the single
   canonical root and retiring the comfy local state.
4. Update sizing docs (`Lumberjacks/docs/google-cloud-stage1-runbook.md:55`, `README.md`).

## Restore points (as of the right-size)
- Old 150 GB disk `comfy-lumberjacks-p7-state` — detached, intact (delete after ~48 h).
- Snapshot `comfy-p7-state-precutover-20260724` (fresh, pre-cutover).
- Daily auto-snapshots (7-day, `KEEP_AUTO_SNAPSHOTS`).
- Untouched `C:\work\comfy\infra\gcp\p7\terraform.tfstate` (+ `.backup`) — the state rollback.
