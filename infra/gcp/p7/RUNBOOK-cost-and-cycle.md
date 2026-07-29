# P7 cost & duty-cycle runbook — 2026-07-28

Operator: **Derek, at the keyboard**. Agents are classifier-blocked from GCP mutations, so
every command below is staged for you to paste. Nothing here has been executed.

Grounding: [`docs/audit/2026-07-25-gcp-burn-rate-review.md`](../../../docs/audit/2026-07-25-gcp-burn-rate-review.md)
(the burn memo — all dollar figures are its list-price estimates, ±20%, no invoiced truth yet),
[`README.md`](README.md), [`RECONCILE-GAP.md`](RECONCILE-GAP.md).

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
README records this exact path as **verified by a real `systemctl restart`, "which is exactly what
the reboot path runs."** So a stopped VM re-enters service predictably — no hand-built state to
lose. (Verified from repo files + README's restart claim; tonight's first scheduled restart is
still the live proof — watch it once.)

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

## C. VM duty-cycle scheduling — the biggest lever, and a cohort decision

> **⚠️ This VM is the LIVE alpha: the Valheim world AND the Gateway. While it is stopped, the
> alpha cohort has no game world to join and no Gateway.** Before enabling any schedule, **post
> the service hours in Discord first** and treat them as a commitment. This is a product-hours
> decision wearing a cost hat.

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

## D. Right-size the machine type — ~$25–45/mo, needs one stop/start window

> **⚠️ Same live-alpha warning as C: the world and Gateway are down for the window (~5–10 min
> including world reload). Post it in Discord first.**

**Options (memo's list, cheapest last):**

| Target | RAM | Saves vs n2-highmem-2 |
|---|---|---|
| `n2-standard-2` | 8 GB | ~$25/mo |
| `e2-standard-2` | 8 GB | ~$45/mo |

**The judgment call is memory headroom, and it's yours:** 16 GB is generous for a Valheim
dedicated server, but this one carries a ~9.1M-ZDO world plus the five-service Lumberjacks
stack. You know the real footprint. Measure before committing:

```
ssh comfy-p7 "free -h; sudo docker stats --no-stream"
```

If steady-state RSS + page cache comfortably fits under ~6–7 GB, 8 GB flies. If it's marginal,
this lever waits — a swapping world server is worse than $45.

**Do (memo's exact command triplet):**

```
gcloud compute instances stop comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b

gcloud compute instances set-machine-type comfy-lumberjacks-p7 \
  --project=lumberjacks-exp-20260711-djc --zone=us-west1-b \
  --machine-type=n2-standard-2      # or e2-standard-2

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

## Decision table

| Lever | $/mo saved | Risk | Your minutes |
|---|---|---|---|
| **A** Billing export (+budget) | $0 (buys truth) | none | ~10 |
| **B** Delete 7 dead auto-snapshots | ~$4.6 (+$1.9 if precutover goes) | permanent; list-first; precutover = cutover rollback point | ~5 |
| **C** Duty-cycle schedule | ~$25 (8h nightly) → ~$50–75 (aggressive) | **live alpha offline during window**; IP bills more while stopped; first-restart proof | ~15 + a Discord post |
| **D** Right-size to 8 GB | ~$25 (n2) / ~$45 (e2) | **one live outage window**; memory headroom is your call | ~15 + a Discord post |

**Suggested tonight:** A now — it only pays if it starts accruing. B: run the list, eyeball,
delete the seven; sleep on the precutover one if the cutover isn't fully trusted yet. C and D
are real decisions, not chores — they stack (~$70–95/mo combined at the aggressive end), but
both put the cohort's world offline on a rhythm, so pick with the cohort in mind and post the
hours before the hours exist.
