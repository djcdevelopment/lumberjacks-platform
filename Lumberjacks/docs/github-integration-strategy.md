# GitHub Integration Strategy

Status: proposed

Last reviewed: 2026-07-11

## 0. Purpose and scope

This is an implementation proposal for how GitHub, GitHub Actions, and Google Cloud
divide responsibility for building, validating, and deploying Lumberjacks. It is
written for engineers who will build the `.github/workflows/` pipeline, not as a
survey of GitHub features.

It assumes the state already on disk:

- Services: `Game.Gateway`, `Game.EventLog`, `Game.Progression`, `Game.OperatorApi`,
  `Game.Simulation`, `Game.Persistence`, `Game.Contracts`, `Game.ServiceDefaults`
  (`src/`), plus `@game/admin-web` (`clients/`).
- Local stack: `infra/docker/docker-compose.yml` (+ `docker-compose.dev.yml`,
  `docker-compose.gcp-stage1.yml`), Postgres via `init.sql`.
- Cloud target: `infra/gcp/stage1` (Terraform, one Compute Engine VM, Compose,
  Ops Agent observability) per [google-cloud-migration-strategy.md](google-cloud-migration-strategy.md)
  and [google-cloud-stage1-runbook.md](google-cloud-stage1-runbook.md).
- Test tiers already established in [Tests.md](Tests.md): dotnet unit/integration
  suites, Node.js E2E smoke scripts (`scripts/test-*.js`), load tests
  (`scripts/load-test-dual-channel.js`).
- A promotion-gate habit already in use for physics labs
  ([research-to-lab-method.md](labs/research-to-lab-method.md): Researched → Simulated
  → Visualized → Validated → Projected → Serialized → Transported → Integrated) and
  ADR 0019, which is the closest existing precedent for "evidence gates a promotion,
  a human accepts the gate."

There is no `.github/` directory yet. This document defines what goes into it and
why, so it can be implemented directly rather than re-derived.

---

## 1. Overall GitHub Strategy

GitHub's job is **bookkeeping, orchestration, and record-keeping for a system whose
truth lives in evidence artifacts, not in GitHub itself.** GitHub Actions never
becomes a second source of truth about whether a scenario passed — it runs the
scenario, captures what came out, and stores a pointer to it. Google Cloud's job is
**runtime**: it hosts the processes, the database, the registry, and the network
path real players use. Nothing in Google Cloud should require a human to have opened
a GitHub Actions log to understand what's running there — the deployed artifact's
digest and the evidence bundle that validated it are always resolvable from the Git
history.

| Concern | Home | Why |
|---|---|---|
| Source of truth for code | GitHub (Git) | Standard; no dispute. |
| Build, test, scenario execution | GitHub Actions | Ephemeral, reproducible compute; ties directly to a commit SHA. |
| Human review of judgment calls | Pull Requests | The place a diff, its evidence, and a reviewer's sign-off co-locate. |
| Bug reports, work tracking | Issues | Cheap, linkable from commits/PRs, closes on merge. |
| Design debate, open questions | Discussions | Explicitly *not* actionable until it becomes an Issue or ADR — keeps Issues a clean backlog. |
| Immutable release record | Releases (tags) | One release = one set of image digests + one evidence bundle, referenced by SHA. |
| Container images | Google Artifact Registry, referenced by digest | GitHub Container Registry is acceptable for public dev images, but Artifact Registry is authoritative because Compute Engine pulls from it directly with Workload Identity, with no PAT/token in the runtime path. |
| Evidence (logs, JSON results, telemetry, screenshots) | GCS bucket (or repo-adjacent artifact store for small runs), indexed from the run | GitHub Actions artifact retention is short (90 days default) and not queryable; evidence must outlive the workflow run and the PR. GitHub stores the *pointer* (a manifest committed to the evidence index or attached to the Release), not the payload. |
| Documentation | `docs/` in-repo (this file, ADRs, runbooks) | Docs are versioned with the code they describe; a stale doc is a diff away from being caught in review. |
| Deployment orchestration | GitHub Actions triggers Terraform + `gcloud`/`ssh` against the target VM | GitHub decides *when* and *what digest*; it does not become a scheduler or a config-management system. Terraform state and the running Compose stack remain GCP's problem. |

**What does NOT belong in GitHub:** runtime configuration for staging/production
(lives in Terraform + `infra/gcp/stage1/terraform.tfvars`, not workflow YAML env
blocks), long-lived secrets beyond what's needed to mint short-lived cloud tokens
(Workload Identity Federation replaces long-lived GCP JSON keys — see §7 and §13),
and any process that runs continuously in a deployed environment (dashboards, alert
routing, ops-agent config are GCP-native, per `infra/gcp/stage1/observability.tf`).

---

## 2. Repository Structure

The existing layout already separates most of these concerns; this section names
what's implicit and adds the two directories that don't exist yet (`evidence/`,
`.github/`).

