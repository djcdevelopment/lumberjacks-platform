# Google Cloud Migration Strategy

Status: Stage 1 implementation complete; original Godot Gate 1 evidence pending;
Comfy/Lumberjacks P7 deployment proven; full P7 cutover test passed on
`n2-highmem-8` after resolving the earlier out-of-memory failure

Last reviewed: 2026-07-12

## Current GCP deployment note

As of 2026-07-11 local / 2026-07-12 UTC, the active proven cloud deployment is the
combined Valheim x Lumberjacks netcode replacement environment, not the original
Godot multiplayer vertical slice described by the Stage 1 gate below. The deployment
runs from the Comfy repository's `infra/gcp/p7` Terraform root and co-locates the
migrated `ComfyEra16` Valheim dedicated server, ComfyNetworkSense 0.5.18, and the
Lumberjacks authority services on GCP VM `comfy-lumberjacks-p7` (`n2-highmem-8`,
us-west1-b) in project `lumberjacks-exp-20260711-djc`.

Cutover-test update (2026-07-12): the full P7 cutover test has been run end-to-end
and passed. An earlier run failed under memory pressure (out-of-memory) when the
co-located Valheim server, Lumberjacks authority services, and PostgreSQL shared a
smaller instance; moving the VM to `n2-highmem-8` (8 vCPU / 64 GB) resolved it and
the cutover then ran clean. This is an operator-confirmed result; the formal Section
8 evidence bundle (compose state, scenario logs, correlated telemetry) has not been
re-collected against a pinned revision, so it is recorded as proven-in-practice, not
as a formally gated evidence bundle. The `comfy-lumberjacks-p7` VM is left running;
the superseded original Stage 1 VM (`lumberjacks-stage1`, `e2-medium`) was stopped
on 2026-07-12 to stop its idle spend.

Current endpoints and entry points:

- Valheim direct join: `8.231.129.249:2456`
- Lumberjacks gateway health/control: `http://8.231.129.249:4000`
- fieldlab target setup: `C:\work\comfy\fieldlab\scripts\set-gcp-p7-target.ps1`
- fieldlab P7 runner: `C:\work\comfy\fieldlab\scripts\run-loopback-window.ps1`
- VM service wrapper: `comfy-lumberjacks-p7.service`

Current Compose services:

- `postgres`
- `gateway`
- `eventlog`
- `progression`
- `operatorapi`
- `valheim-server`

P7/I7 passed in one live window: Lumberjacks-decided handshake accept, ownership pin
with negative control, redirect receipts with zero loss/duplicates, rendered
injection with the Lumberjacks owner, clean client stability scan, and save-integrity
pass. The server was disarmed back to observe-only baseline after the gate.

## 1. Objective

Move the existing intranet-hosted Lumberjacks stack to Google Cloud with minimal
architectural change while building practical cloud engineering experience.

The first experiment answers one question:

> Can the proven Lumberjacks stack operate correctly on a remote Linux machine over
> the public internet?

The repository now contains the Stage 1 Terraform, Compose override, VM bootstrap,
service recovery unit, application telemetry, dashboard, uptime check, and alerting
configuration. That makes the experiment executable; it does **not** make Gate 1
complete. Gate 1 is complete only after the external gameplay, UDP, persistence,
reboot, telemetry-correlation, and alert-delivery evidence listed in Section 5 has
been captured from one pinned revision. The
[Stage 1 runbook](google-cloud-stage1-runbook.md) is the completion plan and evidence
checklist.

Database hosting, secrets delivery, image distribution, DNS, TLS, and deployment
automation are separate experiments. Centralized telemetry is the exception: the
trial environment establishes production-grade logging, metrics, and tracing before
the first application deployment so every parity run is diagnosable.

## 2. What has already been proven locally

The repository already provides the architecture to migrate:

- `Game.Gateway` owns the authoritative simulation, including the 20 Hz tick loop,
  world state, player sessions, resume tokens, WebSocket transport, and UDP fast path.
- `Game.EventLog` durably ingests gameplay and operational events.
- `Game.Progression` evaluates player and guild progression from durable events and
  versioned contracts.
