> **DRAFT — assembled from the public roadmap journal. Never auto-posted. Edit before pasting; delete freely.**

---

Assembled: 2026-07-29T01:48:51Z
Source: Lumberjacks/docs/roadmap/commit-notes.jsonl
Entries: 8 (milestones: A7, M7; kinds: decision, documentation, implementation)

## M7

- **Adopt a Harmony patch policy** — A written policy now governs ComfyNetworkSense Harmony patches: attribute prefix/postfix applied in Awake as the default shape, transpilers only for surgical call-site swaps that must degrade to a no-op, an inlining escalation ladder, load-bearing patch ordering recorded at the patch site, and detour cost measured rather than assumed. <!-- 20260729002358-adopt-a-harmony-patch-policy -->
- **Stage the patch-load A/B rollup, built but never run** — Hot Harmony patch bodies now accumulate per-call timing and emit per-interval rollups to perf-patchload.jsonl behind a new default-off Perf key perfPatchLoadRollupEnabled, with a lab runbook for an inert-versus-armed A/B comparison; volunteer telemetry is unchanged. <!-- 20260729002425-stage-the-patch-load-a-b-rollup-built-but-never- -->
- **Pin the networking lane and open the Community Workbench milestone** — The networking lane parks on a deliberate hard hold at a green machine-state: every remaining step needs live two-human Steam observation and none is scheduled; the hold, its pinned items, and a one-command resume path are recorded in fieldlab/PINNED-networking-lane-2026-07.md. <!-- 20260729003231-pin-the-networking-lane-and-open-the-community-w -->

## A7

- **Fix the share-blockers ahead of the Workbench catalog** — The guest-package installer finally has a README (its reissue TODO quoted as a known gap); the quest vertical-slice architecture doc now carries a banner mapping which layers are live, which moved into the mod, and which were pruned to the public archive; the MCP mod-channel gateway accepts COMFY_GATEWAY_PYTHON instead of a hardcoded other-repo venv path while staying localhost dev-only; and the missing gm-template example is explicitly labeled as Workbench first task QP-1 in sources.json. <!-- 20260729004333-fix-the-share-blockers-ahead-of-the-workbench-ca -->
- **Open the /workbench catalog surface** — The Community Workbench is built: workbench.json (7 tools, 5-stage ownership ladder, honesty invariants) renders through workbench.mjs into a self-contained /workbench page served like the roadmap (mount-override, per-request reload), with a fail-closed /workbench/downloads lane that verifies SHA-256 per request. <!-- 20260729010144-open-the-workbench-catalog-surface -->
- **Correct the licensing term on the public journal** — A 2026-07-23 journal record used the wrong licensing term for this project. <!-- 20260729010314-correct-the-licensing-term-on-the-public-journal -->
- **Ship the cold-start kits behind a privacy gate** — Two downloadable kits now exist: a quest-picker kit with a synthetic sample guild, verified to run from a fresh folder with only Python and openpyxl, and a telemetry starter kit that polls the public aggregates-only v0 API with the standard library. <!-- 20260729010634-ship-the-cold-start-kits-behind-a-privacy-gate -->
- **Stage the Discord seeds and the ownership ledger** — The rollout's Discord layer exists as reviewable files, not posts: an announcement that states the pause plainly and is explicitly not a verdict on anyone, one thread seed per first-wave tool with achievable first tasks, a pinned how-this-works post covering the batch-reply rhythm and graceful step-back, and one thread for the two revivable pieces where reviving is the claiming path. <!-- 20260729010653-stage-the-discord-seeds-and-the-ownership-ledger -->

## Verification receipts

### 20260729002358-adopt-a-harmony-patch-policy — Adopt a Harmony patch policy
- Policy patterns cross-checked against the mod: the ZdoSendCadenceOverride transpiler, ZdoRedirect/NetcodeProbe priority ordering, and UnpatchSelf teardown all match the written rules.

### 20260729002425-stage-the-patch-load-a-b-rollup-built-but-never- — Stage the patch-load A/B rollup, built but never run
- Mod compiled clean (Release, net48) with the plugin-copy guard; the new key defaults off; no benchmark run or evidence folder exists yet and the runbook says so.

### 20260729003231-pin-the-networking-lane-and-open-the-community-w — Pin the networking lane and open the Community Workbench milestone
- Working tree clean except docs/audit (deliberately held for review); every path the pin document references resolves; roadmap render and staged checks green.

### 20260729004333-fix-the-share-blockers-ahead-of-the-workbench-ca — Fix the share-blockers ahead of the Workbench catalog
- Every path in the banner verified against the tree by the fixing agent; sources.json re-validated as JSON; no binding or behavior changes to the gateway.

### 20260729010144-open-the-workbench-catalog-surface — Open the /workbench catalog surface
- workbench:check green (7 tools, validators proven fail-closed via 16 mutation tests); Gateway built clean in the sdk:9.0 container with 6/6 roadmap endpoint tests passing; roadmap re-rendered after the nav change.

### 20260729010314-correct-the-licensing-term-on-the-public-journal — Correct the licensing term on the public journal
- Guard proven fail-closed: a deliberately mislabeled test note was rejected before any file was written; render and check pass with the glossary entry present.

### 20260729010634-ship-the-cold-start-kits-behind-a-privacy-gate — Ship the cold-start kits behind a privacy gate
- Scanner self-test passes all 12 rules on clean and poisoned fixtures; both zips built CLEAN through the gate; the quest-picker zip extracted and ran cold in a fresh directory, and the rendered picker carries the corrected config path with zero stale references.

### 20260729010653-stage-the-discord-seeds-and-the-ownership-ledger — Stage the Discord seeds and the ownership ledger
- Every status claim in the drafts traces to a repo path the drafting agent read; tone checked against the positioning and adoption strategy docs; no post was made anywhere.

## OPTIONAL SMOOTHING PROMPT

```text
Restate the bullets above in plain conversational language for a Discord post.

Rules:
- You may ONLY restate facts that are already present in the bullets and the
  verification receipts above. Do not add anything that is not already stated.
- No new claims, no invented numbers or dates, no superlatives, no marketing
  register. This is a status update, not a pitch.
- Keep any hedge words exactly as written -- for example "built, never run",
  "no DLL", or "not ready" mean something specific and must not be softened or
  dropped.
- Match how a solo operator talks to their own community: plain, direct, a little
  dry is fine.

Paste the finished text back for Derek to review. He edits and posts it himself;
nothing here posts on its own.
```
