# Google Cloud Stage 1 Runbook

This runbook proves that the known Lumberjacks Docker Compose stack can run on a
remote Linux VM over the public internet. It is intentionally not a production
deployment: Gate 1 uses plaintext WebSocket traffic, local Compose credentials, and
PostgreSQL on an attached persistent disk. It does, however, establish production-
grade logging, monitoring, tracing, dashboards, uptime checks, and alerts before the
application is deployed.

Do not use real player data or production credentials in this environment.

## Completion status

The Stage 1 implementation is checked in. The migration gate is **not yet complete**:
the repository does not contain a Gate 1 evidence bundle proving an external native
client session, UDP delivery, restart persistence, trace-correlated logs, populated
metrics, and delivered alerts from the same pinned revision.

Cloud status as of 2026-07-11 local / 2026-07-12 UTC (HISTORICAL - superseded by the
2026-07-20 reconciliation below; the machine type and Gateway port in this paragraph
are no longer current): the proven deployment is
the combined Comfy/Lumberjacks P7 Valheim netcode replacement environment, not this
original Godot Stage 1 proof. That deployment lives under `C:\work\comfy\infra\gcp\p7`
and runs VM `comfy-lumberjacks-p7` (`n2-highmem-8`, us-west1-b) in project
`lumberjacks-exp-20260711-djc`.
It exposes Valheim at `8.231.129.249:2456`, Lumberjacks Gateway at
`http://8.231.129.249:4000`, and Compose services `postgres`, `gateway`, `eventlog`,
`progression`, `operatorapi`, and `valheim-server`. Its P7 gate passed for handshake,
ownership pin, redirect, injection, client stability, and save integrity, and the
server was disarmed afterward.

Cutover-test update (2026-07-12): the full P7 cutover test was rerun and passed
end-to-end. An earlier attempt failed with an out-of-memory condition while the
Valheim server, Lumberjacks authority services, and PostgreSQL were co-located on a
smaller instance; sizing the VM up to `n2-highmem-8` (8 vCPU / 64 GB) cleared it.
This is operator-confirmed, not a re-collected Section 8 evidence bundle. The
original Godot Stage 1 VM (`lumberjacks-stage1`, `e2-medium`) was stopped on
2026-07-12 as superseded; `comfy-lumberjacks-p7` remains running.

Reconciliation, 2026-07-20 (closes the "Unreconciled" note in
`C:\work\comfy\fieldlab\evidence\p7-session-20260719-release-gate-defect\deployment-identifiers.md`):
the two paragraphs above are accurate as history but stale as present tense. Read from
the GCP service inventory on 2026-07-20, `comfy-lumberjacks-p7` is:

- **`n2-highmem-2` (2 vCPU / 16 GB)**, not `n2-highmem-8`. The 8 -> 2 downsize was a
  deliberate cost decision (roughly a third of the spend, about three times the
  runway), not a regression, and it is not to be re-flagged as an OOM risk. The
  2026-07-12 sizing-up note above records why the instance was grown at the time; it
  does not describe the instance today.
- **`TERMINATED` between windows**, not "remains running". Start it explicitly for a
  window (`gcloud compute instances start comfy-lumberjacks-p7`).
- Reachable on the public Gateway at **`:42317`**, not `:4000` - there is no firewall
  rule for `:4000`, so the `http://8.231.129.249:4000` above is unreachable from
  outside the VM even when it is running.
- Disks unchanged and API-confirmed: 40 GB boot + 150 GB state, both `pd-balanced`.

Use this runbook as the completion plan. Sections 1-4 prepare and deploy one immutable
revision, Sections 5-7 execute the live proof, Section 8 records the evidence, and
Section 9 controls cost after the result. Do not mark Gate 1 complete unless every
row in the Section 8 checklist has an artifact or an explicit failing result.

## 1. Prerequisites

Install and authenticate the following on the operator workstation:

- Google Cloud CLI (`gcloud`);
- Terraform 1.6 or later;
- Git;
- .NET SDK, Node.js, and Docker for the local baseline.

Authenticate both the CLI and Terraform:

```bash
gcloud auth login
gcloud auth application-default login
```