- `Game.OperatorApi` exposes operational and steward-facing surfaces.
- PostgreSQL runs with the services through Docker Compose.
- One gateway process listens on TCP 4000 and UDP 4005. Datagram traffic can fall
  back to WebSocket, but external UDP remains a required test case.
- All application services share the existing PostgreSQL schema.

The gateway is a stateful singleton. Increasing its replica count would create
independent simulations and nondeterministic session recovery. The migration must
therefore preserve one authoritative gateway until sharding, fencing, and session
routing have been designed and tested.

## 3. Migration principles

1. Change one operational variable at a time and require evidence before proceeding.
2. Preserve the existing containers, service boundaries, protocols, database engine,
   and Compose topology for the first external proof.
3. Use one Ubuntu Compute Engine VM as the initial cloud host.
4. Keep PostgreSQL in Compose on a persistent disk during the first proof. Moving it
   to Cloud SQL is a later, independently reversible experiment.
5. Externalize configuration and secrets before declaring the deployment operational.
6. Use GitHub Actions as the primary CI/CD orchestrator. Do not introduce Cloud Build
   initially; it can later build images inside GCP if that creates a measured benefit.
7. Add managed services only after they solve a demonstrated reliability, security,
   scale, or operator need.
8. Promote immutable application versions and retain a documented rollback path at
   every gate.
9. Treat observability as foundational infrastructure. Collect correlated structured
   logs, distributed traces, host/runtime/gameplay metrics, uptime results, and alerts
   from the first external run.