```
Game.sln                        # solution root — build entry point for dotnet
src/                             # application code (Gateway, EventLog, Progression, OperatorApi, Simulation, Persistence, Contracts, ServiceDefaults)
clients/                         # @game/admin-web and future Godot/web clients
tests/                           # Game.Contracts.Tests, Game.Simulation.Tests, etc. — unit/integration, per Tests.md
scenarios/                       # NEW — Portable Scenario definitions (see §4). One folder per scenario, manifest + fixtures.
scripts/                         # operator scripts: test-*.js (promoted incrementally into scenarios/, see §4), load-test-dual-channel.js, start-*.sh/ps1
infra/
  docker/                        # docker-compose.yml, docker-compose.dev.yml, docker-compose.gcp-stage1.yml, init.sql — the portable runtime definition
  gcp/stage1/                    # Terraform: Compute Engine, networking, observability, outputs
evidence/                        # NEW — checked-in evidence INDEX only (small JSON manifests + digests), not raw payloads (see §5)
docs/
  adrs/                          # architectural decisions, numbered, append-only
  network/, labs/                # existing subsystem and research docs
  *.md                           # strategy/runbook docs, this file among them
.github/
  workflows/                     # NEW — CI/CD pipeline (see §3)
  actions/                       # NEW — composite actions once ≥2 workflows share a step sequence (see §14)
package.json, Game.sln, Directory.Build.props, Directory.Packages.props  # root build config
Dockerfile                       # NEW location convention: one per service under src/<Service>/Dockerfile once multi-image builds land (see §6); root Dockerfile stays as the current single-image build until then
```

**Why `scenarios/` is new and separate from `scripts/` and `tests/`:** `tests/` is
xUnit — fast, hermetic, no live process. `scripts/test-*.js` today mixes two things:
scenario logic (spin up a session, place a structure, assert progression fired) and
ad hoc CLI plumbing (parse `process.argv`, print to stdout). Splitting these lets the
scenario logic run unmodified against `localhost`, a GitHub Actions runner, or a
staging VM, while the CLI plumbing becomes a thin operator-grammar wrapper (§4).
Existing `scripts/test-*.js` files migrate into `scenarios/<name>/scenario.mjs`
incrementally — this is not a rewrite, it's extracting the part that already
knows how to talk to a live gateway.

**Why `evidence/` is checked in but stays an index:** committing raw evidence
payloads (JSON results, telemetry dumps) bloats the repo and makes evidence mutable
by accident (a rebase can silently drop or reorder it). Committing a small JSON
manifest — scenario name, commit SHA, run ID, evidence bucket path, content hash —
keeps evidence *discoverable from Git* without making Git the evidence store. See §5.

---

## 3. CI Pipeline

One workflow, `.github/workflows/ci.yml`, triggered on `pull_request` and `push` to
`master`. Stages run as separate jobs so failures are attributable and so later
stages can be skipped without re-running earlier ones (GitHub Actions caches nothing
across jobs unless you cache it explicitly — treat every job boundary as an artifact
handoff, not a shared filesystem).

### Stage: Commit
- **Input:** a pushed commit / PR head SHA.
- **Output:** the trigger event; no artifact.
- **Failure condition:** N/A — this is the workflow trigger, not a job.
- **Notes:** commits should be signed (§13). Unsigned commits don't block CI in v1
  but are flagged in the PR check (branch protection can promote this to required
  later — see §14, "scale only when measurements justify it").

### Stage: Build
- **Input:** checked-out source at the trigger SHA.
- **Output:** `dotnet build Game.sln` output (not published — this stage exists to
  fail fast on compile errors before spending time on anything else); `npm ci` +
  `npm run build:dotnet` equivalent for the TypeScript admin-web workspace.
- **Failure condition:** any project fails to compile/build.
- **Artifacts produced:** none persisted — this stage is a gate, not a producer.
  Build output is regenerated in the Container build stage from the same SHA.

