#!/usr/bin/env node

// Renders the public Community Workbench catalog from a single JSON source of truth.
//
// Structurally this is scripts/roadmap.mjs: the same two commands (render, check), the same
// fail-at-parse-time validators, the same self-containment invariants, the same design tokens.
// It deliberately has NO `note` command and no append-only journal — the roadmap records what
// the owner did over time; this page records what a stranger can pick up right now, so its only
// history is the JSON's own diff.
//
// The invariant that matters: every status string on the rendered page is checked against the
// declared enum and must carry a non-empty, honest status_detail. A tool that does not run says
// so in the same typeface as one that does.

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workbenchRelative = 'docs/workbench/workbench.json';
const outputRelative = 'src/Game.Gateway/Community/workbench.html';
const workbenchPath = path.join(repoRoot, workbenchRelative);
const outputPath = path.join(repoRoot, outputRelative);
// In the monorepo the vocabulary is a sibling of Lumberjacks. Fixture repos keep a copy
// inside the package so this generator can still be proven in isolation.
const audienceCandidates = [
  path.join(repoRoot, '..', 'corpus', 'audiences.json'),
  path.join(repoRoot, 'corpus', 'audiences.json'),
];
const audiencesPath = audienceCandidates.find((candidate) => fs.existsSync(candidate)) ?? audienceCandidates[0];
const audiencesRelative = path.relative(repoRoot, audiencesPath).split(path.sep).join('/');

const statuses = new Set(['live', 'dev-only', 'local-only', 'recoverable-not-running']);
const accessKinds = new Set(['site-download', 'public-repo', 'live-service', 'not-published']);
const sourceKinds = new Set(['public-repo', 'private-until-claimed']);
const taskSizes = new Set(['small', 'medium']);
const ownershipStates = new Set(['unclaimed', 'trying', 'claimed', 'owned']);
const completionDestinations = new Set(['tool-thread', 'main-forum']);
const completionStates = new Set(['actionable', 'blocked']);

// Every href that reaches the rendered page must start with one of these. Anything else degrades
// to inert text rather than becoming a live link on a public surface.
const linkPrefixes = [
  'https://github.com/djcdevelopment/',
  'https://discord.com/channels/',
  'https://discord.gg/',
  'https://comfy-p7.duckdns.org/',
  'https://am4.tail8e749c.ts.net/',
  // Baseline's own GitHub Pages site, published from site/ by .github/workflows/pages.yml.
  'https://djcdevelopment.github.io/baseline/',
];

// A /channels/ URL only resolves for someone already inside the server. It is a destination,
// never an entrance, so the page may not offer one without also offering an invite.
const memberOnlyDiscord = 'https://discord.com/channels/';
const inviteShape = /^https:\/\/discord\.gg\/[A-Za-z0-9-]{2,32}$/;

const provisionStateRelative = '../tools/workbench/discord/provision-state.json';

function fail(message) {
  throw new Error(message);
}

function formatUtc(iso) {
  const when = new Date(iso);
  const pad = (value) => String(value).padStart(2, '0');
  return `${when.getUTCFullYear()}-${pad(when.getUTCMonth() + 1)}-${pad(when.getUTCDate())} `
    + `${pad(when.getUTCHours())}:${pad(when.getUTCMinutes())} UTC`;
}

/// The single source of truth for the provenance the page displays. Derived from git, never
/// hand-entered: the previous hand-maintained `updated_at` was false within hours of a change,
/// and a stale date on this page costs more trust than no date would.
///
/// Two modes, split by whether the provenance inputs (the JSON and this generator) carry
/// uncommitted changes:
///
///   production — "Published from <sha7> · <date> UTC", naming the last commit that touched
///     the inputs. Fully deterministic: the same clean checkout renders the same bytes, so
///     check() compares the artifact byte-for-byte, stamp included.
///   preview — "Preview rendered <now> UTC with uncommitted changes". What is on screen is in
///     no commit; say so and timestamp the render. A preview artifact may be inspected locally
///     but can never be published: check() fails a clean tree whose artifact still carries a
///     preview stamp, and the publish gate refuses it outright.
///
/// The commit flow this implies is a pair: commit the inputs first, then render (the stamp
/// names that fresh commit), then commit the regenerated HTML. HTML commits do not touch the
/// inputs, so the stamp stays stable until the next input change.
const provenanceInputs = [workbenchRelative, 'scripts/workbench.mjs', audiencesRelative];

// Both calls below are written against `cwd: repoRoot` and let git discover the repository
// from there. Git hooks break that assumption, the same way roadmap.mjs already documents for
// its own git(): git exports GIT_DIR into the hook environment, and from a linked worktree it
// points at <repo>/.git/worktrees/<name>, whose parent is NOT the work tree. Git then stops
// discovering and treats the current directory as the top of the work tree, so every path in
// this package reads as untracked -- `status --porcelain` reported '?? docs/workbench/
// workbench.json' for a file that is committed and clean. dirty flipped true, the mode flipped
// to preview, and check() rejected a correctly-published page from a worktree while passing
// from the main checkout (observed 2026-08-01, worked around with --no-verify in cab0486).
// Dropping the inherited pointers restores discovery. GIT_INDEX_FILE is deliberately kept:
// nothing here reads the index, and during a hook it names the index being committed.
const gitEnv = { ...process.env };
delete gitEnv.GIT_DIR;
delete gitEnv.GIT_WORK_TREE;

function provenance() {
  const git = (args) => execFileSync('git', args, {
    cwd: repoRoot,
    env: gitEnv,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'ignore'],
  }).trim();

  let committed = '';
  let dirty = false;
  try {
    committed = git(['log', '-1', '--format=%H %cI', '--', ...provenanceInputs]);
    dirty = git(['status', '--porcelain', '--', ...provenanceInputs]).length > 0;
  } catch {
    return { mode: 'no-git', text: 'Content freshness unknown — not rendered from a git checkout' };
  }

  if (dirty || !committed) {
    return { mode: 'preview', text: `Preview rendered ${formatUtc(new Date().toISOString())} with uncommitted changes` };
  }
  const [sha, committedAt] = committed.split(' ');
  // Slice %H here rather than asking git for %h: core.abbrev varies per machine, and the
  // production stamp must be byte-identical wherever the same commit is rendered.
  return { mode: 'production', text: `Published from ${sha.slice(0, 7)} · ${formatUtc(committedAt)}` };
}

const freshnessMarker = /<span class="freshness">[^<]*<\/span>/;

/// Blank the freshness span so a stale-artifact comparison is about content, not about the
/// clock. Only the preview stamp carries a wall-clock time; the production stamp is
/// deterministic, so check() uses this normalisation solely to separate "the content went
/// stale" from "only the stamp is wrong" and to tolerate the moving clock while iterating on
/// uncommitted changes.
function normaliseFreshness(html) {
  return html.replace(freshnessMarker, '<span class="freshness">·</span>');
}

function readSource() {
  const raw = fs.readFileSync(workbenchPath, 'utf8');
  let workbench;
  try {
    workbench = JSON.parse(raw);
  } catch (error) {
    fail(`${workbenchRelative}: invalid JSON: ${error.message}`);
  }
  return { workbench, raw };
}

function requireString(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    fail(`${label} must be a non-empty string`);
  }
}

function requireStringArray(value, label, { allowEmpty = true } = {}) {
  if (!Array.isArray(value)) fail(`${label} must be an array`);
  if (!allowEmpty && value.length === 0) fail(`${label} must not be empty`);
  value.forEach((item, index) => requireString(item, `${label}[${index}]`));
}

function readAudiences() {
  let doc;
  try {
    doc = JSON.parse(fs.readFileSync(audiencesPath, 'utf8'));
  } catch (error) {
    fail(`${audiencesRelative}: cannot read shared audience vocabulary: ${error.message}`);
  }
  if (doc.schema_version !== 1 || !Array.isArray(doc.roles) || doc.roles.length === 0) {
    fail(`${audiencesRelative} must be schema version 1 with a non-empty roles array`);
  }
  const seen = new Set();
  doc.roles.forEach((role, index) => {
    const label = `${audiencesRelative}.roles[${index}]`;
    requireObject(role, label);
    requireString(role.id, `${label}.id`);
    if (!/^[a-z][a-z0-9-]*$/.test(role.id)) fail(`${label}.id must be a lowercase slug: ${role.id}`);
    if (seen.has(role.id)) fail(`${label}.id is duplicated: ${role.id}`);
    seen.add(role.id);
    for (const key of ['label', 'short_label', 'question', 'promise']) requireString(role[key], `${label}.${key}`);
    if (!Number.isInteger(role.order)) fail(`${label}.order must be an integer`);
  });
  return { ...doc, roles: [...doc.roles].sort((a, b) => a.order - b.order) };
}

function requireObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${label} must be an object`);
}

/// A href field that may be null. When it is a string it must be non-empty AND allowlisted, so a
/// bad link fails the build instead of silently rendering as plain text on the published page.
function requireNullableLink(value, label) {
  if (value === null) return;
  requireString(value, `${label} must be null or a non-empty string; ${label}`);
  if (!safeLink(value)) fail(`${label} is not an allowed link target: ${value}`);
}

function requireLink(value, label) {
  requireString(value, label);
  if (!safeLink(value)) fail(`${label} is not an allowed link target: ${value}`);
}

function requireNull(value, label, because) {
  if (value !== null && value !== undefined) fail(`${label} must be null ${because}`);
}

// Copied from scripts/roadmap.mjs on purpose: both public surfaces are generated from
// hand-edited JSON in a repo that also holds real enrollment credentials, and the two
// scripts must not be able to drift into disagreeing about what may be published.
function validateNoSecrets(text) {
  const patterns = [
    { label: 'SteamID', regex: /\b7656119\d{10}\b/ },
    { label: 'credential assignment', regex: /\b(?:client[_ -]?access[_ -]?key|enrollment[_ -]?(?:key|token)|bearer|password|invite[_ -]?token)\s*[=:]\s*["']?[A-Za-z0-9+/_=-]{12,}/i },
    { label: 'credential query string', regex: /[?&](?:access[_-]?key|bearer|invite|key|token)=[^&\s]{8,}/i },
  ];

  for (const pattern of patterns) {
    if (pattern.regex.test(text)) {
      fail(`public workbench source contains a possible ${pattern.label}`);
    }
  }
}

function validateLadder(ladder) {
  if (!Array.isArray(ladder) || ladder.length !== 5) fail('workbench.ladder must contain exactly the five stages');
  ladder.forEach((step, index) => {
    const label = `workbench.ladder[${index}]`;
    requireObject(step, label);
    if (step.stage !== index) fail(`${label}.stage must be ${index} so the ladder reads in order`);
    requireString(step.name, `${label}.name`);
    requireString(step.what_you_did, `${label}.what_you_did`);
    requireString(step.what_you_get, `${label}.what_you_get`);
    requireString(step.recorded_in, `${label}.recorded_in`);
  });
}

function validateAccess(access, label, status) {
  requireObject(access, label);
  if (!accessKinds.has(access.kind)) fail(`${label}.kind is not allowed: ${access.kind}`);

  if (access.kind === 'not-published') {
    requireNull(access.href, `${label}.href`, 'when nothing is published');
  } else {
    requireLink(access.href, `${label}.href`);
  }

  if (access.kind === 'site-download') {
    // A published byte-stream must be verifiable before anyone downloads it. These three fields
    // are what the Gateway's pointer file is checked against; a download advertised without them
    // is an unverifiable binary on a public page.
    requireString(access.sha256, `${label}.sha256`);
    if (!/^[0-9a-f]{64}$/.test(access.sha256)) fail(`${label}.sha256 must be a lowercase 64-character SHA-256 digest`);
    if (!Number.isSafeInteger(access.size_bytes) || access.size_bytes <= 0) {
      fail(`${label}.size_bytes must be a positive safe integer for a site-download`);
    }
    requireString(access.published_at, `${label}.published_at`);
    if (Number.isNaN(Date.parse(access.published_at))) fail(`${label}.published_at must be an ISO timestamp`);
  } else {
    requireNull(access.sha256, `${label}.sha256`, 'unless kind is site-download');
    requireNull(access.size_bytes, `${label}.size_bytes`, 'unless kind is site-download');
    requireNull(access.published_at, `${label}.published_at`, 'unless kind is site-download');
  }

  if (status === 'recoverable-not-running' && access.kind !== 'not-published') {
    fail(`${label}.kind must be not-published while the tool is recoverable-not-running`);
  }
}

function validateSource(source, label) {
  requireObject(source, label);
  if (!sourceKinds.has(source.kind)) fail(`${label}.kind is not allowed: ${source.kind}`);
  if (source.kind === 'public-repo') requireLink(source.href, `${label}.href`);
  else requireNull(source.href, `${label}.href`, 'while the source is private-until-claimed');
  requireString(source.note, `${label}.note`);
}

function validateOwnership(ownership, label) {
  requireObject(ownership, label);
  if (!ownershipStates.has(ownership.state)) fail(`${label}.state is not allowed: ${ownership.state}`);
  if (ownership.state === 'unclaimed') {
    requireNull(ownership.claimed_by, `${label}.claimed_by`, 'while the tool is unclaimed');
    requireNull(ownership.claimed_at, `${label}.claimed_at`, 'while the tool is unclaimed');
    requireNull(ownership.record, `${label}.record`, 'while the tool is unclaimed');
    return;
  }
  // Claiming something is a public fact about a person, so it needs a citable record.
  requireString(ownership.record, `${label}.record`);
  requireString(ownership.claimed_by, `${label}.claimed_by`);
  requireString(ownership.claimed_at, `${label}.claimed_at`);
  if (Number.isNaN(Date.parse(ownership.claimed_at))) fail(`${label}.claimed_at must be an ISO timestamp`);
}

function validateRecovery(recovery, label, status) {
  if (status !== 'recoverable-not-running') {
    requireNull(recovery, label, 'unless the tool is recoverable-not-running');
    return;
  }
  requireObject(recovery, label);
  requireLink(recovery.origin_href, `${label}.origin_href`);
  requireStringArray(recovery.paths, `${label}.paths`, { allowEmpty: false });
  requireString(recovery.notes, `${label}.notes`);
}

/// Contribution rights are declared per tool, never inferred from licence prose, because the
/// seven tools span four repositories with four different answers. The ladder describes the
/// concept; this is what a volunteer actually gets.
function validateContribution(contribution, label, license) {
  requireObject(contribution, label);
  requireString(contribution.stage_3_reward, `${label}.stage_3_reward`);
  if (typeof contribution.code_contributions !== 'boolean') {
    fail(`${label}.code_contributions must be an explicit boolean`);
  }
  if (!contribution.code_contributions && /commit access/i.test(contribution.stage_3_reward)) {
    fail(`${label}.stage_3_reward promises commit access while code_contributions is false`);
  }
  // An all-rights-reserved licence is exactly where an unexamined "yes" does the most damage,
  // so it has to be acknowledged in the data rather than assumed.
  if (/proprietary/i.test(license) && contribution.code_contributions && contribution.proprietary_ack !== true) {
    fail(`${label}: a proprietary licence with code_contributions true requires ${label}.proprietary_ack === true`);
  }
}

/// Where a first task is completed, and whether a stranger can act on it right now.
///
/// The default is derived, not typed: a task completes in its tool's own thread, and it is
/// actionable exactly when that thread exists. A tool with no thread is a build failure for
/// every completion-less task — never a silent downgrade to blocked — because the page must
/// not offer a task whose only completion path does not exist (MC-1 shipped that way once).
/// The explicit `completion` object overrides the default: `main-forum` routes the task to
/// the page-level forum until a thread opens; `blocked` takes the task out of the actionable
/// count and must say why. Destination hrefs are always derived from discussion.href or
/// feedback.forum_href — an authored completion.href would be a second copy of a fact that
/// already has one home, so it must stay null.
function resolveTaskCompletion(workbench, tool, task, label) {
  const completion = task.completion;
  if (completion === undefined) {
    if (!tool.discussion.href) {
      fail(`${label} (${tool.id}/${task.id}): the task completes in the tool's thread but discussion.href is null. `
        + 'Either wire discussion.href to the real thread, declare completion '
        + '{"destination_kind":"main-forum","href":null,"state":"actionable"} to use the forum meanwhile, '
        + 'or declare completion {"state":"blocked","blocked_reason":"..."} so the actionable count stays honest');
    }
    return { destination_kind: 'tool-thread', state: 'actionable', href: tool.discussion.href, blocked_reason: null };
  }
  requireObject(completion, `${label}.completion`);
  if (!completionDestinations.has(completion.destination_kind)) {
    fail(`${label}.completion.destination_kind is not allowed: ${completion.destination_kind}`);
  }
  if (!completionStates.has(completion.state)) {
    fail(`${label}.completion.state is not allowed: ${completion.state}`);
  }
  requireNull(completion.href, `${label}.completion.href`,
    '— the destination derives from discussion.href or feedback.forum_href, never typed twice');

  if (completion.state === 'blocked') {
    requireString(completion.blocked_reason, `${label}.completion.blocked_reason`);
    if (task.suggested) fail(`${label} (${task.id}) is blocked and may not be the suggested first pick`);
    return { destination_kind: completion.destination_kind, state: 'blocked', href: null, blocked_reason: completion.blocked_reason };
  }

  requireNull(completion.blocked_reason, `${label}.completion.blocked_reason`, 'unless the state is blocked');
  const href = completion.destination_kind === 'tool-thread' ? tool.discussion.href : workbench.feedback.forum_href;
  if (!href) {
    fail(`${label} (${tool.id}/${task.id}): completion.state is actionable but its ${completion.destination_kind} `
      + 'destination is null — a task cannot be actionable while its destination is absent');
  }
  return { destination_kind: completion.destination_kind, state: 'actionable', href, blocked_reason: null };
}

/// One computation, used by the hero, the per-card header, the index rows, and the tests —
/// a second copy of this arithmetic is how a count drifts from what the cards show.
function computeTaskCounts(workbench) {
  let actionable = 0;
  let blocked = 0;
  workbench.tools.forEach((tool, index) => {
    tool.first_tasks.forEach((task, taskIndex) => {
      const resolved = resolveTaskCompletion(workbench, tool, task, `workbench.tools[${index}].first_tasks[${taskIndex}]`);
      if (resolved.state === 'blocked') blocked += 1;
      else actionable += 1;
    });
  });
  return { present: actionable + blocked, actionable, blocked };
}

function validateTool(workbench, tool, index, seenIds, audienceIds) {
  const label = `workbench.tools[${index}]`;
  requireObject(tool, label);
  requireString(tool.id, `${label}.id`);
  if (!/^[a-z0-9][a-z0-9-]*$/.test(tool.id)) fail(`${label}.id must be a lowercase slug: ${tool.id}`);
  if (seenIds.has(tool.id)) fail(`duplicate tool id ${tool.id}`);
  seenIds.add(tool.id);

  requireStringArray(tool.audiences, `${label}.audiences`, { allowEmpty: false });
  if (new Set(tool.audiences).size !== tool.audiences.length) fail(`${label}.audiences must not contain duplicates`);
  const unknownAudiences = tool.audiences.filter((audience) => !audienceIds.has(audience));
  if (unknownAudiences.length > 0) {
    fail(`${label}.audiences contains unknown shared audience IDs: ${unknownAudiences.join(', ')}`);
  }

  requireString(tool.name, `${label}.name`);
  requireString(tool.one_liner, `${label}.one_liner`);
  if (!statuses.has(tool.status)) fail(`${label}.status is not allowed: ${tool.status}`);
  // The load-bearing rule of this page: no bare status chip. Every one is qualified in prose.
  requireString(tool.status_detail, `${label}.status_detail`);
  requireString(tool.what_it_does, `${label}.what_it_does`);
  requireString(tool.who_its_for, `${label}.who_its_for`);
  requireString(tool.time_to_first_result, `${label}.time_to_first_result`);
  requireStringArray(tool.requires, `${label}.requires`);

  validateAccess(tool.access, `${label}.access`, tool.status);
  validateSource(tool.source, `${label}.source`);

  // The contribution schema is authoritative; a source note may not restate or contradict it.
  // Ordered: the specific contradiction first, so the sharper message wins when both would fire.
  if (!tool.contribution?.code_contributions && /commit access/i.test(tool.source.note)) {
    fail(`${label}.source.note promises commit access while code_contributions is false`);
  }
  const policyVocab = tool.source.note.match(/nothing here is gated|ladder stage|commit access|\bgated\b/i);
  if (policyVocab) {
    fail(`${label}.source.note restates access policy ("${policyVocab[0]}") — the renderer derives that line from source.kind and code_contributions; keep the note to tool-specific facts`);
  }

  if (!Array.isArray(tool.docs)) fail(`${label}.docs must be an array`);
  tool.docs.forEach((doc, docIndex) => {
    const docLabel = `${label}.docs[${docIndex}]`;
    requireObject(doc, docLabel);
    requireString(doc.label, `${docLabel}.label`);
    requireNullableLink(doc.href, `${docLabel}.href`);
  });
  // If it runs today, a stranger must be able to read how to run it.
  if (tool.status === 'live' && tool.docs.length === 0) {
    fail(`${label}.docs must contain at least one entry while the tool status is live`);
  }

  requireObject(tool.discussion, `${label}.discussion`);
  requireString(tool.discussion.label, `${label}.discussion.label`);
  requireNullableLink(tool.discussion.href, `${label}.discussion.href`);

  if (!Array.isArray(tool.first_tasks) || tool.first_tasks.length === 0) {
    fail(`${label}.first_tasks must contain at least one task — a tool nobody can start is not on offer`);
  }
  const taskIds = new Set();
  tool.first_tasks.forEach((task, taskIndex) => {
    const taskLabel = `${label}.first_tasks[${taskIndex}]`;
    requireObject(task, taskLabel);
    requireString(task.id, `${taskLabel}.id`);
    if (taskIds.has(task.id)) fail(`${taskLabel}.id is duplicated within ${tool.id}: ${task.id}`);
    taskIds.add(task.id);
    requireString(task.title, `${taskLabel}.title`);
    if (!taskSizes.has(task.size)) fail(`${taskLabel}.size is not allowed: ${task.size}`);
    requireString(task.done_when, `${taskLabel}.done_when`);
    if (task.suggested !== undefined && typeof task.suggested !== 'boolean') {
      fail(`${taskLabel}.suggested must be a boolean when present`);
    }
    resolveTaskCompletion(workbench, tool, task, taskLabel);
  });

  validateOwnership(tool.ownership, `${label}.ownership`);
  validateRecovery(tool.recovery ?? null, `${label}.recovery`, tool.status);
  requireStringArray(tool.roadmap_milestones, `${label}.roadmap_milestones`);
  requireString(tool.privacy_note, `${label}.privacy_note`);
  requireString(tool.license, `${label}.license`);
  validateContribution(tool.contribution, `${label}.contribution`, tool.license);
}