Cloud Run is not a target for the current gateway. WebSocket requests have bounded
duration, session affinity is best-effort, and Cloud Run service ingress does not
provide the gateway's inbound UDP listener. See Google's
[WebSocket guidance](https://docs.cloud.google.com/run/docs/triggering/websockets),
[request timeout limits](https://docs.cloud.google.com/run/docs/configuring/request-timeout),
and [container ingress contract](https://docs.cloud.google.com/run/docs/container-contract).

## 4. Local-to-cloud parity model

The first comparison must be intentionally narrow:

```text
Known local stack                 First cloud proof
-----------------                 -----------------
Docker Compose                    Docker Compose
Gateway                           Gateway
EventLog                          EventLog
Progression                       Progression
Operator API                      Operator API
PostgreSQL container              PostgreSQL container
Local persistent volume           Compute Engine persistent disk
```

The first cloud topology is:

```text
Godot client / test clients
          |
          | ws://<reserved-public-ip>:4000
          | udp://<reserved-public-ip>:4005
          v
+--------------------------------------------------+
| Ubuntu Compute Engine VM                         |
|                                                  |
| Docker Compose                                   |
| +---------+ +----------+ +-------------+         |
| | Gateway | | EventLog | | Progression |         |
| +---------+ +----------+ +-------------+         |
| +--------------+ +---------------------------+  |
| | Operator API | | PostgreSQL                |  |
| +--------------+ +-----------------+---------+  |
|                                     |            |
+--------------------------------------+-----------+
                                       |
                              persistent disk
```

For the first proof, firewall rules expose only the ports required by the current
test. Internal service and database ports remain private. The Ops Agent centrally
ingests structured container logs and host metrics and receives application OTLP
metrics and traces before gameplay testing begins.

The same versioned scenario definition must run against both environments and produce
comparable behavioral evidence:

```yaml
scenario: tree-density-traversal.v1
environment: local | gcp
server_version: <version>
mod_version: <version>
world_hash: <hash>
route_hash: <hash>
expected_events: <manifest>
expected_artifacts: <manifest>
transport_mode: websocket | udp
result: pass | fail
```

Health endpoints alone do not prove equivalence. The comparison must include movement,
gameplay-event transport, timing telemetry, persistence, and recovered artifacts.

The architecture evolves without changing application boundaries:

```text
Gate 1             Gate 2              Gate 3                 Gate 4
public IP          DNS + TLS            reproducible delivery  managed persistence
   |                  |                         |                       |
   v                  v                         v                       v
Compute Engine <- Caddy              GitHub Actions             Cloud SQL
Docker Compose                         |                           ^
  application services                 v                           |
  PostgreSQL                       Artifact Registry -> Compute Engine
       |                                                   Docker Compose
persistent disk                                          application services
```

At Gate 4 the PostgreSQL container is removed only after Cloud SQL migration,
verification, backup, restore, and rollback tests pass.

## 5. Stage 1: same stack on Compute Engine

The executable provisioning, deployment, validation, and teardown procedure is in the
[Google Cloud Stage 1 runbook](google-cloud-stage1-runbook.md).

### Scope

- Create a dedicated GCP experiment project using available trial credits.
- Link billing, set a conservative budget and alerts, and label resources.
- Provision one Ubuntu LTS Compute Engine VM with a reserved external IP, dedicated
  service account, OS Login, and a persistent disk.
- Install Docker Engine and the Docker Compose plugin.
- Clone a pinned GitHub revision using a read-only credential.
- Start the existing stack, including PostgreSQL, through Docker Compose.
- Configure restart policies and a small `systemd` unit so the stack returns after a
  VM reboot.
- Create narrowly scoped VPC firewall rules for SSH administration, WebSocket testing,
  and UDP 4005. Do not expose PostgreSQL or internal HTTP services.
- Retain enough host and container logs to diagnose the experiment.
- Install the Ops Agent with OTLP ingestion for metrics and traces, structured Docker
  log parsing, a unified operations dashboard, public uptime checks, and actionable
  alerts for availability, agent silence, application errors, CPU, memory, and disk.

The initial WebSocket endpoint may use the reserved public IP. This is a test endpoint,
not the production address. Validate in sequence:

```text
public-IP reachability
  -> WebSocket gameplay
  -> UDP gameplay
  -> persistence across container and VM restart
```

Do not add DNS, TLS, Artifact Registry, Secret Manager, Cloud SQL, or automated
deployment merely to satisfy this gate. Observability is deliberately present from
the outset and does not change application service boundaries.

### Exit evidence

- The complete stack starts from the pinned revision with Docker Compose.
- A native Godot client connects from outside the VPC.
- A two-player session completes the named gameplay scenario.
- Binary WebSocket traffic works.
- UDP 4005 carries gameplay traffic rather than silently falling back to WebSocket.
- PostgreSQL data survives container and VM restart.
- The existing automated movement scenario completes.
- Logs, scenario results, timing telemetry, and artifacts are recovered from the run.
- Structured logs correlate to Cloud Trace spans, custom gameplay metrics reach Cloud
  Monitoring, the dashboard is populated, and alert delivery is verified.

## 6. Stage 2: public DNS, TLS, and secrets

### Scope

- Point a production-like hostname at the reserved VM address.
- Add Caddy as the public routing boundary. Configure it to obtain and renew a
  certificate, redirect HTTP to HTTPS, and proxy WebSocket upgrades.
- Change the client endpoint to `wss://game.<domain>` while retaining UDP 4005 at the
  same hostname.
- Move credentials and environment-specific configuration out of source and Compose.
- Store secrets in Secret Manager and render a root-readable runtime environment file
  during deployment or startup. Use the VM service account; do not download service
  account keys.
- Restrict Operator API access to an SSH tunnel or an approved operator source range
  until application authentication and authorization exist.

### Exit evidence

- Static IP and production-like DNS resolve externally.
- HTTPS and WSS present a valid certificate and survive a Caddy reload.
- WebSocket and UDP traffic reach the same authoritative gateway.
- No production credential or environment-specific value is stored in source,
  container images, or the committed Compose configuration.

## 7. Stage 3: reproducible build and deployment

### Scope

- Create a regional Artifact Registry repository.
- Use GitHub Actions as the single workflow orchestrator for build, test, release
  approval, image publication, deployment, and post-deployment tests.
- Authenticate GitHub to Google Cloud with Workload Identity Federation rather than a
  stored service-account key.
- Tag images with the Git commit SHA and release identifier, record their immutable
  digests, and deploy by digest rather than `latest`.
- Automate the VM deployment: pull the approved digest set, run
  `docker compose up -d`, wait for health checks, execute the scenario suite, and
  restore the previous digest set if validation fails.
- Serialize deployments so releases cannot overlap.
- Add baseline Cloud Logging and Cloud Monitoring signals for VM saturation,
  container restarts, gateway availability, and failed external checks.

Cloud Build is deferred. If image construction inside GCP becomes an explicit learning
or security objective, retain GitHub Actions for repository validation and release
approval and give Cloud Build only the bounded responsibility of constructing and
publishing images.

### Exit evidence

- An approved commit automatically builds, tests, publishes, and deploys.
- Versioned images and their digests are retained in Artifact Registry.
- Post-deployment behavioral tests pass against the deployed version.
- A failed validation restores the previous digest set through a rehearsed procedure.
- Logs and basic health signals are available to an operator.

## 8. Stage 4: managed PostgreSQL

Cloud SQL is introduced only after external gameplay and the operational deployment
are stable and the application has demonstrated meaningful persistence requirements.
This makes database hosting its own controlled experiment.

### Scope

- Record the PostgreSQL version, extensions, database size, write rate, connection
  count, retention need, recovery-point objective, and recovery-time objective.
- Provision Cloud SQL for PostgreSQL in the VM's region with private IP, automated
  backups, point-in-time recovery where required, and deletion protection.
- Supply the new connection through the existing .NET
  `ConnectionStrings__GameDb` environment variable.
- Rehearse migration using `pg_dump`/`pg_restore` for a small database and accepted
  maintenance window. Evaluate Database Migration Service only when measured size or
  downtime requirements justify continuous replication. See Google's
  [PostgreSQL migration workflow](https://docs.cloud.google.com/database-migration/docs/postgres/quickstart).
- Compare schema objects, row counts, sequence values, key aggregates, and
  application-level reads.
- Complete an automated backup and restore it into an isolated non-production
  instance; record duration and validation results.
- Rehearse both application rollback and database rollback. Never allow the Compose
  database and Cloud SQL to accept production writes simultaneously.

Private IP keeps database traffic off public addressing; see
[Cloud SQL private IP](https://docs.cloud.google.com/sql/docs/postgres/private-ip).

### Exit evidence

- PostgreSQL has moved to Cloud SQL and all dependent services connect privately.
- Data migration and behavioral parity are verified.
- Automated backup succeeds and an isolated restore is tested.
- Failure and rollback procedures are proven within the agreed recovery objectives.

## 9. Stage 5: monitoring, recovery, and operator handoff

### Scope

- Install and configure the Ops Agent where needed and route structured application
  logs without secrets, tokens, or connection strings.
- Dashboard gateway availability, active sessions, simulation tick duration, UDP
  fallback ratio, HTTP failures, container restarts, VM saturation, disk capacity,
  and Cloud SQL connections, latency, storage, and backup status.
- Alert only on actionable, sustained player or recovery impact. Every alert has an
  owner and a linked runbook.
- Define log retention, exclusions, and metric-cardinality limits.
- Rehearse deployment, diagnosis, rollback, database restore, and VM reconstruction.
- Have a second operator execute the runbooks without help from the author.

### Exit evidence

- A second operator can deploy the system.
- A second operator can execute and interpret a named scenario.
- A second operator can diagnose a representative failure.
- A second operator can restore the system within the agreed objective.

## 10. Services by stage

| Service | Architectural purpose | Introduced |
|---|---|---|
| Gateway | Authoritative simulation, player sessions, ranked transport, WebSocket fallback, and UDP gameplay delivery. | Stage 1 |
| EventLog | Durable ingestion of gameplay, proof, progression, and operational events for audit, replay, and projection. | Stage 1 |
| Progression | Evaluates player and guild progression from durable events and versioned community contracts. | Stage 1 |
| Operator API | Exposes health, status, configuration, projection, and steward-facing operational surfaces. | Stage 1 |
| Caddy | Terminates TLS and routes public HTTP/WebSocket traffic while internal services remain private. | Stage 2 |
| PostgreSQL container | Preserves the known database topology and persistence behavior for the first external proof. | Stage 1 |
| Secret Manager | Stores runtime secrets without placing credentials in source or images. | Stage 2 |
| Artifact Registry | Stores versioned, immutable container images used by automated deployment. | Stage 3 |
| GitHub Actions | Orchestrates continuous integration, release approval, deployment, and verification. | Stage 3 |
| Cloud Logging / Monitoring / Trace | Centralizes correlated logs, host/runtime/gameplay metrics, traces, dashboards, uptime checks, and actionable alerts. | Stage 1-5 |
| Cloud SQL | Provides managed PostgreSQL, automated backups, and a testable recovery path. | Stage 4 |

## 11. Deferred services

| Service or change | Adoption condition |
|---|---|
| Cloud Build | Use only if GCP-native image construction has a clear learning, security, or operational benefit. |
| Kubernetes (GKE) | Adopt when orchestration, independent scaling, or managed mixed TCP/UDP load balancing is measured as necessary. |
| Service mesh | Adopt only when service identity, advanced routing, or distributed policy cannot be managed simply. |
| Pub/Sub redesign | Revisit when asynchronous event volume or service decoupling requires a broker. |
| Redis / Memorystore | Revisit when caching, distributed sessions, coordination, or shared state is demonstrated by measurements. |
| Additional VMs | Add for a measured capacity, availability, isolation, or maintenance requirement. |
| Microservice decomposition | Change current boundaries only when independent scale, release, ownership, or availability needs justify the cost. |

GKE must not replicate the authoritative gateway until a stable shard key, exactly-one
authority with fencing, deterministic session routing, durable recovery, graceful
draining, and shard reassignment have been proven. GKE supports mixed-protocol load
balancing, but that capability alone does not make multiple gateway replicas safe;
see Google's [mixed-protocol load balancer guidance](https://docs.cloud.google.com/kubernetes-engine/docs/how-to/mixed-protocol-lb).

## 12. Gated success criteria

| Gate | Required evidence | Decision unlocked |
|---|---|---|
| 1 — Cloud equivalence | Compose startup; external native Godot connection; two-player named scenario; WebSocket and UDP 4005; persistence across restart; recovered artifacts and telemetry. | Continue beyond the basic VM experiment. |
| 2 — Operational deployment | Static IP and DNS; valid TLS; externalized secrets; immutable images; automated deployment; logs and basic health signals. | Treat the cloud environment as reproducibly deployable. |
| 3 — Managed persistence | Cloud SQL migration verified; private connectivity; successful backup and restore test; failure and rollback procedures proven. | Retire PostgreSQL from the VM after the stabilization window. |
| 4 — Reproducible handoff | A second operator can deploy, run a scenario, diagnose failure, and restore the system using the repository runbooks. | Declare the migration maintainable and ready for stewardship. |

Across every gate, the same versioned scenario definition must be executable against
the intranet and Google Cloud environments, producing comparable movement,
gameplay-event transport, timing telemetry, persistence, and artifact outputs.

## 13. Rollback and cost controls

### Rollback

- Record the exact deployed Git revision, Compose configuration, image digest set,
  database location, configuration versions, and named rollback owner.
- Before Cloud SQL, rollback means restoring the prior application digests while
  preserving the PostgreSQL disk and its backup.
- During the Cloud SQL move, stop writers before the final copy and prevent both
  databases from accepting writes. If Cloud SQL has accepted writes, do not simply
  change the connection string back; reconcile the write set and obtain data-owner
  approval for the recovery point.
- Keep the intranet deployment and final source backup available and read-only through
  the agreed stabilization window.
- Rehearse rollback before each gate is approved.

### Cost controls

- Use a dedicated experiment project, budget alerts, resource labels, and least-
  privilege limits on who may create or resize billable resources.
- Begin with one appropriately sized VM and persistent disk. Do not provision GKE,
  Cloud SQL HA, extra VMs, load balancers, or high-volume log retention without a
  measured requirement.
- Co-locate regional resources when they are added and monitor network egress, log
  ingestion, disk growth, Artifact Registry storage, and backup retention.
- Apply Artifact Registry cleanup policies in dry-run mode before enforcement.
- Make no production cost claim without a region-specific estimate and measured
  usage from the parity run.

## Explicit non-goals

- No gateway autoscaling before authority sharding and session routing exist.
- No gameplay protocol rewrite during cloud migration.
- No database-hosting change in the first external proof.
- No public Operator API without authentication and authorization.
- No intranet teardown as part of cutover.
- No managed-service adoption solely because the service is available.
