# P7 cost & duty-cycle runbook — 2026-07-28

Operator: **Derek, at the keyboard**. Agents are classifier-blocked from GCP mutations, so
every command below is staged for you to paste. Nothing here has been executed.

Grounding: [`COST-OF-TELEMETRY.md`](COST-OF-TELEMETRY.md) — **read this before changing any
metric, interval or agent receiver**; it carries the measured price of observability on this
box and the preflight checklist. Also [`README.md`](README.md),
[`RECONCILE-GAP.md`](RECONCILE-GAP.md).

> **Dangling reference, 2026-09-06.** This section previously cited
> `docs/audit/2026-07-25-gcp-burn-rate-review.md` as its grounding. **That file has never
> existed in this repository** — not in the working tree, and `git log --all` on the path
> returns nothing. Its dollar figures were carried into the decision table below without a
> committed source. Treat every unattributed figure here as an estimate until a billing
> export exists (item A). The mechanism those estimates were meant to explain is now
> written down in `COST-OF-TELEMETRY.md`.

## Operator corrections — 2026-07-29 (these override the memo's framing)

Derek's ground truth, recorded before tonight's driving session:

1. **The spec history is deliberate, not waste.** The machine was vastly overspecced early
   on purpose — limit-testing with **800+ headless connections** to simulate Gateway volume
   and find the knee points. That era is over and its findings are banked.
2. **2 vCPU / 16 GB is the floor.** The current shape (`n2-highmem-2` = 2/16) is *plenty
   and correct* — the memo's 8 GB downsize options are **rejected** (see lever D, rewritten).
3. **The disk keeps exploding because of us, not the game**: production-grade world backups
   and push-time snapshot ceremony running during *dev iteration loops*. The world itself is
   a saved world from another era on another server — an heirloom copy, already preserved
   elsewhere. Good to know the machinery exists; it should not run at prod cadence during
   dev. (New lever E.)
4. **The "cohort" today is Derek's own three accounts** plus people who know him by name.
   Downtime coordination is a ping to friends, not a product-hours commitment — C and D's
   warnings are scaled accordingly.
5. **Posture: local-first as much as possible; when on GCP, lean and mean** through
   tonight's session, then a **full shakedown at end of night** (sequence at the bottom).