Choose these values before continuing:

```bash
export PROJECT_ID="lumberjacks-experiment"
export BILLING_ACCOUNT_ID="000000-000000-000000"
export REVISION="<full-commit-sha>"
```

Create and bill a disposable experiment project if one does not already exist:

```bash
gcloud projects create "$PROJECT_ID" --name="Lumberjacks experiment"
gcloud billing projects link "$PROJECT_ID" --billing-account="$BILLING_ACCOUNT_ID"
gcloud config set project "$PROJECT_ID"
gcloud auth application-default set-quota-project "$PROJECT_ID"
```

The operator needs sufficient project permissions to manage Compute Engine, service
accounts, and project services. IAP access requires IAP-secured Tunnel User plus an
OS Login or OS Admin Login role. Creating the optional Terraform budget also requires
permission to manage budgets on the billing account.

## 2. Record the local baseline

From the repository root, record the exact source revision and run the existing test
suite before provisioning cloud resources:

```bash
mkdir -p .tmp/gcp-stage1/local
git rev-parse HEAD | tee .tmp/gcp-stage1/local/revision.txt
dotnet test Game.sln --filter Category!=Performance 2>&1 | tee .tmp/gcp-stage1/local/dotnet-test.log
npm ci
docker compose -f infra/docker/docker-compose.yml up -d --build
node scripts/test-movement.js ws://localhost:4000 2>&1 | tee .tmp/gcp-stage1/local/movement.log
node scripts/load-test-dual-channel.js ws://localhost:4000 2 30 2>&1 | tee .tmp/gcp-stage1/local/dual-channel.log
docker compose -f infra/docker/docker-compose.yml ps | tee .tmp/gcp-stage1/local/compose-ps.txt
docker compose -f infra/docker/docker-compose.yml logs --no-color > .tmp/gcp-stage1/local/compose.log
```

Gate 1 requires a nonzero `Total UDP inputs sent` and `Total UDP entity_updates` in
the dual-channel summary. A passing process exit by itself is not sufficient because
the load script reports WebSocket fallback as a warning rather than a failure.

## 3. Provision the VM

Create a local variables file. Do not commit it:

```bash
cd infra/gcp/stage1
cp terraform.tfvars.example terraform.tfvars
```

Set `project_id`, select a region and zone, replace the example tester address in
`gameplay_source_ranges`, and set `alert_email` to the operator who will acknowledge
the channel and receive the controlled alert. Use the public egress CIDR of the
Godot/test workstation when practical. Set `billing_account_id` to create the
optional monthly budget.

Terraform reads the current public uptime-checker addresses from the Monitoring API
and permits those addresses to reach TCP 4000 in a separate firewall rule. Do not add
them to `gameplay_source_ranges`, and do not widen UDP 4005 for the uptime check.

Initialize, review, and apply:

```bash
terraform init
terraform fmt -check
terraform validate
terraform plan -out=stage1.tfplan
terraform apply stage1.tfplan
terraform output
```

Save the public IP, VM name, and zone shown in the outputs:

```bash
export PUBLIC_IP="$(terraform output -raw public_ip)"
export VM_NAME="$(terraform output -raw vm_name)"
export ZONE="$(terraform output -raw zone)"
```

Terraform enables OS Login and permits SSH only through Identity-Aware Proxy. Connect
with:

```bash
gcloud compute ssh "$VM_NAME" --project "$PROJECT_ID" --zone "$ZONE" --tunnel-through-iap
```

On the VM, confirm bootstrap completion before deployment:

```bash
sudo tail -n 100 /var/log/lumberjacks-bootstrap.log
docker compose version
findmnt /mnt/lumberjacks
sudo test -d /mnt/lumberjacks/postgres
sudo systemctl is-active google-cloud-ops-agent
sudo ss -lntp | grep 4317
```

Docker Compose must be 2.24.4 or later because the Gate 1 override uses the documented
`!override` merge tag.

## 4. Deploy the pinned stack

Run these commands on the VM, replacing the revision with the value selected earlier:

```bash
REVISION="<full-commit-sha>"
PROJECT_ID="lumberjacks-experiment"
sudo git clone https://github.com/djcdevelopment/Lumberjacks.git /opt/lumberjacks
sudo git -C /opt/lumberjacks checkout --detach "$REVISION"
test "$(sudo git -C /opt/lumberjacks rev-parse HEAD)" = "$REVISION"
cd /opt/lumberjacks
sudo install -d -m 0755 /etc/lumberjacks
printf 'GOOGLE_CLOUD_PROJECT=%s\nLUMBERJACKS_VERSION=%s\n' "$PROJECT_ID" "$REVISION" | sudo tee /etc/lumberjacks/environment
sudo docker compose -f infra/docker/docker-compose.yml -f infra/docker/docker-compose.gcp-stage1.yml build
sudo install -m 0644 /opt/lumberjacks/infra/gcp/stage1/lumberjacks-compose.service /etc/systemd/system/lumberjacks-compose.service
sudo systemctl daemon-reload
sudo systemctl enable --now lumberjacks-compose.service
```

Review the effective Compose model and service state:

```bash
cd /opt/lumberjacks
sudo docker compose -f infra/docker/docker-compose.yml -f infra/docker/docker-compose.gcp-stage1.yml config
sudo docker compose -f infra/docker/docker-compose.yml -f infra/docker/docker-compose.gcp-stage1.yml ps
curl --fail http://127.0.0.1:4000/health
curl --fail http://127.0.0.1:4002/health
curl --fail http://127.0.0.1:4003/health
curl --fail http://127.0.0.1:4004/health
```

The effective model must show only TCP 4000 and UDP 4005 bound to all interfaces.
PostgreSQL and ports 4002-4004 must bind only to `127.0.0.1`.
`systemctl show lumberjacks-compose.service -p RequiresMountsFor` must include
`/mnt/lumberjacks`, preventing the stack from starting against an unmounted path.

## 5. Validate external gameplay

On an external test workstation at the allowed source address:

```bash
mkdir -p .tmp/gcp-stage1/gcp
node scripts/test-movement.js "ws://$PUBLIC_IP:4000" 2>&1 | tee .tmp/gcp-stage1/gcp/movement.log
node scripts/load-test-dual-channel.js "ws://$PUBLIC_IP:4000" 2 30 2>&1 | tee .tmp/gcp-stage1/gcp/dual-channel.log
```

Connect the native Godot client to the same WebSocket endpoint and complete the named
two-player scenario. Record the client version, server revision, world identifier,
movement result, transport mode, timing telemetry, and produced artifacts.

On the VM, execute the full vertical slice through the loopback-only service ports:

```bash
cd /opt/lumberjacks
npm ci
node scripts/test-vertical-slice.js ws://localhost:4000
```

## 6. Verify persistence and reboot recovery

After the vertical-slice test has persisted a structure, capture the world response:

```bash
sudo install -d -m 0755 /mnt/lumberjacks/evidence
curl --fail --silent "http://127.0.0.1:4000/structures?region_id=region-spawn" | jq -S . | sudo tee /mnt/lumberjacks/evidence/structures-before.json
sudo reboot
```

Reconnect through IAP after the VM returns, then verify the stack, mount, and data:

```bash
systemctl is-active lumberjacks-compose.service
findmnt /mnt/lumberjacks
curl --retry 20 --retry-delay 3 --retry-connrefused --fail --silent "http://127.0.0.1:4000/structures?region_id=region-spawn" | jq -S . | tee /tmp/structures-after.json
diff -u /mnt/lumberjacks/evidence/structures-before.json /tmp/structures-after.json
```

The named scenario must also be rerun after restart; matching HTTP output alone does
not prove gameplay recovery.

## 7. Verify observability

Allow two metric export intervals after running the gameplay scenarios. Then verify:

```bash
sudo systemctl status "google-cloud-ops-agent*" --no-pager
sudo journalctl -u google-cloud-ops-agent --since "15 minutes ago" --no-pager
sudo grep -E "LogParseErr|Exporting failed|PermissionDenied" /var/log/google-cloud-ops-agent/subagents/*.log || true
```

In Google Cloud Observability, confirm all of the following:

- the **Lumberjacks — Gate 1 Operations** dashboard contains host and application
  time series;
- application logs have a promoted Cloud Logging `severity` and a `service` field;
- request- or message-scoped logs have `trace_id` and `span_id` fields;
- a structured log trace link opens the corresponding Cloud Trace waterfall;
- `workload.googleapis.com/lumberjacks.tick.duration` and session/UDP/delivery metrics
  are present;
- the public uptime check reports from multiple checker regions;
- all six alert policies reference the operator notification channel; and
- a controlled test alert reaches the operator, after which the test condition is
  removed or restored.

## 8. Collect Gate 1 evidence

On the VM:

```bash
mkdir -p /tmp/lumberjacks-stage1-evidence
git -C /opt/lumberjacks rev-parse HEAD > /tmp/lumberjacks-stage1-evidence/revision.txt
sudo docker compose -f /opt/lumberjacks/infra/docker/docker-compose.yml -f /opt/lumberjacks/infra/docker/docker-compose.gcp-stage1.yml ps > /tmp/lumberjacks-stage1-evidence/compose-ps.txt
sudo docker compose -f /opt/lumberjacks/infra/docker/docker-compose.yml -f /opt/lumberjacks/infra/docker/docker-compose.gcp-stage1.yml logs --no-color > /tmp/lumberjacks-stage1-evidence/compose.log
sudo journalctl -u lumberjacks-compose.service --no-pager > /tmp/lumberjacks-stage1-evidence/systemd.log
sudo cp /var/log/lumberjacks-bootstrap.log /tmp/lumberjacks-stage1-evidence/bootstrap.log
sudo chown -R "$USER":"$USER" /tmp/lumberjacks-stage1-evidence
tar -C /tmp -czf /tmp/lumberjacks-stage1-evidence.tgz lumberjacks-stage1-evidence
```

Copy the archive to the workstation and store it with the local/GCP scenario outputs:

```bash
gcloud compute scp "$VM_NAME:/tmp/lumberjacks-stage1-evidence.tgz" .tmp/gcp-stage1/gcp/ --project "$PROJECT_ID" --zone "$ZONE" --tunnel-through-iap
```

Gate 1 passes only when all criteria in the
[migration strategy](google-cloud-migration-strategy.md#12-gated-success-criteria)
have evidence attached. Record failures as failures; do not alter multiple platform
layers to make the first experiment pass.

Use this checklist before recording the gate decision:

| Required proof | Minimum evidence |
|---|---|
| Pinned deployment | `revision.txt`, effective Compose model, and healthy service list |
| External native client | Client/server versions, endpoint, players, world/scenario identifier, and result |
| WebSocket behavior | Movement and two-player scenario logs showing binary WebSocket success |
| UDP behavior | Dual-channel log with nonzero UDP inputs and UDP entity updates |
| Full vertical slice | Passing `test-vertical-slice.js` output from the deployed revision |
| Durable persistence | Before/after structure JSON, successful diff, and post-reboot scenario result |
| Centralized logs and traces | Structured log sample plus a trace link or trace identifier from the same operation |
| Metrics and dashboard | Timestamped capture or exported query results for host, tick, session, UDP, and delivery signals |
| Availability and alerts | Healthy multi-region uptime result and proof the controlled alert reached `alert_email` |
| Gate decision | Date, operator, revision, pass/fail, known failures, and artifact location |

The local ignored Terraform state and plan files are not Gate evidence. Export the
artifacts above to durable storage before stopping or destroying the experiment.

## 9. Stop or remove the experiment

Stop the VM between test sessions to reduce compute charges while retaining both
disks and the reserved address:

```bash
gcloud compute instances stop "$VM_NAME" --project "$PROJECT_ID" --zone "$ZONE"
```

When evidence has been exported and the experiment is no longer needed, destroy the
Terraform resources:

```bash
cd infra/gcp/stage1
terraform plan -destroy -out=destroy.tfplan
terraform apply destroy.tfplan
```

Terraform destruction permanently deletes the PostgreSQL persistent disk. Confirm
that the evidence and any required disk snapshot exist before approving destruction.