### Stage: Static analysis
- **Input:** source at trigger SHA.
- **Output:** `dotnet format --verify-no-changes` (or equivalent analyzer run),
  `npm run lint`, `npm run format:check` (both already exist as package.json
  scripts — this stage just runs what's already defined), CodeQL (see §13).
- **Failure condition:** lint/format diff detected, analyzer errors, CodeQL alert
  at or above the configured severity.
- **Artifacts produced:** CodeQL SARIF upload (native GitHub Security tab
  integration — no separate storage needed).

### Stage: Unit tests
- **Input:** source at trigger SHA.
- **Output:** `dotnet test Game.sln --filter Category!=Performance` (matches
  Tests.md exactly — Performance-tagged tests are excluded from the PR-blocking
  path and run on a schedule instead, see §14), vitest for TypeScript packages.
- **Failure condition:** any test fails; coverage regression is tracked but does
  not block in v1 (no baseline exists yet to regress against).
- **Artifacts produced:** `.trx`/JUnit test result files, uploaded as workflow
  artifacts and summarized in the job summary (`$GITHUB_STEP_SUMMARY`).

### Stage: Portable Scenario execution
- **Input:** compiled services from Build, a scenario set (§4) selected by PR
  labels or changed-file paths (a PR touching only `clients/admin-web/` does not
  need the full gameplay scenario suite; a PR touching `src/Game.Simulation`
  triggers all gameplay scenarios).
- **Output:** pass/fail per scenario, plus the scenario's evidence bundle.
- **Failure condition:** any required scenario fails; a scenario marked
  `advisory: true` in its manifest (new/unstable scenarios, see §10) reports but
  does not block.
- **Artifacts produced:** evidence bundle (§5) per scenario, uploaded and indexed.

### Stage: Evidence capture
- **Input:** raw output from the scenario run (server logs, gateway telemetry,
  entity_update timing, admin-web screenshots if applicable).
- **Output:** a structured evidence bundle: `manifest.json` + payload files, content
  hashed.
- **Failure condition:** evidence capture itself does not fail the build on missing
  optional evidence (e.g., no screenshot for a headless-only scenario), but a
  scenario is not considered to have run if its *required* evidence types (defined
  per scenario) are missing.
- **Artifacts produced:** uploaded to the evidence store (GCS bucket in v1, see §5),
  with the manifest also attached as a GitHub Actions artifact and summarized in the
  job summary for reviewer visibility without leaving the PR.

### Stage: Artifact creation
- **Input:** Build outputs (dotnet publish, npm build).
- **Output:** publish-ready binaries/static assets, an SBOM (CycloneDX or SPDX, see
  §13) generated against the exact dependency lockfiles used in Build.
- **Failure condition:** publish fails, or SBOM generation fails (missing lockfile,
  unresolved dependency).
- **Artifacts produced:** publish output (input to Container build), SBOM file.

### Stage: Container build
- **Input:** publish output, `Dockerfile`(s).
- **Output:** one image per deployable service (Gateway, EventLog, Progression,
  OperatorApi — matching `infra/docker/docker-compose.yml`'s service list; see §6
  for the migration from today's single root `Dockerfile`).
- **Failure condition:** any image build fails.
- **Artifacts produced:** locally tagged images (not yet pushed), each with the SBOM
  from the prior stage attached as image metadata.

### Stage: Container signing
- **Input:** built images.
- **Output:** cosign signatures (keyless, OIDC-backed — see §13) over each image
  digest.
- **Failure condition:** signing fails (OIDC token issuance failure, cosign error).
- **Artifacts produced:** signature + attestation, stored alongside the image in
  Artifact Registry (cosign's native model — no separate signature store needed).

### Stage: Publish image
- **Input:** signed images.
- **Output:** images pushed to Google Artifact Registry via Workload Identity
  Federation (§7, §13 — no static GCP key material in the workflow).
- **Failure condition:** push fails, auth fails, registry rejects (quota, policy).
- **Artifacts produced:** the image digest (`sha256:...`) — this digest is the
  single identifier carried forward through every remaining stage. Tags (`:latest`,
  `:pr-123`) are convenience labels only; nothing downstream trusts a tag.

### Stage: Deployment candidate
- **Input:** the published digest + its evidence bundle manifest + its SBOM.
- **Output:** a "deployment candidate" record — a small JSON object
  `{ digest, commit_sha, evidence_manifest_url, sbom_url, scenario_results }` —
  attached to the PR (as a check-run summary) and, on `master`, appended to a
  running candidates log.
- **Failure condition:** any required upstream stage failed (this stage only runs
  if everything before it succeeded — it is a checkpoint, not new validation).
- **Artifacts produced:** the candidate record itself, which is what §9 (PR
  workflow) and §10 (capability promotion) read from.

### Stage: Optional staging deployment
- **Input:** a deployment candidate, gated on branch (`master` only in v1 — PR
  preview environments are a §14 scaling step, not a v1 requirement given there's
  one staging VM today).
- **Output:** the candidate digest deployed to the `infra/gcp/stage1` VM via SSH +
  `docker compose pull && docker compose up -d` against the pinned digest (not a
  moving tag).
- **Failure condition:** deploy script fails, or the post-deployment health check
  (next stage) fails — in which case this stage auto-triggers rollback to the
  previously-deployed digest (recorded from the prior successful run).
- **Artifacts produced:** deployment log, previous-digest record (for rollback).

### Stage: Post-deployment validation
- **Input:** the newly staged deployment.
- **Output:** a subset of scenarios re-run against the live staging URL (exactly
  the same scenario code as Portable Scenario execution — only the target host
  changes, per §4's portability requirement), plus the existing
  `scripts/test-multiplayer.js`-style smoke check against `wss://` staging.
- **Failure condition:** any scenario fails against staging that passed locally in
  CI — this is the signal that something environment-specific broke (network path,
  CORS, DB migration not applied) and triggers the same rollback as above.
- **Artifacts produced:** a second evidence bundle, tagged `environment: staging`,
  linked to the same commit SHA as the CI-run bundle so the two can be diffed.

### Stage: Promotion recommendation
- **Input:** the full chain — deployment candidate, staging evidence, scenario
  pass history for this scenario set.
- **Output:** a recommendation, not a decision: a comment on the PR (or a Release
  draft, for `master`) stating "this candidate meets the evidence bar for
  promotion from `<current gate>` to `<next gate>`" per §10's ladder.
- **Failure condition:** none — this stage cannot fail, because it makes no
  decision. It only surfaces evidence. A human accepts or rejects the
  recommendation.
- **Artifacts produced:** the recommendation comment/draft Release, which is the
  handoff artifact from automation to the human approvers in §9 and §10.

---

## 4. Portable Scenario Execution

A **Portable Scenario** is a directory under `scenarios/<name>/` containing:

```
scenarios/vertical-slice/
  scenario.mjs        # exports prepare/launch/join/observe/cleanup/rollback
  manifest.json        # { required_evidence: [...], advisory: false, timeout_s: 120, targets: ["local","ci","staging"] }
  fixtures/            # any scenario-specific seed data
```

The operator grammar is six verbs, each a function the scenario module exports.
A thin CLI (`scripts/scenario-run.mjs <name> --target <local|ci|staging>`) calls
them in order and is the *only* thing that changes between environments — it
resolves `--target` to a gateway URL (`ws://localhost:4000` locally,
`ws://gateway:4000` inside the CI Compose network, `wss://staging.<domain>` against
GCP) and passes nothing else environment-specific into the scenario itself.

| Verb | Responsibility | Failure handling |
|---|---|---|
| `prepare` | Bring up dependencies the scenario needs but doesn't own (start `docker compose -f infra/docker/docker-compose.yml up -d` locally; on `ci`/`staging` targets, this is a no-op — the stack is already running). Seed any fixture data. | Non-zero exit aborts before `launch`; nothing was joined yet, so no `cleanup` needed. |
| `launch` | Start or confirm the target process is reachable (health-check the gateway's `/health` endpoint per `docker-compose.yml`). | Retries with backoff up to `manifest.json`'s `timeout_s`, then fails the scenario. |
| `join` | Connect scenario actors (bot clients over WebSocket, matching the pattern already in `scripts/test-multiplayer.js`) to the running target. | A failed `join` triggers `cleanup` then `rollback`, then fails. |
| `observe` | Drive the scenario's actual behavior (place a structure, complete a challenge step) and capture evidence as it happens — this is where evidence capture (§5) hooks in, not a separate pass over logs afterward. | Assertion failures are recorded as evidence *and* fail the scenario; `observe` always runs to completion (doesn't short-circuit on first assertion failure) so a single run surfaces every problem, not just the first. |
| `cleanup` | Disconnect actors, tear down anything `prepare` started that shouldn't outlive the run (local only — CI and staging targets don't tear down the shared stack). | Best-effort; a `cleanup` failure is logged but doesn't flip a passing scenario to failing. |
| `rollback` | Only invoked by the pipeline's Post-deployment validation stage (§3), never by a scenario itself. Reverts a staging deployment to the previous digest. Scenario code never calls this directly — it's the pipeline's response to a scenario failing against a *deployed* target. | N/A — this is pipeline-level, included in the grammar because it's the symmetric counterpart to `launch` for a deployed environment. |

**Portability guarantee:** the same `scenario.mjs` runs unmodified on a
contributor's laptop (`--target local`), a GitHub Actions runner (`--target ci`,
gateway reachable via the Compose network Actions spins up), and the staging VM
(`--target staging`, gateway reachable over `wss://`). The only per-environment
variable is the connection URL, resolved outside the scenario module. If a
scenario needs environment-conditional logic beyond that, it's not portable and
the manifest's `targets` array should say so explicitly rather than letting the
scenario silently branch on an env var.

This is a direct generalization of what `scripts/test-vertical-slice.js` and
`scripts/test-multiplayer.js` already do (both already accept an optional gateway
URL argument, per [deployment-strategy.md](deployment-strategy.md)) — §2's
migration path moves their scenario logic into this shape without changing what
they test.

---

## 5. Evidence Pipeline

**Evidence** is anything a scenario or pipeline stage directly observed and
recorded, unmodified, at the time it happened: server logs, JSON scenario results,
gateway telemetry snapshots, admin-web screenshots, `entity_update` timing traces,
`dotnet test` `.trx` output, load test latency histograms.

**Interpretation** is anything derived from evidence by a person or a later process:
"this scenario is stable enough to promote," "this latency regression is caused by
the interest-management change in PR #142," "this is acceptable for a first cloud
proof." Interpretation is written in PR descriptions, promotion recommendations,
ADRs, and retros — never inside the evidence bundle itself.

**Evidence bundle shape** (produced by the Evidence capture stage, §3):

```
manifest.json:
  {
    "scenario": "vertical-slice",
    "commit_sha": "...",
    "run_id": "<github run id>",
    "environment": "ci" | "staging" | "local",
    "started_at": "...", "finished_at": "...",
    "result": "pass" | "fail",
    "evidence": [
      { "type": "log", "path": "gateway.log", "sha256": "..." },
      { "type": "json_result", "path": "scenario-result.json", "sha256": "..." },
      { "type": "telemetry", "path": "entity-update-timing.json", "sha256": "..." },
      { "type": "screenshot", "path": "admin-web-final.png", "sha256": "..." }
    ]
  }
```

- **What is evidence:** raw logs; raw JSON scenario results; raw telemetry/metrics
  exports; state snapshots (DB row counts, entity counts at a point in time);
  screenshots; performance numbers as measured (latency, tick duration); timings;
  trace files. Each is content-hashed at capture time.
- **What is interpretation:** the promotion recommendation text, a reviewer's PR
  comment, a written root-cause, a milestone write-up, this document's own
  section headers.
- **What can change:** interpretation, freely — a human can revisit "this counts
  as passing" as understanding improves. The evidence bundle's `manifest.json`
  can gain new pointers over time (e.g., a later analysis run adds a derived
  chart), but existing entries are never edited in place.
- **What must never change:** an existing evidence file, once its hash is recorded
  in a manifest that has been referenced by a PR, Release, or promotion record. If
  a scenario produced wrong evidence, the fix is to re-run it and record a new
  bundle with a new `run_id` — not to edit the old one. This is enforced
  operationally (evidence buckets use object versioning + a retention lock in GCS,
  not application-level checks) rather than by convention alone, because
  convention alone is exactly the thing "immutable" is meant to survive.

**Storage:** v1 uses a GCS bucket (`gs://lumberjacks-evidence/<scenario>/<run_id>/`)
with versioning and a retention lock, written directly from the GitHub Actions
runner via Workload Identity Federation (§7). GitHub Actions' own artifact storage
is used only as a convenience mirror for the PR's lifetime (90-day retention) — the
GCS copy is authoritative. The `evidence/` directory in-repo (§2) holds only the
`manifest.json` files for evidence tied to a `master` commit or a Release, committed
by the workflow via a bot commit, so `git log` on `evidence/` is itself a durable
index of what evidence exists without needing to query GCS to find out.

---

## 6. Artifact Strategy

| Artifact | Versioning scheme | Notes |
|---|---|---|
| Docker images | Immutable digest (`sha256:...`), tagged additionally with `<service>:<commit-sha>` and, on `master` only, `<service>:latest` moved to point at the newest digest | Tags are for humans browsing the registry; every automated reference (Compose file on the VM, deployment candidate record) uses the digest. One image per service: `gateway`, `eventlog`, `progression`, `operatorapi` — today's single root `Dockerfile` becomes one `src/<Service>/Dockerfile` per service as part of this rollout, matching `docker-compose.yml`'s service boundaries so a change to `Game.Progression` doesn't force rebuilding/redeploying `Game.Gateway`. |
| Build artifacts (dotnet publish output, admin-web static build) | Tied to commit SHA, not independently versioned | Ephemeral — regenerated from source on demand; not a release artifact in their own right, only an input to the container build. |
| Scenario outputs | `<scenario>/<run_id>/manifest.json` (§5) | Never overwritten; superseded by a new `run_id`, not a version bump. |
| Release bundles | Semantic-ish tag `vYYYY.MM.DD-N` (date-based, since gameplay milestones don't map cleanly to semver's meaning) pointing at a fixed set of service digests + the evidence bundle that validated them + the SBOM | A Release is a *pointer object*, not a rebuild — it names digests that were already built, signed, and validated earlier in the pipeline. |
| SBOMs | One per image, generated at Artifact creation (§3), attached to the image in Artifact Registry and linked from the Release | CycloneDX JSON format (tooling-agnostic, widely supported by scanners). |
| Container signatures | cosign, keyless/OIDC, one per image digest | Verified at deploy time (§7, §11) — the deploy script refuses to pull an unsigned or invalidly-signed digest. |

**Why digests, not tags, for deployment:** a tag is a mutable pointer — `:latest`
can mean a different image an hour later, and nothing about a running container
proves which build it actually is. A digest is the image's own content hash: what
Terraform/Compose references, what cosign signs, and what the container actually
runs are the same 64 hex characters, so "what's deployed" and "what was validated"
are the same question by construction, not by discipline. This is the same
principle as evidence immutability (§5) applied to the runtime artifact.

---

## 7. Google Cloud Integration

GitHub's responsibility ends at *producing a signed, evidenced, digest-addressed
image and asking GCP to run it.* Everything after that — infrastructure state,
running processes, secrets at rest, database contents — belongs to GCP and is
managed by Terraform (`infra/gcp/stage1`), not by workflow YAML.

- **Artifact Registry:** authoritative image store (§1, §6). GitHub Actions pushes
  via Workload Identity Federation; the staging VM pulls via its own service
  account, also via Workload Identity, per `infra/gcp/stage1/main.tf`'s existing
  service account setup.
- **Compute Engine:** the current and only deploy target (`infra/gcp/stage1`). The
  deploy step is an SSH command (via IAP tunnel, matching the existing runbook's
  access model) that runs `docker compose -f docker-compose.gcp-stage1.yml pull &&
  docker compose ... up -d`, with the Compose file's image references updated to
  the new digests by the deploy step, not hand-edited.
- **Terraform:** owns everything Compute Engine needs to exist (VM, network,
  firewall, Ops Agent config, budget alert) — GitHub Actions can run
  `terraform plan`/`apply` as a separate, manually-triggered workflow
  (`infra-apply.yml`, distinct from `ci.yml`) gated by required approval, because
  infrastructure changes are a different risk class than application deploys and
  should not share a trigger with every PR merge.
- **Docker Compose:** stays the runtime orchestrator on the VM (per the migration
  strategy's explicit choice not to introduce Cloud Run or a scheduler yet). GitHub
  Actions never runs Compose logic itself beyond `pull`/`up` — Compose files
  themselves are edited in-repo and reviewed like any other change.
- **Secrets:** short-lived only, minted at workflow run time. No standing
  `GCP_SA_KEY` JSON secret in GitHub — Workload Identity Federation issues a token
  scoped to the specific workflow run's OIDC claims (repo, branch, workflow name).
  Anything the *running application* needs (DB connection string, etc.) is a GCP
  Secret Manager reference resolved on the VM at container start, never passed
  through a GitHub Actions log.
- **Identity / Workload Identity Federation:** the trust boundary. A GitHub Actions
  OIDC token, scoped narrowly (this repo, this workflow file, optionally this
  branch), exchanges for a short-lived GCP access token via a configured Workload
  Identity Pool. No credential GitHub holds is valid outside the run that requested
  it.

**Where GitHub's responsibility ends, explicitly:** GitHub does not know the VM's
current health beyond what the Post-deployment validation stage (§3) observed at
deploy time. Ongoing monitoring, alerting, and incident response are GCP-native
(Ops Agent, Cloud Monitoring/Logging, per `infra/gcp/stage1/observability.tf`) —
GitHub Actions is not a monitoring system and should not be polled as one.

---

## 8. Contributor Experience

Target: clone to a passing scenario in under ten minutes, with every step
mirroring what CI does.

```bash
git clone <repo> && cd Lumberjacks
npm ci && dotnet restore Game.sln          # install
npm run dev:docker                          # infra up (same compose file CI uses)
node scripts/scenario-run.mjs vertical-slice --target local   # run one scenario
```

- **Verify success:** `scenario-run.mjs` exits 0 and prints the evidence bundle's
  local path — the contributor can open `scenario-result.json` themselves, the
  same file CI would have uploaded.
- **Recover from failure:** the CLI's failure output names the failing verb
  (`prepare`/`launch`/`join`/`observe`) and points at the captured evidence
  (gateway log tail, last scenario assertion). Because the same code runs in CI,
  a contributor reproducing a red PR check runs the *exact* failing command CI
  ran — the workflow's job summary includes the literal `scenario-run.mjs`
  invocation, not a paraphrase.
- **Submit a PR:** standard GitHub flow; the PR template (added as part of this
  rollout, `.github/PULL_REQUEST_TEMPLATE.md`) asks which scenarios were run
  locally and links this doc's §9 checklist.

No step here diverges from CI's own sequence — `npm run dev:docker` is the same
Compose file `prepare` uses on the `ci` target; `scenario-run.mjs` is the same
entrypoint. A contributor is never debugging a discrepancy between "how I ran it"
and "how CI ran it," because there is only one way to run it.

---

## 9. Pull Request Workflow

**Required checks (block merge):** Build, Static analysis, Unit tests, Portable
Scenario execution (non-advisory scenarios only), Deployment candidate (proves the
full chain up to that point succeeded).

**Not required to block merge, but posted to the PR:** staging deployment result
and post-deployment validation (only run on `master`, not per-PR, given one shared
staging VM — see §14 for when this changes), the Promotion recommendation comment.

**Required approvals:** one human approval for any change under `src/`, `clients/`,
`infra/`, or `scenarios/`. Two for anything under `infra/gcp/` (infrastructure
changes carry cost and blast-radius risk beyond a normal code change). Zero
additional approval for `docs/`-only changes beyond the standard review, matching
this repo's existing "evidence-based, not process-heavy" documentation habit.

**Automatic validation (what CI evaluates):** did it compile, does it pass existing
tests, do required scenarios still pass, is the diff free of new CodeQL findings, is
the SBOM clean of critical CVEs, does the PR's scenario evidence show a performance
regression against the baseline recorded on `master` (flagged, not auto-blocking,
in v1 — no regression budget has been calibrated yet).

**What reviewers evaluate that automation cannot:** whether the *scenario itself*
still represents the right behavior (a scenario passing proves the code does what
the scenario checks, not that the scenario checks the right thing); architectural
fit; whether a new capability should be promoted (§10 — this is never automatic);
whether evidence that looks fine numerically is actually fine experientially (a
screenshot showing correct layout but bad UX passes every automated check).

**Surfaced directly in the PR, not requiring a click-through:** scenario pass/fail
summary (job summary), evidence bundle links (GCS URLs per scenario), artifact
links (image digest once built), performance deltas versus `master`'s last
recorded baseline, and — once introduced per §14 — a staging preview URL when
ephemeral per-PR environments are justified by actual review-cycle friction, not
before.

---

## 10. Capability Promotion

The ladder mirrors the one already proven in `research-to-lab-method.md`, adapted
from "physics model" gates to "shippable capability" gates:

| Gate | Meaning | Evidence required | Who approves |
|---|---|---|---|
| Experimental | Scenario exists, runs, may be flaky (`advisory: true` in its manifest — reports but doesn't block PRs). | At least one recorded pass, any environment. | No approval needed to reach this — this is the default state for any new scenario. |
| Validated | Scenario is required (non-advisory) in CI; passes consistently against `local`/`ci` targets. | N consecutive green runs on `master` (N calibrated per scenario type, not fixed globally — a fast unit-style scenario needs more reps than an expensive load scenario). | Automatic — the pipeline can flip `advisory: false` itself once the run-history threshold is met, because this is a repeatability claim, not a judgment claim. |
| Repeatable | Scenario passes against `staging`, not just `ci` — proves it survives the real network path, real Postgres, real Compose topology. | Post-deployment validation (§3) green across multiple independent deploys. | Automatic promotion recommendation; a human (the on-call reviewer for that area) accepts it — because "the network path is fine" still deserves a look before it's load-bearing. |
| Marked Proven | A human has evaluated the *experience*, not just the numbers — played it, watched it, judged it against the product intent. | The evidence bundle from a Repeatable-gate run, plus a written interpretation (PR comment, ADR, or milestone note) from the approving human. | Human only. This is the gate this document's philosophy exists to protect — automation cannot certify "this feels right." |
| Stable | Sustained Marked-Proven status across a defined window (a release cycle, a set number of milestone runs) with no regressions and community/player validation where applicable (e.g., a friend-test session, per `deployment-strategy.md`'s existing "distribute test .exe to friends" practice). | Accumulated evidence bundles across the window + at least one external validation session. | Human, and for anything touching shared game systems, more than one — matching the two-approver rule for `infra/gcp/` in §9, because a Stable promotion is effectively a "this is now load-bearing" claim. |

**Automatic:** Experimental → Validated. This is pure repeatability, exactly the
kind of claim automation is positioned to make honestly.

**Human-approved:** Validated → Repeatable is automation-recommended,
human-accepted (low friction — the human is confirming a network-path claim, not
making a subjective call). Repeatable → Marked Proven → Stable require a human
judgment each time; nothing in this pipeline should ever auto-promote across these
because that's precisely the "interpretation" the philosophy separates from
"evidence."

**Community-validated:** the Stable gate's external validation session is the one
place a non-maintainer's observation is required evidence, not just welcome
feedback — matching the existing friend-testing practice this repo already treats
as meaningful validation, not a nice-to-have.

---

## 11. Deployment Philosophy

| Environment | What runs there | How it's reached |
|---|---|---|
| Local | `docker compose -f infra/docker/docker-compose.yml up` | Contributor's machine, `--target local`. |
| Developer sandbox | Same Compose file, contributor's own throwaway state | Not currently a separate cloud environment — today "sandbox" *is* local, per the existing repo. A cloud-hosted per-developer sandbox is a §14 scaling step, introduced only if local Compose stops being sufficient for some class of bug (e.g., something that only reproduces under real network latency). |
| Staging | `infra/gcp/stage1` Compute Engine VM, Compose with pinned digests | `master` merges only, via the deploy step in §3. |
| Production | Not yet provisioned | Out of scope for this document until staging has sustained Repeatable-gate evidence over real usage — provisioning production ahead of that evidence would violate "scale only when measurements justify it." |

**Rollback:** the deploy step (§3) always records the digest it's replacing before
pulling the new one. A failed post-deployment validation triggers an automatic
`docker compose up -d` back to the recorded previous digest — this is the
`rollback` verb from §4's grammar, invoked by the pipeline, never by a scenario.
Manual rollback (a human deciding a *validated* deploy should still be reverted,
e.g., after a Marked-Proven judgment call goes wrong post-release) is a one-command
re-run of the same rollback step against any prior recorded digest, triggerable via
`workflow_dispatch`.

**Blue/green, canary, feature flags:** not implemented in v1 — a single Compute
Engine VM has no capacity for parallel environments, and introducing them now would
be building for a scale this repo doesn't have evidence it needs yet. When staging
graduates to a second VM or a managed instance group (§14), blue/green becomes the
natural next deployment model (swap the load balancer target, not the container) —
this is worth designing when it's needed, not now. Feature flags are an application
concern (a config service or LaunchDarkly-style toggle), independent of this
pipeline; nothing here blocks adding one, but this document doesn't mandate one
that isn't needed yet.

**Disaster recovery:** Postgres runs on a persistent disk per the migration
strategy — disk snapshots (Terraform-managed, scheduled) are the recovery
mechanism for data; the application layer is fully recoverable by redeploying any
previously-signed digest, since nothing about the running containers is stateful
outside Postgres.

**Determinism:** every deployment step operates on a digest, never a tag or a
branch name (§6). Given the same digest, the same Terraform state, and the same
Compose file, a deploy is reproducible by construction — "what's running" is
always answerable by reading the VM's Compose file, not by asking what a workflow
run happened to build that day.

---

## 12. Observability

Collected automatically, without instrumentation living in application code beyond
what already exists (structured logging, the tick loop's own timing):

| Signal | Source | Where it lands |
|---|---|---|
| Metrics (tick duration, entity counts, connection counts) | Existing service telemetry (Gateway tick loop) | Ops Agent → Cloud Monitoring, per `infra/gcp/stage1/observability.tf`. |
| Logs | Container stdout/stderr | Ops Agent → Cloud Logging (structured, correlated by trace ID where present). |
| Traces | Any distributed calls across Gateway/EventLog/Progression | Cloud Trace, if/when cross-service tracing is added — not yet instrumented; this row names the destination, not a commitment to add tracing now. |
| Health checks | Gateway `/health` (already used by the deploy step's `launch` verb, §4) | Polled by Post-deployment validation; long-running health is Cloud Monitoring uptime checks, per the existing `infra/gcp/stage1` setup. |
| Scenario duration | Evidence bundle `started_at`/`finished_at` (§5) | Evidence index; trended in the Promotion recommendation stage by comparing against prior runs' manifests. |
| Performance history | Load test / scenario evidence bundles over time | Evidence store (GCS) — queryable by scenario name and date prefix; no separate time-series system introduced until the evidence volume actually needs one (§14). |
| Failure history | CI job history (native GitHub) + evidence bundles marked `result: fail` | GitHub's own Actions history is sufficient in v1; this becomes a dashboard only once volume makes scrolling Actions history impractical. |
| Deployment history | The candidates log (§3's Deployment candidate stage) + Release tags | Git history is the deployment history — every deploy corresponds to a Release or a candidate record, both versioned. |

The consistent pattern: this pipeline emits structured records (evidence manifests,
candidate records) as a side effect of doing its job, and observability is built by
querying those records, not by adding a parallel instrumentation layer.

---

## 13. Security

- **OIDC everywhere secrets would otherwise be static:** GitHub Actions → GCP via
  Workload Identity Federation (§7); cosign signing is keyless via GitHub's OIDC
  issuer. No long-lived cloud credential is stored as a GitHub secret.
- **Least privilege:** the Workload Identity Pool's provider condition is scoped to
  this specific repo and, for the deploy/infra-apply workflows, this specific
  branch — a fork's PR workflow cannot mint a token that can push images or touch
  Terraform state. Separate service accounts for "push image" versus "deploy to
  VM" versus "apply Terraform," each with only the IAM roles that stage needs.
- **Secret management:** anything the application needs at runtime is a GCP Secret
  Manager reference, resolved on the VM, never a GitHub Actions secret passed
  through env — GitHub secrets are reserved for CI-time credentials only (the WIF
  provider config itself, which is not sensitive by design).
- **Signed commits:** encouraged repo-wide (branch protection can require it once
  contributor tooling is confirmed to support it smoothly — flag-only in v1, per
  §3's Commit stage).
- **Signed containers:** every image, cosign-signed at Container signing (§3);
  the deploy step verifies the signature before pulling, refusing an unsigned or
  mismatched digest.
- **Dependency scanning:** Dependabot (native GitHub, low setup cost) for both
  NuGet and npm ecosystems, configured to open PRs, not auto-merge — a dependency
  bump is still a change that should pass the same Portable Scenario suite as any
  other PR.
- **CodeQL:** enabled for C# and JavaScript/TypeScript, run in Static analysis
  (§3), results in the native Security tab.
- **SBOM generation:** CycloneDX per image (§3, §6), attached to Artifact Registry
  entries and linked from Releases.
- **Vulnerability scanning:** Artifact Registry's built-in vulnerability scanning
  on push, plus SBOM-driven scanning (e.g., `grype` against the generated SBOM) as
  a CI stage that can block on critical/high findings once a baseline of current
  findings has been triaged (not day one — see §14's "measurable need" principle;
  turning this on before triage would immediately red-block every PR against
  existing, already-accepted findings).
- **Policy enforcement:** branch protection requires the checks in §9; Terraform
  changes require the two-approver rule; nothing here needs a dedicated policy
  engine (OPA/Conftest) at current scale — revisit if infra changes grow complex
  enough that "two reviewers read the diff" stops being sufficient.

---

## 14. Scaling Strategy

Each step below is justified only by a specific, named pain the prior step
actually produces — not by anticipating it.

1. **Simple workflows (where this document starts):** one `ci.yml`, one
   `infra-apply.yml`. This is sufficient while there's one staging VM, one set of
   services, and a PR volume low enough that duplicated YAML across two files
   isn't yet a maintenance burden.
2. **Matrix builds:** introduced when there are ≥2 genuinely parallel axes worth
   splitting — e.g., running the dotnet test suite across multiple target
   frameworks, or building multiple service images with divergent build steps.
   Not introduced for "might need it later."
3. **Reusable workflows:** introduced when a second repository (a future Godot
   client repo, per `godot-client-plan.md`, or a spun-out shared package) needs
   the *same* CI shape — reusable workflows solve cross-repo duplication, not
   within-repo tidiness.
4. **Composite actions:** introduced when the same multi-step sequence (e.g.,
   "authenticate to GCP via WIF, then push an image") appears in more than one
   job within `ci.yml`/`infra-apply.yml` — the trigger is duplication actually
   observed in the YAML, not a guess that it'll be needed.
5. **Self-hosted runners:** introduced only if GitHub-hosted runner limits
   (concurrency, minutes, or the inability to reach the staging VM's internal
   network without IAP/tunneling overhead) become a measured bottleneck — e.g., if
   scenario execution against a real network path becomes slow or flaky enough on
   hosted runners that a runner living on the same network as staging measurably
   improves signal quality. Given IAP tunneling already solves the reachability
   problem in the current runbook, this is not an early move.
6. **Distributed runners:** introduced if scenario volume grows enough that a
   single self-hosted runner queue becomes the bottleneck — measured by actual
   queue wait time, not scenario count in the abstract.
7. **Remote execution:** the far end of this ladder (e.g., Bazel remote execution
   for build caching) — justified only if `dotnet build`/`npm build` wall-clock
   time itself becomes the dominant cost in the pipeline, which nothing in the
   current repo size suggests.

The through-line: every step up this ladder should be preceded by a number
(minutes, queue depth, duplicated lines of YAML) that a maintainer can point to,
not a prediction about where the project "will probably" need to go.

---

## 15. Guiding Principles

1. **Automate bookkeeping, never judgment.** The pipeline records what happened
   and recommends; a human decides what it means for the product.
2. **Evidence is immutable.** Once a manifest is referenced by a PR, Release, or
   promotion record, its contents never change — a correction is a new run, not
   an edit.
3. **Scenarios define capability, not the other way around.** If a scenario
   doesn't exist for a behavior, that behavior has no evidence-backed claim to
   any promotion gate above Experimental.
4. **Deploy the artifact that was validated.** A digest, not a tag, not a
   rebuild. If it wasn't signed and evidenced by this pipeline, it doesn't run in
   staging.
5. **Prefer portable workflows over environment-specific scripts.** If a scenario
   needs an `if (environment === 'staging')` branch inside its own logic, that's
   a sign the environment difference belongs in the connection resolver, not the
   scenario.
6. **Scale only when measurements justify it.** Every entry in §14 names its own
   trigger condition; don't pull one forward on intuition.
7. **Humans approve capability; automation proves repeatability.** The ladder in
   §10 is deliberately asymmetric — the bottom is cheap to earn and free to
   automate, the top costs a human's actual judgment and stays that way.
8. **GitHub orchestrates; Google Cloud runs.** If a design pushes runtime
   behavior into workflow YAML (config, secrets, scheduling logic), that's a sign
   it belongs in Terraform or the application instead.
9. **One source of truth per fact.** An image's identity is its digest, not its
   tag. A deploy's history is the Git/Release history, not a spreadsheet. A
   scenario's result is its evidence bundle, not the green checkmark alone — the
   checkmark is a pointer to the bundle, not a replacement for it.