> The "real external cohort" conditions referenced below (lever C's loud warning, lever
> E's re-arm rule) are now one named trigger — the **First Stranger gate**: definition
> and full due-list in
> [`docs/decisions/pd-2-security-posture-first-stranger-gate.md`](../../../docs/decisions/pd-2-security-posture-first-stranger-gate.md).

## Hard rule

> **Do NOT `terraform apply` from `infra/gcp/p7`.** RECONCILE-GAP is OPEN: plan against the
> live state is `2 to add, 1 to change, 5 to destroy` — including destroy-and-recreate of the
> VM and deletion of 4 live resources. Everything tonight is `gcloud`/console-side.
> The Terraform reconcile stays a separate, deliberate effort.

## Shared facts

| | |
|---|---|
| Project | `lumberjacks-exp-20260711-djc` |
| VM / zone | `comfy-lumberjacks-p7` / `us-west1-b` (region `us-west1`) |
| Machine | `n2-highmem-2` (2 vCPU / 16 GB), RUNNING 24/7 |
| Static IP | `8.231.129.249` (game UDP 2456, Gateway 42317) |
| Est. burn | **~$93–113/mo**, of which the VM is ~$76–96 (**~80%**), snapshots ~$7.00, disks ~$7.20, IP ~$2.92 |
| VM compute rate | ~$2.52/day stopped-savings per the memo ⇒ **~$0.105/hr** |

### Why stop/start is a real option now (it wasn't during R&D)

The deploy lane is baked: all five services are **digest-pinned in `docker-compose.yml` with no
`build:` fallback**, resolved through `/etc/comfy-p7/environment` alone; Gateway changes arrive as
a locally-cut image via `Promote-GatewayImage.ps1`; post-workbench-deploy content updates are pure
file copies. The systemd unit (`comfy-lumberjacks-p7.service`, `WantedBy=multi-user.target`) runs
`docker compose up -d` on boot, and the compose services carry `restart: unless-stopped`. The
README records this exact path as verified by a real `systemctl restart`.

> **FALSIFIED 2026-07-30 — do not treat stop/start as self-healing yet.** This section used to
> conclude "a stopped VM re-enters service predictably — no hand-built state to lose." A cold
> stop/start cycle left six containers in `Created` and nothing serving, while SSH answered
> normally. Two premises were wrong: a `systemctl restart` does not exercise the reboot path
> (it skips the state-disk mount race and the shutdown teardown), and the unit's **enablement
> was itself hand-built state** — nothing in this repo ever installed or enabled
> `comfy-lumberjacks-p7.service`. Fixes are staged and unverified; diagnosis, the by-hand apply
> steps, and the next-boot verification procedure are in
> [`RUNBOOK-boot-determinism.md`](RUNBOOK-boot-determinism.md). Budget a boot check into every
> stop/start until that runbook's step 3 passes.

One timing fact to respect every time: **the ~9.1M-ZDO `ComfyEra16` world takes ~a minute to
reload. The server is not joinable until the log emits `Game server connected`.**

---

## A. BigQuery billing export — do first, zero risk

**What it buys:** turns this whole cost picture from ±20% list-price arithmetic into invoiced,
per-SKU truth queryable via `bq`. The memo's top data recommendation. Export has **no backfill**
— data accrues only from enablement forward, which is exactly why it's tonight's first move.
First rows land in ~24–48h.

**Risk:** none. BigQuery storage for billing data is pennies.

**Do:**

1. Create a dataset to receive it:

```
bq mk --dataset --location=US lumberjacks-exp-20260711-djc:billing_export
```

2. Enable the export — **console only** (there is no gcloud surface for this):
   Billing → **Billing export** → BigQuery export → **Standard usage cost** → edit settings →
   project `lumberjacks-exp-20260711-djc`, dataset `billing_export` → Save.
   (Needs Billing Account Administrator on the billing account — you.)

3. Optional but cheap while you're in there — a budget alert (`billingbudgets.googleapis.com`
   is already enabled on the project):

```
gcloud billing budgets create \
  --billing-account=<BILLING_ACCOUNT_ID> \
  --display-name="p7-monthly" --budget-amount=120USD \
  --threshold-rule=percent=0.5 --threshold-rule=percent=0.9 --threshold-rule=percent=1.0
```

`<BILLING_ACCOUNT_ID>`: `gcloud billing accounts list` (format `XXXXXX-XXXXXX-XXXXXX`).

**Verify (in ~24–48h, not tonight):**

```
bq ls lumberjacks-exp-20260711-djc:billing_export
bq query --use_legacy_sql=false "SELECT service.description, ROUND(SUM(cost),2) AS usd
  FROM \`lumberjacks-exp-20260711-djc.billing_export.gcp_billing_export_v1_<BILLING_ACCOUNT_ID_UNDERSCORED>\`
  GROUP BY 1 ORDER BY 2 DESC"
```

(Table name appears in the dataset once data flows; the suffix is the billing account ID with
underscores.)

**Rollback:** disable the export in the same console pane; drop the dataset if you like.

---

## B. Orphaned pre-cutover snapshots — ~$6.50/mo, DESTRUCTIVE

**What they are:** the old 150 GB disk was deleted 2026-07-24, but
`onSourceDiskDelete: KEEP_AUTO_SNAPSHOTS` left its lineage behind and it will never age out:
**250.47 GB of dead-disk snapshots** — seven daily auto-snapshots (2026-07-17 → 07-23,
178.44 GB, ~$4.6/mo) plus `comfy-p7-state-precutover-20260724` (72.03 GB, ~$1.87/mo). The
live 32 GB `state-v2` lineage (18.34 GB) is healthy and **not** touched here.