function validate(workbench, rawText = '', audienceDoc = readAudiences()) {
  if (workbench.schema_version !== 1) fail('workbench.schema_version must be 1');
  requireString(workbench.title, 'workbench.title');
  requireString(workbench.headline, 'workbench.headline');
  // Deliberately absent. Freshness is derived from git by contentFreshness(); a hand-entered
  // date here would become a second, competing source of truth and go stale the same way.
  if ('updated_at' in workbench) {
    fail('workbench.updated_at must not exist — the displayed freshness is derived from git, not typed in');
  }
  requireLink(workbench.owners_href, 'workbench.owners_href');
  requireString(workbench.honesty_statement, 'workbench.honesty_statement');
  requireString(workbench.not_a_verdict, 'workbench.not_a_verdict');
  // The summary is what a reader who never expands the disclosure actually sees, so it has to
  // carry the protective claim on its own rather than merely pointing at it.
  requireString(workbench.not_a_verdict_summary, 'workbench.not_a_verdict_summary');
  // The ladder sits below the tools, so the hero is the only place left that can say a
  // non-code contribution counts. Required, not optional.
  requireString(workbench.invitation_line, 'workbench.invitation_line');

  requireObject(workbench.feedback, 'workbench.feedback');
  requireString(workbench.feedback.rhythm, 'workbench.feedback.rhythm');
  requireString(workbench.feedback.forum_label, 'workbench.feedback.forum_label');
  requireNullableLink(workbench.feedback.forum_href, 'workbench.feedback.forum_href');
  requireString(workbench.feedback.dispatches_label, 'workbench.feedback.dispatches_label');
  requireNullableLink(workbench.feedback.dispatches_href, 'workbench.feedback.dispatches_href');
  requireString(workbench.feedback.start_here_label, 'workbench.feedback.start_here_label');
  requireNullableLink(workbench.feedback.start_here_href, 'workbench.feedback.start_here_href');
  requireString(workbench.feedback.invite_label, 'workbench.feedback.invite_label');
  requireNullableLink(workbench.feedback.invite_href, 'workbench.feedback.invite_href');
  if (workbench.feedback.invite_href !== null && !inviteShape.test(workbench.feedback.invite_href)) {
    fail(`workbench.feedback.invite_href must be a https://discord.gg/ invite: ${workbench.feedback.invite_href}`);
  }
  validateLadder(workbench.ladder);
  // Stage 3 describes the concept; the cards carry the concrete right. If the rung names a
  // specific grant it will contradict at least one of the seven tools.
  if (/commit access/i.test(workbench.ladder[3].what_you_get)) {
    fail('workbench.ladder[3].what_you_get must not promise commit access globally — it differs per tool, so the card is authoritative');
  }

  if (!Array.isArray(workbench.tools) || workbench.tools.length === 0) fail('workbench.tools must not be empty');
  const seenIds = new Set();
  const audienceIds = new Set(audienceDoc.roles.map((role) => role.id));
  workbench.tools.forEach((tool, index) => validateTool(workbench, tool, index, seenIds, audienceIds));

  // Counts are computed, never typed. The headline may name the tool count as prose, but only
  // while it agrees with the data it sits beside; task counts may not be typed anywhere at all,
  // because the hero's actionable count moves whenever a task blocks or a thread opens.
  const headlineCount = workbench.headline.match(/\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|\d+)\s+tools?\b/i);
  if (headlineCount) {
    const words = ['one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 'eleven', 'twelve'];
    const claimed = words.indexOf(headlineCount[1].toLowerCase()) >= 0
      ? words.indexOf(headlineCount[1].toLowerCase()) + 1
      : Number(headlineCount[1]);
    if (claimed !== workbench.tools.length) {
      fail(`workbench.headline says ${headlineCount[1]} tools but the catalog holds ${workbench.tools.length} — the prose count must match the data or leave the number out`);
    }
  }
  const countProse = (rawText || '').match(/\b\d+\s+(?:first\s+)?tasks?\s+open\b|\b\d+\s+(?:tasks?\s+)?(?:actionable|blocked)\b/i);
  if (countProse) {
    fail(`hardcoded task-count prose is forbidden (found "${countProse[0]}") — the renderer computes every count from first_tasks`);
  }

  // Governance is impersonal on this page: authority belongs to "the project operator", a role
  // a stranger can locate in OWNERS.md, not to a first name they have never been introduced to.
  if (/\bDerek\b/.test(rawText || '')) {
    fail('workbench.json must not name a person — write "the project operator"; identity and ownership live in OWNERS.md');
  }

  // A recommended entry point only works if there is exactly one of them. Two suggestions is
  // a comparison to make, which is the thing the suggestion exists to spare a newcomer.
  const suggested = workbench.tools.flatMap((tool) => tool.first_tasks.filter((task) => task.suggested));
  if (suggested.length > 1) {
    fail(`only one first task may be suggested; found ${suggested.length}: ${suggested.map((task) => task.id).join(', ')}`);
  }

  // The rule that matters: a member-only link may not be the only way in. An inert discussion
  // row renders no URL at all, so the five deliberate placeholders cannot trip this.
  const memberOnlyLinks = [
    workbench.feedback.forum_href,
    workbench.feedback.dispatches_href,
    workbench.feedback.start_here_href,
    ...workbench.tools.map((tool) => tool.discussion.href),
  ].filter((href) => typeof href === 'string' && href.startsWith(memberOnlyDiscord));
  if (memberOnlyLinks.length > 0 && !workbench.feedback.invite_href) {
    fail(`${memberOnlyLinks.length} member-only Discord link(s) render with no invite — a first-time visitor cannot reach any of them`);
  }

  validateNoSecrets(rawText || JSON.stringify(workbench));
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

// Same shape as roadmap.mjs's safeLink, with a narrower allowlist for this page and one extra
// guard: a bare leading '/' would also admit '//evil.example', which the browser reads as a
// protocol-relative absolute URL, not a site-relative path.
function safeLink(href) {
  if (typeof href !== 'string' || href.length === 0) return false;
  if (href.startsWith('//')) return false;
  if (href.startsWith('/')) return true;
  return linkPrefixes.some((prefix) => href.startsWith(prefix));
}

function renderLink(item) {
  if (!safeLink(item.href)) return `<span class="inert-link">${escapeHtml(item.label)}</span>`;
  const external = item.href.startsWith('https://');
  return `<a href="${escapeHtml(item.href)}"${external ? ' target="_blank" rel="noreferrer"' : ''}>${escapeHtml(item.label)}</a>`;
}

function list(items, className = '') {
  return `<ul${className ? ` class="${className}"` : ''}>${items.map((item) => `<li>${escapeHtml(item)}</li>`).join('')}</ul>`;
}

function cssToken(value) {
  return String(value).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'unknown';
}

function statusLabel(status) {
  return {
    'live': 'LIVE',
    'dev-only': 'DEV ONLY',
    'local-only': 'LOCAL ONLY',
    'recoverable-not-running': 'RECOVERABLE · NOT RUNNING',
  }[status] ?? String(status).toUpperCase();
}

/// Compact form for the hero tally, where the full "RECOVERABLE · NOT RUNNING" chip text would
/// read as two separate counts.
function statusShortLabel(status) {
  return status === 'recoverable-not-running' ? 'recoverable' : status;
}

function ownershipLabel(state) {
  return {
    unclaimed: 'UNCLAIMED',
    trying: 'SOMEONE IS TRYING',
    claimed: 'CLAIMED',
    owned: 'OWNED',
  }[state] ?? String(state).toUpperCase();
}

function ownershipSentence(ownership) {
  if (ownership.state === 'unclaimed') {
    return 'Nobody holds this yet. Completing one of the first tasks above is how that changes.';
  }
  const who = escapeHtml(ownership.claimed_by);
  const when = escapeHtml(ownership.claimed_at);
  return `${who} since ${when}.`;
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/// The button alone. Its visible text is short so it can sit inline in the action row next to
/// the first-result badge; the tool name rides in the accessible name instead, so a screen
/// reader never hears seven undifferentiated "Download" links.
function renderAccessButton(tool) {
  const access = tool.access;
  if (access.kind === 'site-download') {
    return `<a class="access-button site-download" href="${escapeHtml(access.href)}" aria-label="Download ${escapeHtml(tool.name)}">Download</a>`;
  }
  if (access.kind === 'public-repo') {
    return `<a class="access-button public-repo" href="${escapeHtml(access.href)}" target="_blank" rel="noreferrer" aria-label="Open the public repository for ${escapeHtml(tool.name)}">Open repository</a>`;
  }
  if (access.kind === 'live-service') {
    return `<a class="access-button live-service" href="${escapeHtml(access.href)}" target="_blank" rel="noreferrer" aria-label="Open the live service for ${escapeHtml(tool.name)}">Open live service</a>`;
  }
  return '<span class="access-button disabled" aria-disabled="true">Not published yet</span>';
}

/// Everything that qualifies the button: the digest triple that makes a download verifiable
/// before anyone runs it, or the sentence explaining why there is no file. Rendered outside the
/// button so a four-line verification block cannot inflate the call to action, and outside any
/// <details> because both are load-bearing — check() enforces that.
function renderAccessDetail(tool) {
  const access = tool.access;
  if (access.kind === 'site-download') {
    return `<div class="access-verify">
      <div class="access-detail"><span>SHA-256</span><code>${escapeHtml(access.sha256)}</code></div>
      <div class="access-detail"><span>Size</span><code>${escapeHtml(formatBytes(access.size_bytes))}</code></div>
      <div class="access-detail"><span>Published</span><code>${escapeHtml(access.published_at)}</code></div>
    </div>`;
  }
  const note = {
    'public-repo': 'Clone and build it yourself — there is no packaged download.',
    'live-service': 'This one is a running service, not a file. There is nothing to download.',
    'not-published': 'No download exists for this today. That is a fact about the packaging, not about whether the code works — read the status above.',
  }[access.kind];
  return `<div class="access-verify"><p class="access-note">${escapeHtml(note)}</p></div>`;
}

/// A go/no-go gate — "do I have Java?" — so it stays out of the disclosures entirely. These are
/// qualified sentences rather than tokens ("a copy, never the live save"), so they stay a list:
/// chipping them would either truncate the qualifier or produce chips the width of the card.
function renderRequires(tool) {
  const body = tool.requires.length > 0
    ? list(tool.requires, 'requires-list')
    : '<p class="inert-link">Nothing beyond a browser.</p>';
  return `<div class="requires"><strong>You will need</strong>${body}</div>`;
}

/// The first doc that actually resolves. A doc whose href is null degrades to inert text, which
/// is the right thing in a list but dead weight in an action row, so the promoted slot skips it.
function primaryDocIndex(tool) {
  return tool.docs.findIndex((doc) => safeLink(doc.href));
}

function renderPrimaryDoc(tool) {
  const index = primaryDocIndex(tool);
  return index < 0 ? '' : `<span class="tool-link"><em>Docs</em>${renderLink(tool.docs[index])}</span>`;
}

/// Whatever the action row did not promote. Returns '' rather than an empty labelled row when
/// the promoted link was the only one.
function renderDocs(tool) {
  if (tool.docs.length === 0) {
    return '<div class="tool-links"><strong>Docs</strong><span class="inert-link">None written yet.</span></div>';
  }
  const skip = primaryDocIndex(tool);
  const rest = tool.docs.filter((_, index) => index !== skip);
  if (rest.length === 0) return '';
  const items = rest.map((doc) => renderLink(doc)).join('<span aria-hidden="true"> · </span>');
  return `<div class="tool-links"><strong>More docs</strong>${items}</div>`;
}

/// Stays in the always-visible zone whether or not it resolves. When a thread exists this is
/// the conversion destination. When it does not, but a task explicitly completes in the main
/// forum, the row links the forum and says so — the temporary destination has to be reachable
/// from the card that names it. Only when no destination exists at all does the row stay
/// inert, and the inert label is itself the honest answer.
function renderDiscussionLink(workbench, tool) {
  if (safeLink(tool.discussion.href)) {
    return `<span class="tool-link"><em>Discuss</em>${renderLink(tool.discussion)}</span>`;
  }
  const forumInterim = safeLink(workbench.feedback.forum_href)
    && tool.first_tasks.some((task, taskIndex) => {
      const resolved = resolveTaskCompletion(workbench, tool, task, `${tool.id}.first_tasks[${taskIndex}]`);
      return resolved.state === 'actionable' && resolved.destination_kind === 'main-forum';
    });
  const thread = forumInterim
    ? renderLink({ label: `no thread yet — post in the ${workbench.feedback.forum_label}`, href: workbench.feedback.forum_href })
    : '<span class="inert-link">no thread yet</span>';
  return `<span class="tool-link"><em>Discuss</em>${thread}</span>`;
}

/// Per-tool actionable/blocked split, shared by the card header and the index row.
function toolTaskCounts(workbench, tool) {
  let actionable = 0;
  let blocked = 0;
  tool.first_tasks.forEach((task, taskIndex) => {
    const resolved = resolveTaskCompletion(workbench, tool, task, `${tool.id}.first_tasks[${taskIndex}]`);
    if (resolved.state === 'blocked') blocked += 1;
    else actionable += 1;
  });
  return { actionable, blocked };
}

function renderFirstTasks(workbench, tool) {
  const rows = tool.first_tasks.map((task, taskIndex) => {
    const resolved = resolveTaskCompletion(workbench, tool, task, `${tool.id}.first_tasks[${taskIndex}]`);
    // A blocked task stays on the page with its reason in the same typeface as the invitation —
    // hiding it would make the catalog look more complete than it is.
    const blockedChip = resolved.state === 'blocked' ? '<span class="task-blocked">blocked</span>' : '';
    const blockedReason = resolved.state === 'blocked'
      ? `<span class="task-blocked-reason"><em>Blocked:</em> ${escapeHtml(resolved.blocked_reason)}</span>`
      : '';
    return `<li class="first-task${task.suggested ? ' suggested' : ''}">
        <div class="first-task-top">
          <strong>${escapeHtml(task.title)}</strong>
          <span class="task-flags">${blockedChip}${task.suggested ? '<span class="task-suggested">good first pick</span>' : ''}<span class="task-size ${cssToken(task.size)}">${escapeHtml(task.size)}</span></span>
        </div>
        <span class="done-when"><em>Done when:</em> ${escapeHtml(task.done_when)}</span>
        ${blockedReason}
        <span class="task-id">${escapeHtml(task.id)}</span>
      </li>`;
  }).join('');
  const counts = toolTaskCounts(workbench, tool);
  const header = counts.blocked === 0
    ? `Pick one to start · ${escapeHtml(String(counts.actionable))} open`
    : counts.actionable === 0
      ? `Nothing to start yet · ${escapeHtml(String(counts.blocked))} blocked`
      : `Pick one to start · ${escapeHtml(String(counts.actionable))} open · ${escapeHtml(String(counts.blocked))} blocked`;
  // Ownership lives inside this box rather than beside it: it is the answer to "what happens if
  // I do one of these", not a fact of equal standing with the tasks themselves.
  const ownership = `<div class="ownership-state ${escapeHtml(cssToken(tool.ownership.state))}">
        <span class="pill ownership ${escapeHtml(cssToken(tool.ownership.state))}">${ownershipLabel(tool.ownership.state)}</span>
        <span>${ownershipSentence(tool.ownership)}</span>
        ${tool.ownership.record ? `<span class="ownership-record">recorded in ${escapeHtml(tool.ownership.record)}</span>` : ''}
      </div>
      <p class="stage-3-right"><strong>At stage 3</strong>${escapeHtml(tool.contribution.stage_3_reward)}</p>`;
  return `<div class="first-tasks">
      <strong>${header}</strong>
      <ul class="first-task-list">${rows}</ul>
      ${ownership}
    </div>`;
}

function renderRecovery(tool) {
  if (!tool.recovery) return '';
  const paths = tool.recovery.paths.map((item) => `<li><code>${escapeHtml(item)}</code></li>`).join('');
  return `<div class="recovery">
      <strong>Where the pieces are</strong>
      <p>${escapeHtml(tool.recovery.notes)}</p>
      <p class="recovery-origin">${renderLink({ label: tool.recovery.origin_href, href: tool.recovery.origin_href })}</p>
      <ul class="recovery-paths">${paths}</ul>
    </div>`;
}

function renderTool(workbench, tool) {
  const token = cssToken(tool.status);
  const milestones = tool.roadmap_milestones.length > 0
    ? tool.roadmap_milestones.map((id) => `<span class="dep">${escapeHtml(id)}</span>`).join('')
    : '<span class="dep">unlinked</span>';
  const sourceLine = safeLink(tool.source.href)
    ? renderLink({ label: tool.source.href, href: tool.source.href })
    : '<span class="inert-link">Private until claimed</span>';
  const moreDocs = renderDocs(tool);

  // Block order is deliberate and the rule behind it is: disclosure is for depth, never for
  // gates. Anything a reader uses to rule themselves out — status, requirements, digest,
  // first tasks — stays visible. Only what they read to go deeper collapses.
  return `<article class="tool ${escapeHtml(token)}" id="${escapeHtml(tool.id)}">
    <div class="tool-head">
      <div><h3>${escapeHtml(tool.name)}</h3><span class="tool-id">${escapeHtml(tool.id)}</span></div>
      <span class="pill ${escapeHtml(token)}">${statusLabel(tool.status)}</span>
    </div>
    <p class="one-liner">${escapeHtml(tool.one_liner)}</p>
    <div class="status-detail ${escapeHtml(token)}"><strong>What that status actually means</strong>${escapeHtml(tool.status_detail)}</div>
    <p class="tool-audience"><strong>For</strong>${escapeHtml(tool.who_its_for)}</p>
    <div class="tool-action">
      <span class="first-result"><em>First result</em>${escapeHtml(tool.time_to_first_result)}</span>
      ${renderAccessButton(tool)}
    </div>
    ${renderAccessDetail(tool)}
    <div class="tool-link-row">${renderPrimaryDoc(tool)}${renderDiscussionLink(workbench, tool)}</div>
    ${renderRequires(tool)}
    <details class="tool-detail">
      <summary>How it works, in detail</summary>
      <div class="tool-detail-body">
        <p>${escapeHtml(tool.what_it_does)}</p>
        <p class="deps">roadmap ${milestones}</p>
      </div>
    </details>
    ${renderFirstTasks(workbench, tool)}
    ${renderRecovery(tool)}
    <details class="tool-detail">
      <summary>Source, privacy &amp; licence</summary>
      <div class="tool-detail-body">
        ${moreDocs}
        <div class="tool-links"><strong>Source</strong>${sourceLine}</div>
        <p class="access-policy">${escapeHtml(accessPolicyLine(tool))}</p>
        <p class="source-note">${linkLicensing(escapeHtml(tool.source.note))}</p>
        <div class="tool-footnotes">
          <div class="privacy"><strong>Privacy</strong>${linkLicensing(escapeHtml(tool.privacy_note))}</div>
          <div class="license"><strong>License</strong>${linkLicensing(escapeHtml(tool.license))}</div>
        </div>
      </div>
    </details>
  </article>`;
}

/// The ladder pays out in "credited in OWNERS.md" three times over. Naming a file a reader
/// cannot open is an unverifiable promise, so every mention becomes a link — from one stored
/// href, never re-typed into the prose. Escaping runs first, so this can only ever wrap a
/// literal token that survived escaping.
function linkOwners(escaped, ownersHref) {
  return escaped.replaceAll(
    'OWNERS.md',
    `<a href="${escapeHtml(ownersHref)}" target="_blank" rel="noreferrer">OWNERS.md</a>`,
  );
}

/// Same argument for the licence file: six cards end "see LICENSING.md." and a named document
/// must be reachable. One stored href — the same one the footer's "license details" uses.
const licensingHref = 'https://github.com/djcdevelopment/baseline/blob/main/docs/legal/LICENSING.md';

function linkLicensing(escaped) {
  return escaped.replaceAll(
    'LICENSING.md',
    `<a href="${escapeHtml(licensingHref)}" target="_blank" rel="noreferrer">LICENSING.md</a>`,
  );
}

/// The compact access-policy line, derived from structured fields only. This used to be prose
/// repeated per tool in source.note — three byte-identical copies and one near-miss — which is
/// a drift surface: the schema (source.kind, contribution.code_contributions) is authoritative,
/// so the sentence is computed from it and the notes keep only tool-specific facts.
function accessPolicyLine(tool) {
  if (tool.source.kind !== 'public-repo') {
    return 'The source opens when the tool is claimed — until then it is the one gated thing on this card.';
  }
  return tool.contribution.code_contributions
    ? 'Nothing here is gated — the source is readable now. Ladder stage 3 opens commit access for this tool.'
    : 'Nothing here is gated — the source is readable now. Ladder stage 3 does not open commit access here — the card above says what it grants.';
}

function renderLadder(ladder, ownersHref) {
  return `<div class="ladder" role="list" aria-label="Participation ladder">${ladder.map((step) => `<article class="ladder-step stage-${escapeHtml(String(step.stage))}" role="listitem">
      <div class="ladder-stage">Stage ${escapeHtml(String(step.stage))}</div>
      <h3>${escapeHtml(step.name)}</h3>
      <p class="ladder-did">${escapeHtml(step.what_you_did)}</p>
      <div class="ladder-get"><strong>You get</strong>${linkOwners(escapeHtml(step.what_you_get), ownersHref)}</div>
      <div class="ladder-recorded">${linkOwners(escapeHtml(step.recorded_in), ownersHref)}</div>
    </article>`).join('')}</div>`;
}

/// Rows, not tiles. A grid of mini-cards sitting above a grid of real cards reads as
/// duplication and makes the eye run the same scan twice; one dense row per tool is visibly a
/// different kind of object from the thing it indexes, and reads top-to-bottom in one sweep.
function renderToolIndex(workbench, tools) {
  const rows = tools.map((tool) => {
    const token = cssToken(tool.status);
    const counts = toolTaskCounts(workbench, tool);
    const tally = counts.blocked === 0
      ? `${counts.actionable} open`
      : `${counts.actionable} open · ${counts.blocked} blocked`;
    return `<a class="index-row ${escapeHtml(token)}" href="#${escapeHtml(tool.id)}">
          <span class="pill ${escapeHtml(token)}">${statusLabel(tool.status)}</span>
          <span class="index-name">${escapeHtml(tool.name)}</span>
          <span class="index-time">${escapeHtml(tool.time_to_first_result)}</span>
          <span class="index-tasks">${escapeHtml(tally)}</span>
        </a>`;
  }).join('');
  return `<nav class="tool-index" aria-label="All tools">${rows}</nav>`;
}

function renderAudienceLenses(workbench, audienceDoc) {
  const cards = audienceDoc.roles.map((role) => {
    const matches = workbench.tools.filter((tool) => tool.audiences.includes(role.id));
    const toc = matches.map((tool) => `<a href="#${escapeHtml(tool.id)}">${escapeHtml(tool.name)}</a>`).join('');
    return `<article class="lens" id="lens-${escapeHtml(role.id)}">
      <div class="lens-kicker">${escapeHtml(role.short_label)}</div>
      <h3>${escapeHtml(role.question)}</h3>
      <p>${escapeHtml(role.promise)}</p>
      <nav class="lens-toc" aria-label="${escapeHtml(role.label)} tools">${toc}</nav>
      <a class="lens-more" href="https://djcdevelopment.github.io/baseline/for/${escapeHtml(role.id)}/">Stories, updates, and everything in this view →</a>
    </article>`;
  }).join('');
  const jump = audienceDoc.roles.map((role) => `<a href="#lens-${escapeHtml(role.id)}">${escapeHtml(role.short_label)}</a>`).join('');
  return `<nav class="lens-jump" aria-label="Choose an audience lens">${jump}</nav>
    <div class="lens-grid">${cards}</div>
    <p class="outside-lane">These are starting points, not access controls. <a href="#tools">Cross the lane and inspect all ${escapeHtml(String(workbench.tools.length))} tools.</a></p>`;
}

function statusSummary(tools) {
  const counts = new Map();
  for (const tool of tools) counts.set(tool.status, (counts.get(tool.status) ?? 0) + 1);
  return [...statuses]
    .filter((status) => counts.has(status))
    .map((status) => `${counts.get(status)} ${statusShortLabel(status)}`)
    .join(' · ');
}

function render(workbench, audienceDoc = readAudiences()) {
  const tools = workbench.tools.map((tool) => renderTool(workbench, tool)).join('\n');
  const ladder = renderLadder(workbench.ladder, workbench.owners_href);
  const toolIndex = renderToolIndex(workbench, workbench.tools);
  const audienceLenses = renderAudienceLenses(workbench, audienceDoc);
  const freshness = provenance().text;
  const summary = statusSummary(workbench.tools);
  const counts = computeTaskCounts(workbench);
  const forum = safeLink(workbench.feedback.forum_href)
    ? renderLink({ label: workbench.feedback.forum_label, href: workbench.feedback.forum_href })
    : `<span class="inert-link">${escapeHtml(workbench.feedback.forum_label)}</span>`;
  const startHere = safeLink(workbench.feedback.start_here_href)
    ? renderLink({ label: workbench.feedback.start_here_label, href: workbench.feedback.start_here_href })
    : `<span class="inert-link">${escapeHtml(workbench.feedback.start_here_label)}</span>`;
  const invite = safeLink(workbench.feedback.invite_href)
    ? `<a class="hero-join" href="${escapeHtml(workbench.feedback.invite_href)}" target="_blank" rel="noreferrer">${escapeHtml(workbench.feedback.invite_label)}</a>`
    : `<span class="inert-link">${escapeHtml(workbench.feedback.invite_label)}</span>`;
  const dispatches = safeLink(workbench.feedback.dispatches_href)
    ? `<a class="hero-join" href="${escapeHtml(workbench.feedback.dispatches_href)}" target="_blank" rel="noreferrer">${escapeHtml(workbench.feedback.dispatches_label)}</a>`
    : `<span class="inert-link">${escapeHtml(workbench.feedback.dispatches_label)}</span>`;

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta http-equiv="Content-Security-Policy" content="default-src 'self'; style-src 'unsafe-inline';">
  <meta name="generator" content="scripts/workbench.mjs">
  <title>${escapeHtml(workbench.title)}</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #06090c;
      --paper: #0b1216;
      --panel: #111b21;
      --panel-2: #16242c;
      --line: #1e2e35;
      --line-bright: rgba(255, 255, 255, 0.15);
      --ink: #f4fbf8;
      --muted: #a4b9b4;
      --green: #4ade80;
      --green-bg: rgba(74, 222, 128, 0.14);
      --green-glow: rgba(74, 222, 128, 0.28);
      --amber: #fbbf24;
      --amber-bg: rgba(251, 191, 36, 0.14);
      --amber-glow: rgba(251, 191, 36, 0.28);
      --red: #f87171;
      --red-bg: rgba(248, 113, 113, 0.14);
      --blue: #38bdf8;
      --blue-bg: rgba(56, 189, 248, 0.14);
      --blue-glow: rgba(56, 189, 248, 0.28);
      --violet: #c084fc;
      --violet-bg: rgba(192, 132, 252, 0.14);
      --wood: #e5b278;
      --wood-bg: rgba(229, 178, 120, 0.14);
      --shadow-lg: 0 24px 60px rgba(0, 0, 0, 0.6);
      --shadow-card: 0 14px 36px rgba(0, 0, 0, 0.4);
      --sans: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      --mono: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
    }
    * { box-sizing: border-box; }
    html { scroll-behavior: smooth; }
    body {
      margin: 0;
      background: radial-gradient(circle at 50% -10%, #194037 0%, #0d1a18 35%, var(--bg) 80%);
      color: var(--ink);
      font-family: var(--sans);
      line-height: 1.6;
      -webkit-font-smoothing: antialiased;
    }
    a { color: #38bdf8; text-underline-offset: .25em; transition: color 0.15s ease; }
    a:hover { color: #bae6fd; }
    code { font-family: var(--mono); color: #eaf6f2; overflow-wrap: anywhere; background: rgba(0, 0, 0, 0.4); padding: 2px 7px; border-radius: 5px; border: 1px solid rgba(255, 255, 255, 0.08); font-size: 0.88em; }
    .wrap { width: min(1200px, calc(100% - 36px)); margin: 0 auto; }
    .tool, .ladder-step, section[id] { scroll-margin-top: 84px; }
    summary:focus-visible, a:focus-visible { outline: 2px solid var(--wood); outline-offset: 3px; border-radius: 4px; }

    /* Glassmorphic Navigation Bar */
    .topbar { position: sticky; top: 0; z-index: 100; border-bottom: 1px solid rgba(255, 255, 255, 0.09); background: rgba(6, 9, 12, 0.88); backdrop-filter: blur(20px) saturate(150%); }
    .topbar-inner { min-height: 64px; display: flex; align-items: center; justify-content: space-between; gap: 20px; }
    .brand { display: flex; align-items: center; gap: 12px; font-weight: 800; letter-spacing: -0.01em; font-size: 1.08rem; }
    .mark { width: 32px; height: 32px; display: grid; place-items: center; border: 1px solid var(--wood); color: var(--wood); border-radius: 8px; font-family: var(--mono); font-weight: 800; font-size: 0.88rem; background: var(--wood-bg); box-shadow: 0 0 14px rgba(229, 178, 120, 0.2); }
    nav { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 16px; font-size: 0.9rem; font-weight: 500; }
    nav a { color: var(--muted); text-decoration: none; padding: 5px 10px; border-radius: 6px; transition: all 0.15s ease; }
    nav a:hover { color: #ffffff; background: rgba(255, 255, 255, 0.07); }
    nav a[aria-current="page"] { color: #ffffff; background: rgba(255, 255, 255, 0.1); font-weight: 700; }

    /* Hero Section */
    .hero { padding: 54px 0 32px; }
    .eyebrow { display: inline-flex; align-items: center; gap: 8px; padding: 6px 16px; border-radius: 999px; background: rgba(229, 178, 120, 0.12); border: 1px solid rgba(229, 178, 120, 0.3); color: var(--wood); font-size: 0.78rem; font-weight: 800; text-transform: uppercase; letter-spacing: 0.15em; margin-bottom: 16px; box-shadow: 0 2px 12px rgba(0, 0, 0, 0.3); }
    h1 { max-width: 960px; margin: 6px 0 18px; font-size: clamp(2.4rem, 5.2vw, 3.9rem); line-height: 1.08; letter-spacing: -0.04em; font-weight: 900; background: linear-gradient(135deg, #ffffff 0%, #d1f5ec 50%, #4ade80 100%); -webkit-background-clip: text; -webkit-text-fill-color: transparent; }
    .headline { max-width: 920px; color: #fde047; font-family: var(--mono); font-size: clamp(0.94rem, 1.6vw, 1.1rem); font-weight: 700; line-height: 1.6; }
    .invitation { max-width: 940px; margin: 20px 0 18px; color: #86efac; font-size: 1.05rem; font-weight: 600; line-height: 1.55; }
    .hero-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; margin: 24px 0 20px; }
    .hero-cta { display: inline-flex; align-items: center; gap: 8px; padding: 13px 24px; border-radius: 10px; color: #ffffff; background: linear-gradient(135deg, #16a34a, #15803d); border: 1px solid #4ade80; font-weight: 800; font-size: 0.98rem; text-decoration: none; box-shadow: 0 4px 22px rgba(74, 222, 128, 0.35); transition: all 0.2s ease; }
    .hero-cta:hover { transform: translateY(-2px); box-shadow: 0 8px 28px rgba(74, 222, 128, 0.5); color: #ffffff; text-decoration: none; }
    .hero-join { display: inline-flex; align-items: center; gap: 8px; padding: 13px 24px; border-radius: 10px; color: #e9d5ff; background: var(--violet-bg); border: 1px solid rgba(192, 132, 252, 0.4); font-weight: 800; font-size: 0.98rem; text-decoration: none; transition: all 0.2s ease; }
    .hero-join:hover { color: #ffffff; border-color: #c084fc; background: rgba(192, 132, 252, 0.25); text-decoration: none; }
    .join-path { max-width: 960px; margin: 0 0 18px; color: var(--muted); font-size: 0.88rem; line-height: 1.55; background: rgba(0, 0, 0, 0.3); padding: 14px 18px; border-radius: 10px; border: 1px solid rgba(255, 255, 255, 0.08); }
    .join-path .hero-join { padding: 3px 12px; border-radius: 999px; font-size: 0.8rem; border-width: 1px; }
    .join-path strong { color: var(--ink); }
    .hero-meta { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 18px; }
    .hero-meta span, .hero-meta a { border: 1px solid rgba(255, 255, 255, 0.09); border-radius: 999px; padding: 8px 16px; color: var(--muted); background: rgba(255, 255, 255, 0.04); font-size: 0.84rem; font-weight: 500; backdrop-filter: blur(8px); }
    .hero-meta a { color: var(--blue); text-decoration: none; transition: all 0.15s ease; }
    .hero-meta a:hover { border-color: var(--blue); background: var(--blue-bg); color: #ffffff; }

    /* Sections Layout */
    section { padding: 44px 0 54px; }
    section + section { border-top: 1px solid rgba(255, 255, 255, 0.07); }
    .section-head { display: grid; grid-template-columns: minmax(180px, 0.32fr) 1fr; gap: 34px; margin-bottom: 26px; align-items: start; }
    .section-number { color: var(--wood); font-family: var(--mono); font-size: 0.84rem; font-weight: 800; letter-spacing: 0.16em; padding-top: 4px; }
    h2 { margin: 0 0 10px; font-size: clamp(1.65rem, 3.4vw, 2.55rem); letter-spacing: -0.03em; font-weight: 800; color: #ffffff; }
    h3 { margin: 0; line-height: 1.3; font-weight: 700; }
    .section-copy { color: var(--muted); max-width: 840px; font-size: 1rem; }
    ul { margin: 10px 0 0; padding-left: 1.2rem; }
    li { margin: 6px 0; }

    /* Status Pills */
    .pill { display: inline-flex; align-items: center; flex: 0 0 auto; padding: 4px 11px; border-radius: 999px; font-family: var(--mono); font-size: 0.7rem; font-weight: 900; letter-spacing: 0.07em; border: 1px solid currentColor; white-space: nowrap; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.25); }
    .pill.live { color: #4ade80; background: var(--green-bg); border-color: rgba(74, 222, 128, 0.4); }
    .pill.dev-only { color: #38bdf8; background: var(--blue-bg); border-color: rgba(56, 189, 248, 0.4); }
    .pill.local-only { color: #fbbf24; background: var(--amber-bg); border-color: rgba(251, 191, 36, 0.4); }
    .pill.recoverable-not-running { color: #f87171; background: var(--red-bg); border-color: rgba(248, 113, 113, 0.4); }
    .pill.ownership { font-size: 0.68rem; }
    .pill.ownership.unclaimed { color: var(--muted); background: rgba(164, 185, 180, 0.12); border-color: rgba(164, 185, 180, 0.25); }
    .pill.ownership.trying { color: #fbbf24; background: var(--amber-bg); border-color: rgba(251, 191, 36, 0.4); }
    .pill.ownership.claimed { color: #38bdf8; background: var(--blue-bg); border-color: rgba(56, 189, 248, 0.4); }
    .pill.ownership.owned { color: #4ade80; background: var(--green-bg); border-color: rgba(74, 222, 128, 0.4); }
    .inert-link { color: #8ba39e; font-style: italic; }

    .not-a-verdict { max-width: 960px; margin: 8px 0 0; padding: 16px 20px; border-left: 4px solid var(--wood); border-radius: 0 12px 12px 0; background: rgba(229, 178, 120, 0.09); border-top: 1px solid rgba(229, 178, 120, 0.18); border-right: 1px solid rgba(229, 178, 120, 0.18); border-bottom: 1px solid rgba(229, 178, 120, 0.18); color: #ebe3d5; font-size: 0.94rem; }
    .not-a-verdict > summary { cursor: pointer; color: var(--wood); font-weight: 700; outline: none; }
    .not-a-verdict > p { margin: 10px 0 0; }

    /* Audience lenses: tailored tables of contents, never hidden content. */
    .lens-jump { display: flex; flex-wrap: wrap; justify-content: flex-start; gap: 8px; margin-bottom: 20px; }
    .lens-jump a { padding: 7px 12px; border: 1px solid rgba(229, 178, 120, 0.28); border-radius: 999px; color: var(--wood); background: var(--wood-bg); font-family: var(--mono); font-size: 0.72rem; font-weight: 800; text-decoration: none; }
    .lens-jump a:hover { border-color: var(--wood); color: #ffffff; }
    .lens-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
    .lens { scroll-margin-top: 84px; display: flex; flex-direction: column; gap: 9px; padding: 20px; border: 1px solid rgba(255, 255, 255, 0.1); border-radius: 14px; background: linear-gradient(165deg, rgba(22, 36, 44, 0.9), rgba(13, 20, 25, 0.96)); }
    .lens:target { border-color: var(--wood); box-shadow: 0 0 0 3px var(--wood-bg); }
    .lens-kicker { color: var(--wood); font-family: var(--mono); font-size: 0.7rem; font-weight: 900; letter-spacing: 0.1em; text-transform: uppercase; }
    .lens h3 { color: #ffffff; font-size: 1.04rem; }
    .lens p { margin: 0; color: var(--muted); font-size: 0.86rem; }
    .lens-toc { display: flex; flex-wrap: wrap; justify-content: flex-start; gap: 6px; margin-top: 3px; }
    .lens-toc a { padding: 4px 8px; border-radius: 5px; background: rgba(56, 189, 248, 0.1); color: var(--blue); font-size: 0.75rem; text-decoration: none; }
    .lens-more { margin-top: auto; padding-top: 5px; color: var(--wood); font-size: 0.78rem; font-weight: 700; }
    .outside-lane { margin: 18px 0 0; padding: 13px 17px; border-left: 3px solid var(--violet); color: var(--muted); background: var(--violet-bg); border-radius: 0 9px 9px 0; }

    /* Tool Index Quick Bar */
    .tool-index { display: grid; gap: 7px; margin-bottom: 26px; }
    .index-row { display: grid; grid-template-columns: minmax(0, 180px) minmax(0, 1.1fr) minmax(0, 1fr) minmax(0, 84px); align-items: center; gap: 16px; padding: 11px 18px; border: 1px solid rgba(255, 255, 255, 0.08); border-left: 4px solid var(--line); border-radius: 11px; background: rgba(17, 27, 33, 0.75); backdrop-filter: blur(10px); color: var(--ink); text-decoration: none; transition: all 0.15s ease; }
    .index-row:hover { background: rgba(255, 255, 255, 0.07); transform: translateX(4px); text-decoration: none; color: #ffffff; border-color: rgba(255, 255, 255, 0.18); }
    .index-row.live { border-left-color: var(--green); }
    .index-row.dev-only { border-left-color: var(--blue); }
    .index-row.local-only { border-left-color: var(--amber); }
    .index-row.recoverable-not-running { border-left-color: var(--red); }
    .index-row .pill { justify-self: start; }
    .index-name { font-weight: 700; font-size: 0.94rem; color: #ffffff; }
    .index-time { color: var(--muted); font-size: 0.82rem; }
    .index-tasks { justify-self: end; color: var(--wood); font-family: var(--mono); font-size: 0.78rem; font-weight: 800; white-space: nowrap; }

    /* Participation Ladder Flow */
    .ladder { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 14px; margin-top: 18px; }
    .ladder-step { position: relative; min-width: 0; display: grid; align-content: start; gap: 10px; padding: 20px 18px; border: 1px solid rgba(255, 255, 255, 0.09); border-top: 4px solid var(--blue); border-radius: 14px; background: linear-gradient(165deg, rgba(22, 36, 44, 0.92), rgba(13, 20, 25, 0.96)); box-shadow: var(--shadow-card); transition: transform 0.2s ease, border-color 0.2s ease; }
    .ladder-step:hover { transform: translateY(-3px); border-color: rgba(255, 255, 255, 0.2); }
    .ladder-step.stage-0 { border-top-color: var(--muted); }
    .ladder-step.stage-3 { border-top-color: var(--violet); }
    .ladder-step.stage-4 { border-top-color: var(--green); }
    .ladder-step:not(:last-child)::after { content: "→"; position: absolute; right: -12px; top: 50px; z-index: 2; color: var(--wood); font-weight: 900; font-size: 1.15rem; text-shadow: 0 2px 10px rgba(0, 0, 0, 0.95); }
    .ladder-stage { color: var(--wood); font-family: var(--mono); font-size: 0.7rem; font-weight: 900; letter-spacing: 0.12em; text-transform: uppercase; }
    .ladder-step h3 { font-size: 1.08rem; color: #ffffff; }
    .ladder-did { margin: 0; color: #e2edea; font-size: 0.86rem; line-height: 1.5; }
    .ladder-get { padding-top: 12px; border-top: 1px solid rgba(255, 255, 255, 0.09); color: var(--muted); font-size: 0.8rem; }
    .ladder-get strong { display: block; color: var(--green); font-family: var(--mono); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.09em; margin-bottom: 3px; }
    .ladder-recorded { color: #8ba39e; font-family: var(--mono); font-size: 0.74rem; }

    /* Tool Showcase Cards */
    .tool-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 24px; align-items: start; }
    .tool { padding: 28px; border: 1px solid rgba(255, 255, 255, 0.1); border-top: 4px solid var(--blue); border-radius: 18px; background: linear-gradient(165deg, rgba(22, 36, 44, 0.96) 0%, rgba(12, 19, 24, 0.99) 100%); box-shadow: var(--shadow-lg); backdrop-filter: blur(14px); }
    .tool.live { border-top-color: var(--green); box-shadow: 0 18px 50px rgba(0, 0, 0, 0.55), inset 0 1px 0 rgba(74, 222, 128, 0.18); }
    .tool.dev-only { border-top-color: var(--blue); box-shadow: 0 18px 50px rgba(0, 0, 0, 0.55), inset 0 1px 0 rgba(56, 189, 248, 0.18); }
    .tool.local-only { border-top-color: var(--amber); box-shadow: 0 18px 50px rgba(0, 0, 0, 0.55), inset 0 1px 0 rgba(251, 191, 36, 0.18); }
    .tool.recoverable-not-running { border-top-color: var(--red); box-shadow: 0 18px 50px rgba(0, 0, 0, 0.55), inset 0 1px 0 rgba(248, 113, 113, 0.18); }
    .tool-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 14px; }
    .tool-head > div { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .tool-head h3 { font-size: 1.24rem; font-weight: 800; color: #ffffff; letter-spacing: -0.01em; }
    .tool-id { color: var(--wood); font-family: var(--mono); font-size: 0.74rem; font-weight: 800; padding: 3px 9px; border-radius: 6px; background: var(--wood-bg); border: 1px solid rgba(229, 178, 120, 0.3); }
    .one-liner { margin: 14px 0 0; color: #e2edea; font-size: 0.98rem; font-weight: 500; line-height: 1.55; }

    /* Honesty Status Detail Alert Box */
    .status-detail { padding: 15px 18px; border-radius: 12px; border-left: 4px solid var(--blue); background: var(--blue-bg); color: #e6f3f0; font-size: 0.9rem; line-height: 1.6; margin-top: 16px; border-top: 1px solid rgba(255, 255, 255, 0.07); border-right: 1px solid rgba(255, 255, 255, 0.07); border-bottom: 1px solid rgba(255, 255, 255, 0.07); }
    .status-detail strong { display: block; margin-bottom: 4px; color: var(--blue); font-family: var(--mono); font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.1em; font-weight: 800; }
    .status-detail.live { border-left-color: var(--green); background: var(--green-bg); }
    .status-detail.live strong { color: #4ade80; }
    .status-detail.local-only { border-left-color: var(--amber); background: var(--amber-bg); }
    .status-detail.local-only strong { color: #fbbf24; }
    .status-detail.recoverable-not-running { border-left-color: var(--red); background: var(--red-bg); }
    .status-detail.recoverable-not-running strong { color: #f87171; }

    .tool-audience { margin: 16px 0 0; color: #b0c4bf; font-size: 0.9rem; }
    .tool-audience strong { margin-right: 8px; color: var(--ink); font-family: var(--mono); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.09em; }

    /* Action Row & Buttons */
    .tool-action { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 14px; margin-top: 18px; padding-top: 16px; border-top: 1px solid rgba(255, 255, 255, 0.07); }
    .first-result { display: inline-flex; align-items: baseline; gap: 8px; color: #ffffff; font-size: 0.9rem; font-weight: 700; }
    .first-result em { color: var(--muted); font-family: var(--mono); font-size: 0.68rem; font-style: normal; text-transform: uppercase; letter-spacing: 0.09em; }
    .access-button { display: inline-block; padding: 11px 20px; border: 1px solid var(--green); border-radius: 9px; color: #ffffff; background: linear-gradient(135deg, #16a34a, #15803d); font-family: var(--sans); font-size: 0.86rem; font-weight: 800; text-decoration: none; white-space: nowrap; box-shadow: 0 4px 16px rgba(74, 222, 128, 0.25); transition: all 0.15s ease; }
    .access-button:hover { color: #ffffff; transform: translateY(-1px); box-shadow: 0 6px 20px rgba(74, 222, 128, 0.4); text-decoration: none; }
    .access-button.public-repo { border-color: rgba(56, 189, 248, 0.4); color: #38bdf8; background: var(--blue-bg); box-shadow: 0 4px 16px rgba(56, 189, 248, 0.18); }
    .access-button.public-repo:hover { color: #ffffff; background: rgba(56, 189, 248, 0.3); border-color: #38bdf8; }
    .access-button.live-service { border-color: rgba(251, 191, 36, 0.4); color: #fbbf24; background: var(--amber-bg); box-shadow: 0 4px 16px rgba(251, 191, 36, 0.18); }
    .access-button.live-service:hover { color: #ffffff; background: rgba(251, 191, 36, 0.3); border-color: #fbbf24; }
    .access-button.disabled { border-color: rgba(255, 255, 255, 0.09); color: #8ba39e; background: rgba(255, 255, 255, 0.04); box-shadow: none; cursor: not-allowed; }

    .access-verify { margin-top: 12px; padding: 12px 16px; border-radius: 9px; background: rgba(0, 0, 0, 0.25); border: 1px solid rgba(255, 255, 255, 0.06); }
    .access-note { margin: 0; color: var(--muted); font-size: 0.84rem; }
    .access-detail { display: flex; gap: 10px; margin-top: 6px; font-size: 0.76rem; align-items: center; }
    .access-detail span { flex: 0 0 78px; color: var(--muted); font-family: var(--mono); text-transform: uppercase; font-size: 0.68rem; letter-spacing: 0.07em; }

    .tool-link-row { display: flex; flex-wrap: wrap; gap: 8px 22px; margin-top: 16px; }
    .tool-link { display: inline-flex; align-items: baseline; gap: 8px; font-size: 0.86rem; }
    .tool-link em { color: var(--muted); font-family: var(--mono); font-size: 0.68rem; font-style: normal; text-transform: uppercase; letter-spacing: 0.09em; }

    .requires { margin-top: 16px; padding: 14px 17px; border: 1px solid rgba(255, 255, 255, 0.09); border-radius: 11px; background: rgba(0, 0, 0, 0.25); }
    .requires strong { display: block; color: var(--muted); font-family: var(--mono); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.09em; }
    .requires-list { margin: 6px 0 0; color: #e2edea; font-size: 0.88rem; }
    .requires-list li { margin: 5px 0; }

    /* Accordion Details */
    .tool-detail { margin-top: 16px; border: 1px solid rgba(255, 255, 255, 0.09); border-radius: 11px; background: rgba(0, 0, 0, 0.18); transition: border-color 0.15s ease; }
    .tool-detail:hover { border-color: rgba(255, 255, 255, 0.18); }
    .tool-detail > summary { padding: 11px 17px; cursor: pointer; color: var(--muted); font-family: var(--mono); font-size: 0.74rem; font-weight: 800; text-transform: uppercase; letter-spacing: 0.09em; outline: none; }
    .tool-detail > summary:hover { color: #ffffff; }
    .tool-detail[open] > summary { border-bottom: 1px solid rgba(255, 255, 255, 0.09); color: var(--wood); }
    .tool-detail-body { padding: 16px 17px; }
    .tool-detail-body > p { margin: 0 0 10px; color: #e2edea; font-size: 0.86rem; }
    .deps { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0; color: var(--muted); font-family: var(--mono); font-size: 0.74rem; text-transform: uppercase; }
    .dep { padding: 2px 8px; background: rgba(255, 255, 255, 0.09); border-radius: 4px; color: var(--ink); border: 1px solid rgba(255, 255, 255, 0.07); }
    .tool-links { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px 10px; margin-bottom: 10px; font-size: 0.86rem; }
    .tool-links strong { flex: 0 0 84px; color: var(--muted); font-family: var(--mono); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.09em; }
    .access-policy { margin: 10px 0 0; color: #e2edea; font-size: 0.84rem; }
    .source-note { margin: 10px 0 0; color: var(--muted); font-size: 0.84rem; }
    .first-tasks { margin-top: 18px; padding: 18px; border-radius: 14px; border-left: 4px solid var(--wood); background: rgba(229, 178, 120, 0.07); border: 1px solid rgba(229, 178, 120, 0.2); border-left-width: 4px; }
    .first-tasks > strong { color: var(--wood); font-family: var(--mono); font-size: 0.74rem; text-transform: uppercase; letter-spacing: 0.11em; }
    .first-task-list { list-style: none; margin: 12px 0 0; padding: 0; display: grid; gap: 11px; }
    .first-task { display: grid; gap: 6px; padding: 14px 16px; border: 1px solid rgba(255, 255, 255, 0.09); border-radius: 10px; background: rgba(0, 0, 0, 0.3); }
    .first-task.suggested { border-color: rgba(74, 222, 128, 0.45); background: rgba(74, 222, 128, 0.09); box-shadow: 0 2px 14px rgba(74, 222, 128, 0.12); }
    .first-task-top { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; }
    .first-task strong { font-size: 0.96rem; color: #ffffff; }
    .task-flags { display: flex; flex: 0 0 auto; align-items: center; gap: 6px; }
    .task-suggested { padding: 3px 9px; border-radius: 999px; color: #4ade80; background: var(--green-bg); border: 1px solid rgba(74, 222, 128, 0.35); font-family: var(--mono); font-size: 0.65rem; font-weight: 800; text-transform: uppercase; white-space: nowrap; }
    .task-id { color: var(--wood); font-family: var(--mono); font-size: 0.74rem; font-weight: 800; }
    .task-size { padding: 2px 8px; border-radius: 999px; color: var(--muted); background: rgba(255, 255, 255, 0.07); font-family: var(--mono); font-size: 0.65rem; text-transform: uppercase; font-weight: 700; }
    .task-size.small { color: #4ade80; background: var(--green-bg); }
    .task-size.medium { color: #fbbf24; background: var(--amber-bg); }
    .task-blocked { padding: 3px 9px; border-radius: 999px; color: #f87171; background: var(--red-bg); border: 1px solid rgba(248, 113, 113, 0.35); font-family: var(--mono); font-size: 0.65rem; font-weight: 800; text-transform: uppercase; white-space: nowrap; }
    .task-blocked-reason { color: #f87171; font-size: 0.84rem; }
    .task-blocked-reason em { color: var(--ink); font-style: normal; font-weight: 700; }
    .done-when { color: var(--muted); font-size: 0.84rem; }
    .done-when em { color: #ffffff; font-style: normal; font-weight: 700; }
    .recovery { margin-top: 16px; padding: 16px 18px; border-left: 4px solid var(--red); border-radius: 0 12px 12px 0; background: var(--red-bg); border: 1px solid rgba(248, 113, 113, 0.22); border-left-width: 4px; }
    .recovery > strong { color: #f87171; font-family: var(--mono); font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.09em; }
    .recovery p { margin: 6px 0 0; color: #eedcdb; font-size: 0.84rem; }
    .recovery-origin { font-family: var(--mono); font-size: 0.78rem; overflow-wrap: anywhere; }
    .recovery-paths { margin: 8px 0 0; padding-left: 1.15rem; color: var(--muted); font-size: 0.8rem; }
    .recovery-paths li { margin: 4px 0; }
    .ownership-state { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-top: 14px; padding: 11px 16px; border: 1px solid rgba(255, 255, 255, 0.09); border-radius: 10px; background: rgba(0, 0, 0, 0.25); color: var(--muted); font-size: 0.84rem; }
    .ownership-record { color: #8ba39e; font-family: var(--mono); font-size: 0.74rem; }
    .stage-3-right { margin: 12px 0 0; color: var(--muted); font-size: 0.84rem; }
    .stage-3-right strong { margin-right: 8px; color: var(--violet); font-family: var(--mono); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.09em; }
    .tool-footnotes { display: grid; gap: 8px; margin-top: 14px; font-size: 0.84rem; color: var(--muted); }
    .tool-footnotes strong { margin-right: 8px; color: var(--ink); font-family: var(--mono); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.09em; }
    .privacy { padding: 12px 16px; border-left: 4px solid var(--violet); border-radius: 0 10px 10px 0; background: var(--violet-bg); border: 1px solid rgba(192, 132, 252, 0.22); border-left-width: 4px; }

    /* Footer */
    footer { padding: 44px 0 74px; color: var(--muted); font-size: 0.88rem; border-top: 1px solid rgba(255, 255, 255, 0.07); margin-top: 44px; }
    .feedback-rhythm { margin-bottom: 16px; padding: 16px 20px; border-left: 4px solid var(--blue); border-radius: 0 12px 12px 0; background: var(--blue-bg); border: 1px solid rgba(56, 189, 248, 0.22); border-left-width: 4px; color: #e2edea; }
    .print-note { display: none; margin-bottom: 16px; padding: 14px 18px; border-left: 4px solid var(--wood); color: #ebe3d5; font-size: 0.88rem; }
    .generated { color: #8ba39e; font-family: var(--mono); font-size: 0.78rem; margin-top: 12px; }
    .baseline-provenance { margin-top: 18px; color: #8ba39e; font-family: var(--mono); font-size: 0.74rem; letter-spacing: 0.02em; }
    .baseline-provenance a { color: inherit; text-decoration: none; border-bottom: 1px dotted currentColor; }

    @media (max-width: 900px) {
      .section-head { grid-template-columns: 1fr; gap: 14px; }
      .tool-grid { grid-template-columns: 1fr; }
      .lens-grid { grid-template-columns: 1fr; }
      .index-row { grid-template-columns: minmax(0, 150px) minmax(0, 1fr); row-gap: 6px; }
      .ladder { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .ladder-step::after { display: none; }
      .topbar-inner { align-items: flex-start; padding: 12px 0; }
      nav { gap: 10px; }
      .hero { padding-top: 36px; }
    }
    @media (max-width: 600px) {
      .wrap { width: min(1200px, calc(100% - 24px)); }
      .topbar-inner { display: block; }
      nav { justify-content: flex-start; margin-top: 10px; }
      .ladder { grid-template-columns: 1fr; }
      .tool-head { display: grid; }
      .tool-links strong { flex: 1 1 100%; }
      .index-row { grid-template-columns: 1fr; }
      .index-tasks { justify-self: start; }
      .tool-action { justify-content: flex-start; }
      .access-button { white-space: normal; width: 100%; text-align: center; }
    }
    @media print {
      :root { color-scheme: light; --bg: #fff; --paper: #fff; --panel: #f6f7f7; --panel-2: #eee; --line: #ccd2d0; --ink: #15201d; --muted: #53635e; }
      body { background: #fff; }
      .topbar { position: static; background: #fff; }
      .tool, .ladder-step { break-inside: avoid; box-shadow: none; }
      nav, .tool-index { display: none; }
      a { color: #075985; }
      details > summary { list-style: none; font-weight: 700; }
      .print-note { display: block; }
      .tool-detail > summary { border-bottom: 1px solid var(--line); }
    }
  </style>
</head>
<body>
  <!-- GENERATED FILE: edit docs/workbench/workbench.json and run npm run workbench:render -->
  <header class="topbar">
    <div class="wrap topbar-inner">
      <div class="brand"><span class="mark">LJ</span><span>Community Workbench</span></div>
      <nav aria-label="Gateway surfaces">
        <a href="/roadmap">Roadmap</a>
        <a href="/community">Community</a>
        <a href="/workbench" aria-current="page">Workbench</a>
        <a href="/questpicker">Quest Picker</a>
        <a href="/steward">Steward</a>
        <a href="/questlab">Quest Lab</a>
        <a href="/networksense">NetworkSense</a>
        <a href="/events">Events</a>
        <a href="/testing">Testing</a>
      </nav>
    </div>
  </header>

  <main>
    <div class="wrap hero">
      <div class="eyebrow">✦ Comfy × Valheim · Lumberjacks P7</div>
      <h1>${escapeHtml(workbench.title.replace('Comfy × Valheim — ', ''))}</h1>
      <div class="headline">${escapeHtml(workbench.headline)}</div>
      <p class="invitation">${escapeHtml(workbench.invitation_line)}</p>
      <details class="not-a-verdict">
        <summary>${escapeHtml(workbench.not_a_verdict_summary)}</summary>
        <p>${escapeHtml(workbench.not_a_verdict)}</p>
      </details>
      <div class="hero-actions">
        <a class="hero-cta" href="#audiences">Choose your view</a>
        ${invite}
        ${dispatches}
      </div>
      <p class="join-path">The path in: join → read ${startHere} → post in a tool's thread. Those last two and the ${forum} are <strong>member-only</strong> Discord links; they will not open until you have joined.</p>
      <div class="hero-meta">
        <span class="freshness">${escapeHtml(freshness)}</span>
        <span>${escapeHtml(summary)}</span>
        <a href="#tools">${escapeHtml(String(counts.actionable))} first tasks open${counts.blocked > 0 ? ` · ${escapeHtml(String(counts.blocked))} blocked` : ''}</a>
      </div>
    </div>

    <section id="audiences">
      <div class="wrap">
        <div class="section-head"><div class="section-number">00 · YOUR VIEW</div><div><h2>Start with the question you brought.</h2><p class="section-copy">Each lens is a table of contents over the same underlying catalog. Pick more than one, or ignore them and explore everything; no record is hidden because it sits outside a chosen lane.</p></div></div>
        ${audienceLenses}
      </div>
    </section>

    <section id="tools">
      <div class="wrap">
        <div class="section-head"><div class="section-number">01 · THE TOOLS</div><div><h2>${escapeHtml(workbench.honesty_statement)}</h2><p class="section-copy">A status chip here is never a mood. It is one of four declared states, and every one of them is qualified in prose by the person who wrote the code. Where something does not run, the card says so and points at where the pieces are.</p></div></div>
        ${toolIndex}
        <div class="tool-grid">${tools}</div>
      </div>
    </section>

    <section id="ladder">
      <div class="wrap">
        <div class="section-head"><div class="section-number">02 · THE LADDER</div><div><h2>Five rungs, and you can stop on any of them.</h2><p class="section-copy">Nobody starts as an owner. Each rung says what you did, what you get for it, and where it is written down. Stopping at stage 1 is a complete and finished contribution.</p></div></div>
        ${ladder}
      </div>
    </section>
  </main>

  <footer class="wrap">
    <div class="feedback-rhythm">${escapeHtml(workbench.feedback.rhythm)} Threads live in the ${forum}, which needs a server membership — ${invite} first if you have not joined.</div>
    <p class="print-note">Printed copy: each card's <strong>How it works, in detail</strong> and <strong>Source, privacy &amp; licence</strong> sections are collapsed and do not print — no stylesheet can force them open. Expand them in a browser to read them. Everything a decision rests on prints: the status and what it means, what you will need, download digests, the first tasks, where recoverable pieces are, and what stage 3 grants for that tool.</p>
    <div>If a card on this page is wrong, that is the most useful bug report you can file — the whole point is that the status matches reality.</div>
    <div class="generated">Generated deterministically from ${escapeHtml(workbenchRelative)} · do not hand-edit this file.</div>
    <nav class="baseline-provenance" aria-label="Project provenance"><a href="https://github.com/djcdevelopment/baseline" target="_blank" rel="noreferrer">Baseline</a><span aria-hidden="true"> · </span><a href="https://github.com/djcdevelopment/Lumberjacks" target="_blank" rel="noreferrer">Lumberjacks</a><span aria-hidden="true"> · </span><a href="https://github.com/djcdevelopment/comfy" target="_blank" rel="noreferrer">Comfy</a><span aria-hidden="true"> · </span><a href="${escapeHtml(licensingHref)}" target="_blank" rel="noreferrer">license details</a></nav>
  </footer>
</body>
</html>
`;
}

function writeRendered(workbench) {
  const html = render(workbench);
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, html, 'utf8');
  return html;
}

function check(args) {
  for (const arg of args) fail(`unknown check option ${arg}`);
  const { workbench, raw } = readSource();
  validate(workbench, raw);

  const prov = provenance();
  if (prov.mode === 'no-git') {
    fail('check requires a git checkout — the provenance stamp cannot be verified without one');
  }

  const expected = render(workbench);
  let actual;
  try {
    actual = fs.readFileSync(outputPath, 'utf8');
  } catch {
    fail(`${outputRelative} is missing; run npm run workbench:render`);
  }
  if (normaliseFreshness(actual) !== normaliseFreshness(expected)) {
    fail(`${outputRelative} is stale; run npm run workbench:render`);
  }
  if (!freshnessMarker.test(actual)) fail('the generated page is missing its derived freshness stamp');

  // The stamp itself is part of the contract, asymmetrically. A clean tree renders
  // deterministically, so the artifact must carry exactly the production stamp a fresh render
  // would produce — a preview stamp surviving into a clean tree is a published false claim
  // (this page once told every visitor it came from an uncommitted working tree while the
  // source sat committed). The inverse fails too: a production stamp over dirty inputs names
  // a commit that does not contain what is on screen.
  if (prov.mode === 'production') {
    if (/Preview rendered |from an uncommitted working tree/.test(actual)) {
      fail(`${outputRelative} carries a preview stamp but the provenance inputs are clean — run npm run workbench:render so the published page names its source commit`);
    }
    if (actual !== expected) {
      fail(`${outputRelative} provenance stamp is out of date; run npm run workbench:render`);
    }
  } else if (/Published from /.test(actual)) {
    fail(`${outputRelative} carries a published provenance stamp but the provenance inputs have uncommitted changes — commit the inputs and re-render, or render the preview honestly`);
  }

  if (!actual.includes('id="audiences"') || !actual.includes('id="ladder"') || !actual.includes('id="tools"') || !actual.includes('01 · THE TOOLS')) {
    fail('generated workbench is missing a required section');
  }
  // The Gateway serves this file verbatim under a self-only CSP; a <script> or <link> would
  // either be blocked or, worse, turn a static catalog into a page that fetches something.
  if (/<(?:script|link)\b/i.test(actual)) fail('workbench HTML must remain self-contained and script-free');

  // Progressive disclosure is for depth, never for the honesty. A status_detail behind a
  // <summary> is a status chip a reader can take at face value without ever seeing what
  // qualifies it — which is the exact failure this page exists to prevent.
  if (/<details[^>]*>(?:(?!<\/details>)[\s\S])*?status-detail/.test(actual)) {
    fail('status_detail must never render inside a <details> — it is the load-bearing honesty of this page');
  }
  const detailCount = (actual.match(/class="status-detail/g) ?? []).length;
  if (detailCount !== workbench.tools.length) {
    fail(`expected ${workbench.tools.length} status_detail blocks, found ${detailCount}`);
  }
  // Same argument for the two facts a volunteer uses to rule themselves out, and for the
  // digest that makes a download verifiable before anyone runs it.
  if (/<details[^>]*>(?:(?!<\/details>)[\s\S])*?(?:requires-list|access-verify)/.test(actual)) {
    fail('requirements and download digests must stay visible — they are gates, not depth');
  }

  // A rendered member-only Discord URL with no rendered invite strands every first-time visitor.
  const memberOnlyRendered = (actual.match(/https:\/\/discord\.com\/channels\//g) ?? []).length;
  const invitesRendered = (actual.match(/https:\/\/discord\.gg\//g) ?? []).length;
  if (memberOnlyRendered > 0 && invitesRendered === 0) {
    fail(`${memberOnlyRendered} member-only Discord link(s) rendered with no invite link on the page`);
  }

  // Every promise of a named repository document has to be a link the reader can open. Count
  // text nodes only: the href itself ends in OWNERS.md, so counting raw occurrences would score
  // each correctly-linked mention twice.
  const renderedText = actual.replace(/<[^>]*>/g, ' ');
  const ownersNamed = (renderedText.match(/OWNERS\.md/g) ?? []).length;
  const ownersLinked = (actual.match(/<a [^>]*>OWNERS\.md<\/a>/g) ?? []).length;
  if (ownersNamed !== ownersLinked) {
    fail(`OWNERS.md is named ${ownersNamed} time(s) but linked only ${ownersLinked} — a promised document must be reachable`);
  }
  const licensingNamed = (renderedText.match(/LICENSING\.md/g) ?? []).length;
  const licensingLinked = (actual.match(/<a [^>]*>LICENSING\.md<\/a>/g) ?? []).length;
  if (licensingNamed !== licensingLinked) {
    fail(`LICENSING.md is named ${licensingNamed} time(s) but linked only ${licensingLinked} — a promised document must be reachable`);
  }

  // The ladder defers to the cards, so every card must actually state its own right.
  for (const tool of workbench.tools) {
    if (!actual.includes(escapeHtml(tool.contribution.stage_3_reward))) {
      fail(`${tool.id}: contribution.stage_3_reward is not rendered on the page`);
    }
  }

  // The invite the page shows must be the invite the Discord side recorded. Skipped when the
  // state file is absent so a checkout without the tools tree still builds.
  const statePath = path.join(repoRoot, provisionStateRelative);
  if (fs.existsSync(statePath)) {
    const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    if (state.invite_href && state.invite_href !== workbench.feedback.invite_href) {
      fail(`invite drift: workbench.json has ${workbench.feedback.invite_href}, provision-state.json has ${state.invite_href}`);
    }
    // An expiring invite is the one failure no other guard can see: the href stays well-formed
    // and allowlisted forever while the destination quietly stops existing, and the page's only
    // entry point for a non-member dies with it. Recording the date turns that into something
    // the build can say out loud. Hand-maintained, so it fails safe: a regenerated invite whose
    // date was not updated warns early rather than late. null means a non-expiring invite.
    if (state.invite_expires_at !== null && state.invite_expires_at !== undefined) {
      const expiresAt = Date.parse(state.invite_expires_at);
      if (Number.isNaN(expiresAt)) fail(`provision-state.json invite_expires_at is not a timestamp: ${state.invite_expires_at}`);
      const daysLeft = Math.floor((expiresAt - Date.now()) / 86_400_000);
      if (daysLeft <= 0) {
        fail(`the Discord invite expired ${state.invite_expires_at} — the page's only way in for a non-member is dead. Regenerate it as a never-expiring invite, then update workbench.json and provision-state.json.`);
      }
      if (daysLeft <= 14) {
        console.warn(`WARNING: the Discord invite expires in ${daysLeft} day(s) (${state.invite_expires_at}). Regenerate it as never-expiring before it strands every first-time visitor.`);
      }
    }
  }

  const unclaimed = workbench.tools.filter((tool) => tool.ownership.state === 'unclaimed').length;
  console.log(`Workbench OK: ${workbench.tools.length} tools (${statusSummary(workbench.tools)}), ${unclaimed} unclaimed, generated HTML current.`);
}

function renderCommand(args) {
  for (const arg of args) fail(`unknown render option ${arg}`);
  const { workbench, raw } = readSource();
  validate(workbench, raw);
  writeRendered(workbench);
  console.log(`Rendered ${outputRelative} from ${workbenchRelative}`);
}

function usage() {
  console.log(`Usage:
  node scripts/workbench.mjs render
  node scripts/workbench.mjs check

render stamps provenance automatically: clean inputs produce "Published from <sha7> · <date>"
naming the last commit that touched docs/workbench/workbench.json or scripts/workbench.mjs;
uncommitted inputs produce a "Preview rendered ..." stamp that can never be published. The
flow for a content change is therefore a pair: commit the inputs, render, commit the HTML.

The workbench has no note/journal command: docs/workbench/workbench.json is the whole source
of truth, and its history is the file's own diff.`);
}

export {
  provenance,
  provenanceInputs,
  formatUtc,
  freshnessMarker,
  normaliseFreshness,
  readSource,
  validate,
  render,
  check,
  writeRendered,
  statusSummary,
  escapeHtml,
  resolveTaskCompletion,
  computeTaskCounts,
  workbenchRelative,
  outputRelative,
  workbenchPath,
  outputPath,
};

// Importing this module (the test suite does) must not run the CLI; only a direct
// `node scripts/workbench.mjs <command>` invocation dispatches. Windows paths can differ in
// drive-letter case between argv and import.meta.url, so compare case-insensitively there.
const cliPath = process.argv[1] ? path.resolve(process.argv[1]) : '';
const selfPath = fileURLToPath(import.meta.url);
const invokedAsCli = process.platform === 'win32'
  ? cliPath.toLowerCase() === selfPath.toLowerCase()
  : cliPath === selfPath;

if (invokedAsCli) {
  try {
    const [command, ...args] = process.argv.slice(2);
    if (command === 'render') renderCommand(args);
    else if (command === 'check') check(args);
    else {
      usage();
      process.exitCode = command ? 1 : 0;
    }
  } catch (error) {
    console.error(`workbench: ${error.message}`);
    process.exitCode = 1;
  }
}