**Risk:** deletion is permanent. The memo's own hedge: the precutover snapshot is the largest
single item **and the rollback point for the disk cutover** — both the memo and RECONCILE-GAP
list it as a restore point. Deleting the seven auto-snapshots is low-regret once you confirm
`state-v2` and the world are healthy; the precutover one is a trust call on the 07-24 cutover.

**Do — list first, eyeball creation dates and sizes yourself:**

```
gcloud compute snapshots list --project=lumberjacks-exp-20260711-djc \
  --format="table(name,creationTimestamp,storageBytes.size(zeroIfMissing=true),sourceDisk.basename())" \
  --sort-by=creationTimestamp
```

Expect: 7 auto-snapshots sourced from the **deleted** `comfy-lumberjacks-p7-state` (dates
07-17…07-23), the named precutover snapshot, and the live `-state-v2` dailies (7-day retention,
leave alone).

**Then delete the seven dead auto-snapshots** (memo's exact list):

```
gcloud compute snapshots delete \
  comfy-lumberjacks-p-us-west1-b-20260717105022-1li4f4a0 \
  comfy-lumberjacks-p-us-west1-b-20260718105022-zopa3hsp \
  comfy-lumberjacks-p-us-west1-b-20260719105022-1hf6jta0 \
  comfy-lumberjacks-p-us-west1-b-20260720105022-j08pag2n \
  comfy-lumberjacks-p-us-west1-b-20260721105022-06p03fu7 \
  comfy-lumberjacks-p-us-west1-b-20260722105022-27bgp4y5 \
  comfy-lumberjacks-p-us-west1-b-20260723105022-nhxiyrky \
  --project=lumberjacks-exp-20260711-djc
```

**Separately, if and only if you now trust the cutover** (~$1.87/mo more):

```
gcloud compute snapshots delete comfy-p7-state-precutover-20260724 \
  --project=lumberjacks-exp-20260711-djc
```

**Verify:** re-run the list command — only `-state-v2`-sourced snapshots (and precutover, if
kept) remain.

**Rollback:** none. That's the point of the list-first step.

---

## C. VM duty-cycle scheduling — the biggest lever

> The VM hosts the Valheim world + Gateway, so they're offline while it's stopped — but per
> operator correction 4, today's players are Derek's own accounts and name-known friends.
> **A quick ping to the handful is courteous; no product-hours commitment exists yet.** Revisit
> the loud version of this warning when a real external cohort clears the readiness gates.
> Given that, the **aggressive end (stopped except session windows) is the natural dev-season
> default**, not the cautious 8h-nightly compromise.

**What it saves (at ~$0.105/hr compute):**

| Duty cycle | Compute saved |
|---|---|
| Off 8h nightly (e.g. 02:00–10:00 local) | **~$25/mo** |
| Off 16h/day (evenings-only service) | **~$50/mo** |
| Stopped except session windows | up to ~$65–75/mo (approaches the full ~$76–96) |

Honest arithmetic note: the ~$50–70/mo band requires evenings-only or session-only hours;
plain "off overnight" is ~$25/mo. Stack with lever D for more.

**Risks / erosion:** disks, snapshots, and buckets keep billing while stopped (~$14/mo floor).
The static IP bills at the *higher* unused rate while the VM is down — list-price ~$0.01/hr
unused vs ~$0.004/hr attached, so an 8h nightly stop adds roughly ~$1.5/mo back (list-price
behavior, not memo-verified — the export from lever A will show it exactly). Snapshot schedules
run against disks regardless of instance state.

**Do — Option 1, native instance schedule (set-and-forget):**

```
# One-time: the Compute Engine system service agent must be allowed to stop/start the VM,
# or the schedule silently no-ops.
gcloud projects describe lumberjacks-exp-20260711-djc --format="value(projectNumber)"

gcloud projects add-iam-policy-binding lumberjacks-exp-20260711-djc \
  --member="serviceAccount:service-<PROJECT_NUMBER>@compute-system.iam.gserviceaccount.com" \
  --role="roles/compute.instanceAdmin.v1"

# The schedule itself (example: stop 02:00, start 10:00, your local time — cron is in the
# policy's timezone; pick yours, e.g. America/New_York):
gcloud compute resource-policies create instance-schedule p7-nightly-off \
  --project=lumberjacks-exp-20260711-djc --region=us-west1 \
  --timezone="<TZ, e.g. America/New_York>" \
  --vm-stop-schedule="0 2 * * *" \
  --vm-start-schedule="0 10 * * *"

gcloud compute instances add-resource-policies comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b \
  --resource-policies=p7-nightly-off
```

**Option 2, manual stop/start** (no standing policy; fine while you're deciding):

```
gcloud compute instances stop comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b

gcloud compute instances start comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b
```

**Verify (after the first scheduled or manual start — prove the reboot path once):**

```
gcloud compute instances describe comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b \
  --format="value(status,resourcePolicies)"

# Gateway back:
Invoke-RestMethod http://8.231.129.249:42317/health

# World joinable — wait for this line before telling anyone to join (~1 min after boot):
ssh comfy-p7 "sudo docker logs --since 15m \$(sudo docker ps -qf name=valheim) 2>&1 | grep 'Game server connected'"
```

**Rollback:**

```
gcloud compute instances remove-resource-policies comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b \
  --resource-policies=p7-nightly-off

gcloud compute resource-policies delete p7-nightly-off \
  --project=lumberjacks-exp-20260711-djc --region=us-west1
```

Terraform note: schedule/machine-type changes widen the already-open drift. That's accepted —
log them mentally for the RECONCILE-GAP close-out; do not "fix" it with an apply.

---

## D. Machine type — REJECTED as written; one optional family check remains

**Operator call (correction 2): the 8 GB downsizes are dead.** 2 vCPU / 16 GB is the declared
floor — the ~9.1M-ZDO world + five-service stack keep the highmem shape. Do not run the
memo's `n2-standard-2` / `e2-standard-2` triplet.

**The one thing left worth a look at shakedown time:** a *same-shape family swap* —
`n2-highmem-2` → `e2-highmem-2` (still 2 vCPU / 16 GB). The e2 family lists cheaper for the
same shape, but **no number is stated here on purpose**: price it from lever A's invoiced
data once it lands, and only bother if the delta is real. Same stop → `set-machine-type` →
start mechanics as below, same three verifies, minutes to roll back.

**Do (only for the optional e2-highmem-2 check — same-shape swap):**

```
gcloud compute instances stop comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b

gcloud compute instances set-machine-type comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b \
  --machine-type=e2-highmem-2      # same 2 vCPU / 16 GB shape — the floor holds

gcloud compute instances start comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b
```

**Verify — all three, in order, before telling anyone to join:**

```
gcloud compute instances describe comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b \
  --format="value(machineType)"

Invoke-RestMethod http://8.231.129.249:42317/health

ssh comfy-p7 "sudo docker logs --since 15m \$(sudo docker ps -qf name=valheim) 2>&1 | grep 'Game server connected'"
```

Then watch `free -h` once under a real player session before calling it done.

**Rollback:** same stop → `set-machine-type --machine-type=n2-highmem-2` → start → same three
verifies. Minutes, not drama.

---

## E. Backup posture: prod cadence during dev is the disk bleed

**What's happening (operator correction 3):** the valheim-server container's hourly
production-grade world backups — plus push-time snapshot ceremony — run at prod cadence
while the work is dev iteration. On a ~9.1M-ZDO world that compounds fast, and it's the
actual reason "the HD keeps exploding." The world is an heirloom copy (saved world from an
earlier era on another server) already preserved outside this VM; the dev/prod backup split
already exists in this repo (env-driven — dev runs with backups OFF *on purpose*).

**Do — confirm the exact switch on-box first (one grep), then flip to dev posture:**

```
ssh comfy-p7 "grep -i backup /etc/comfy-p7/environment; sudo docker inspect \$(sudo docker ps -qf name=valheim) --format '{{range .Config.Env}}{{println .}}{{end}}' | grep -i backup"
```

Expect the image's `BACKUPS*` family (`BACKUPS=true/false`, cadence/retention vars). Then set
the dev posture in `/etc/comfy-p7/environment` (back the file up first — same discipline as
the promote script) and `sudo docker compose up -d --no-deps valheim-server` from
`/opt/comfy/infra/gcp/p7`. **Re-arm rule:** prod cadence comes back on before any real
external-cohort window — write that in the same environment-file comment so it can't be
forgotten.

**Verify:** the backups dir stops growing (`ssh comfy-p7 "du -sh <backups-path>"` before/after
an hour); world saves themselves are untouched (the .db atomic-write save is a different
mechanism — verify in files, not logs, per standing practice).

**Rollback:** flip the env back, `up -d --no-deps valheim-server`. Minutes.

Also at shakedown time: the daily `state-v2` snapshot schedule (7-day retention) is sized for
prod trust; during dev season a sparser cadence is defensible — decide when the invoiced
numbers from A show what it actually costs.

---

## Decision table

| Lever | $/mo saved | Risk | Your minutes |
|---|---|---|---|
| **A** Billing export (+budget) | $0 (buys truth) | none | ~10 |
| **B** Delete 7 dead auto-snapshots | ~$4.6 (+$1.9 if precutover goes) | permanent; list-first; precutover = cutover rollback point | ~5 |
| **C** Duty-cycle: stopped except sessions | up to ~$65–75 | world offline while stopped — ping the friends; first-restart proof pending | ~15 |
| **D** e2-highmem-2 family swap (optional) | unknown until A prices it | one ~10-min outage window; same 2/16 shape | ~15, at shakedown |
| **E** Dev backup posture | disk growth stops compounding | must re-arm before a real cohort window | ~10 |

**Tonight (lean and mean, per the operator):** run **A now** (export only accrues forward) and
otherwise leave GCP alone while you drive the finish line.
**End-of-night full shakedown, in order:** **B** (list, eyeball, delete the seven; precutover
is a trust call) → **E** (flip to dev backup posture, write the re-arm rule) → **C** (set the
schedule or just stop it when you log off; **watch the first restart once** — it's the last
unproven claim) → **D-prime** (only if A's invoiced data shows the e2 swap is worth a window).
Combined with the corrections, the realistic dev-season burn floor is the ~$14/mo
storage+IP floor plus compute only for hours you're actually on.

---

## 2026-09-06 reclamation — what was actually burning

Triggered by ~$36 of unexplained spend over six days. The VM had been left running
since 2026-08-30 with zero players connected. The compute was never the problem.

**The bill was telemetry, not the VM.** Cloud Monitoring bills chargeable metric volume
as `series x samples x bytes-per-sample`, and that product is independent of load: an
idle server pays exactly what a busy one pays. Measured 69 MiB/day chargeable at
$0.258/MiB (150 MiB/month free), against roughly $1/day for the e2-medium itself.

| Source | MiB/day | Why it was expensive |
|---|---|---|
| `workload.googleapis.com` (app OTel) | 44 | 65 series at a **10s** export interval, mostly CUMULATIVE histograms with 14 bucket bounds (~70 B/sample vs ~8 for a gauge). A cumulative histogram is re-sent in full every interval whether or not a request arrived. |
| `agent.googleapis.com` (Ops Agent) | 25 | `processes/*` emits one series per process per metric: 73 processes x 5 metrics = 365 series at 30s, for things like `agetty` and `chronyd`. |

Fixes: export interval 10s -> 60s (`docker-compose.yml`), and `processes/*` dropped at
the agent via an `exclude_metrics` processor (`ops-agent-config.yaml`). Expected ~14
MiB/day. The alert policies in `monitoring.tf` read `memory/percent_used`,
`swap/percent_used`, `disk/percent_used` and `agent/uptime` — none are in `processes/*`,
so nothing was disarmed. **Verified**: process series stopped at the agent restart while
all four alert dependencies kept reporting.

**Disk reclamation, 52 GiB -> 22.4 GiB used.** Largest single item was a **16 GiB
swapfile with 0 B ever used**, sized in `bootstrap.sh.tftpl` for the 64 GiB machine named
in its own comment; this is an e2-medium with 3.8 GiB RAM. Now 4 GiB. Also: 5.7 GiB
docker build cache, 6.3 GiB of superseded Valheim world copies (`.old`, `_backup_auto-*`,
one aborted partial — the live world and `CreatorOSBeta1` were kept), 5.4 GiB of July
promotion-drill snapshots, and a 1.9 GiB `.invalid-partial`.

State disk rebuilt 32 GiB -> 15 GiB as `comfy-p7-state-v3` (PDs cannot be shrunk; this was
a copy-and-swap). Verified byte-identical before the swap: 8,905,030,474 bytes and 4,768
files on both sides, checksum-mode rsync clean, then the stack proven running on it.
Re-attached under device name `comfy-p7-state` because `bootstrap.sh.tftpl` hard-codes
`/dev/disk/by-id/google-comfy-p7-state` and **exits 1** if it is missing. The daily
snapshot policy was attached to the old disk and had to be moved by hand.

### Two hazards found on the way, neither of them cost

1. **The boot disk is the only copy of every release image.** All 76 images are
   local-only tags; there is no Artifact Registry or Container Registry in the project,
   and `docker-compose.yml` states there is deliberately no `build:` fallback. The boot
   disk also carries `auto_delete = true`. Deleting the instance destroys every promoted
   image and every rollback target. **Do not rebuild or replace the boot disk before
   exporting the active images.** This is why `boot_disk_size_gb` was only reduced in
   `variables.tf` (for a future clean provision) and the live 40 GiB disk was left alone.

2. **Deployed instance metadata had drifted from this repo.** The live `startup-script`
   was 3,315 bytes against the repo's 8,076, and still carried a Cloud Logging docker
   receiver that this repo recorded as disabled on 2026-07-23 — it had never stopped
   running. The live copy also predates the docker `RequiresMountsFor=/mnt/comfy-p7`
   drop-in and the stack-unit installer. Only the agent-config and swapfile blocks were
   patched in metadata; the rest was left for a deliberate review.

   Root cause: **there is no terraform state in the repo and no remote backend**, so
   `terraform apply` would try to create this stack rather than update it. Changes here
   have to be made against instance metadata directly. `data_disk_size_gb` defaulted to
   150 while the live disk was 32 — that default never described anything deployed.

### Release images are now backed up off the boot disk

Closed hazard 1 on 2026-09-06. All 73 local-only tags (62 distinct images) are archived to:

    gs://comfy-p7-cutover-lumberjacks-exp-20260711-djc/release-images/
      p7-release-images-20260906.tar.gz        536 MiB, crc32c fm19YA==
      p7-release-images-20260906.manifest.txt  tag list + the four tags live at export

Restore:

    gcloud storage cp gs://.../p7-release-images-20260906.tar.gz - | gunzip | docker load

Verified at creation: crc32c matches between the VM-side archive and the stored object,
the tar opens cleanly, `manifest.json` lists 62 images, and all four tags named in
`/etc/comfy-p7/environment` are present along with 69 rollback tags.

The VM's own service account **cannot write this bucket** (`storage.objects.create`
denied), so the export was pulled to a workstation and pushed from there. If this should
become a scheduled job rather than a manual one, that binding has to be granted first —
it was deliberately not granted here, to avoid widening the runtime service account for a
one-off.

Re-export whenever an image is promoted. The archive is a point-in-time copy, not a
mirror; a tag promoted after 2026-09-06 exists only on the boot disk until this is re-run.
