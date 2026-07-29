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

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workbenchRelative = 'docs/workbench/workbench.json';
const outputRelative = 'src/Game.Gateway/Community/workbench.html';
const workbenchPath = path.join(repoRoot, workbenchRelative);
const outputPath = path.join(repoRoot, outputRelative);

const statuses = new Set(['live', 'dev-only', 'local-only', 'recoverable-not-running']);
const accessKinds = new Set(['site-download', 'public-repo', 'live-service', 'not-published']);
const sourceKinds = new Set(['public-repo', 'private-until-claimed']);
const taskSizes = new Set(['small', 'medium']);
const ownershipStates = new Set(['unclaimed', 'trying', 'claimed', 'owned']);

// Every href that reaches the rendered page must start with one of these. Anything else degrades
// to inert text rather than becoming a live link on a public surface.
const linkPrefixes = [
  'https://github.com/djcdevelopment/',
  'https://discord.com/channels/',
  'https://comfy-p7.duckdns.org/',
];

function fail(message) {
  throw new Error(message);
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

function validateTool(tool, index, seenIds) {
  const label = `workbench.tools[${index}]`;
  requireObject(tool, label);
  requireString(tool.id, `${label}.id`);
  if (!/^[a-z0-9][a-z0-9-]*$/.test(tool.id)) fail(`${label}.id must be a lowercase slug: ${tool.id}`);
  if (seenIds.has(tool.id)) fail(`duplicate tool id ${tool.id}`);
  seenIds.add(tool.id);

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
  });

  validateOwnership(tool.ownership, `${label}.ownership`);
  validateRecovery(tool.recovery ?? null, `${label}.recovery`, tool.status);
  requireStringArray(tool.roadmap_milestones, `${label}.roadmap_milestones`);
  requireString(tool.privacy_note, `${label}.privacy_note`);
  requireString(tool.license, `${label}.license`);
}

function validate(workbench, rawText = '') {
  if (workbench.schema_version !== 1) fail('workbench.schema_version must be 1');
  requireString(workbench.title, 'workbench.title');
  requireString(workbench.headline, 'workbench.headline');
  requireString(workbench.updated_at, 'workbench.updated_at');
  if (Number.isNaN(Date.parse(workbench.updated_at))) fail('workbench.updated_at must be an ISO timestamp');
  requireString(workbench.honesty_statement, 'workbench.honesty_statement');
  requireString(workbench.not_a_verdict, 'workbench.not_a_verdict');

  requireObject(workbench.feedback, 'workbench.feedback');
  requireString(workbench.feedback.rhythm, 'workbench.feedback.rhythm');
  requireString(workbench.feedback.forum_label, 'workbench.feedback.forum_label');
  requireNullableLink(workbench.feedback.forum_href, 'workbench.feedback.forum_href');

  validateLadder(workbench.ladder);

  if (!Array.isArray(workbench.tools) || workbench.tools.length === 0) fail('workbench.tools must not be empty');
  const seenIds = new Set();
  workbench.tools.forEach((tool, index) => validateTool(tool, index, seenIds));

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

function renderAccess(tool) {
  const access = tool.access;
  if (access.kind === 'site-download') {
    return `<div class="access site-download">
      <a class="access-button" href="${escapeHtml(access.href)}">Download ${escapeHtml(tool.name)}</a>
      <div class="access-detail"><span>SHA-256</span><code>${escapeHtml(access.sha256)}</code></div>
      <div class="access-detail"><span>Size</span><code>${escapeHtml(formatBytes(access.size_bytes))}</code></div>
      <div class="access-detail"><span>Published</span><code>${escapeHtml(access.published_at)}</code></div>
    </div>`;
  }
  if (access.kind === 'public-repo') {
    return `<div class="access public-repo">
      <a class="access-button" href="${escapeHtml(access.href)}" target="_blank" rel="noreferrer">Open the public repository</a>
      <p class="access-note">Clone and build it yourself — there is no packaged download.</p>
    </div>`;
  }
  if (access.kind === 'live-service') {
    return `<div class="access live-service">
      <a class="access-button" href="${escapeHtml(access.href)}" target="_blank" rel="noreferrer">Open the live service</a>
      <p class="access-note">This one is a running service, not a file. There is nothing to download.</p>
    </div>`;
  }
  return `<div class="access not-published">
    <span class="access-button disabled" aria-disabled="true">Not published yet</span>
    <p class="access-note">No download exists for this today. That is a fact about the packaging, not about whether the code works — read the status above.</p>
  </div>`;
}

function renderDocs(tool) {
  if (tool.docs.length === 0) {
    return '<div class="tool-links"><strong>Docs</strong><span class="inert-link">None written yet.</span></div>';
  }
  const items = tool.docs.map((doc) => renderLink(doc)).join('<span aria-hidden="true"> · </span>');
  return `<div class="tool-links"><strong>Docs</strong>${items}</div>`;
}

function renderDiscussion(tool) {
  const thread = safeLink(tool.discussion.href)
    ? renderLink(tool.discussion)
    : '<span class="inert-link">thread opens with the announcement</span>';
  return `<div class="tool-links"><strong>Discussion</strong>${thread}</div>`;
}

function renderFirstTasks(tool) {
  const rows = tool.first_tasks.map((task) => `<li class="first-task">
        <div class="first-task-head"><span class="task-id">${escapeHtml(task.id)}</span><span class="task-size ${cssToken(task.size)}">${escapeHtml(task.size)}</span></div>
        <strong>${escapeHtml(task.title)}</strong>
        <span class="done-when"><em>Done when:</em> ${escapeHtml(task.done_when)}</span>
      </li>`).join('');
  return `<div class="first-tasks">
      <strong>Pick one to start</strong>
      <ul class="first-task-list">${rows}</ul>
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

function renderTool(tool) {
  const token = cssToken(tool.status);
  const milestones = tool.roadmap_milestones.length > 0
    ? tool.roadmap_milestones.map((id) => `<span class="dep">${escapeHtml(id)}</span>`).join('')
    : '<span class="dep">unlinked</span>';
  const requires = tool.requires.length > 0
    ? list(tool.requires, 'requires-list')
    : '<p class="inert-link">Nothing beyond a browser.</p>';
  const sourceLine = safeLink(tool.source.href)
    ? renderLink({ label: tool.source.href, href: tool.source.href })
    : '<span class="inert-link">Private until claimed</span>';

  return `<article class="tool ${escapeHtml(token)}" id="${escapeHtml(tool.id)}">
    <div class="tool-head">
      <div><span class="tool-id">${escapeHtml(tool.id)}</span><h3>${escapeHtml(tool.name)}</h3></div>
      <span class="pill ${escapeHtml(token)}">${statusLabel(tool.status)}</span>
    </div>
    <p class="one-liner">${escapeHtml(tool.one_liner)}</p>
    <div class="status-detail ${escapeHtml(token)}"><strong>What that status actually means</strong>${escapeHtml(tool.status_detail)}</div>
    <div class="tool-facts">
      <div><strong>What it does</strong>${escapeHtml(tool.what_it_does)}</div>
      <div><strong>Who it is for</strong>${escapeHtml(tool.who_its_for)}</div>
    </div>
    <div class="meta-line"><span>first result · ${escapeHtml(tool.time_to_first_result)}</span><span class="deps">roadmap ${milestones}</span></div>
    <div class="tool-columns">
      <div class="requires"><strong>You will need</strong>${requires}</div>
      ${renderAccess(tool)}
    </div>
    <div class="tool-link-row">
      ${renderDocs(tool)}
      ${renderDiscussion(tool)}
      <div class="tool-links"><strong>Source</strong>${sourceLine}</div>
    </div>
    <p class="source-note">${escapeHtml(tool.source.note)}</p>
    ${renderFirstTasks(tool)}
    ${renderRecovery(tool)}
    <div class="ownership-state ${escapeHtml(cssToken(tool.ownership.state))}">
      <span class="pill ownership ${escapeHtml(cssToken(tool.ownership.state))}">${ownershipLabel(tool.ownership.state)}</span>
      <span>${ownershipSentence(tool.ownership)}</span>
      ${tool.ownership.record ? `<span class="ownership-record">recorded in ${escapeHtml(tool.ownership.record)}</span>` : ''}
    </div>
    <div class="tool-footnotes">
      <div class="privacy"><strong>Privacy</strong>${escapeHtml(tool.privacy_note)}</div>
      <div class="license"><strong>License</strong>${escapeHtml(tool.license)}</div>
    </div>
  </article>`;
}

function renderLadder(ladder) {
  return `<div class="ladder" role="list" aria-label="Participation ladder">${ladder.map((step) => `<article class="ladder-step stage-${escapeHtml(String(step.stage))}" role="listitem">
      <div class="ladder-stage">Stage ${escapeHtml(String(step.stage))}</div>
      <h3>${escapeHtml(step.name)}</h3>
      <p class="ladder-did">${escapeHtml(step.what_you_did)}</p>
      <div class="ladder-get"><strong>You get</strong>${escapeHtml(step.what_you_get)}</div>
      <div class="ladder-recorded">${escapeHtml(step.recorded_in)}</div>
    </article>`).join('')}</div>`;
}

function statusSummary(tools) {
  const counts = new Map();
  for (const tool of tools) counts.set(tool.status, (counts.get(tool.status) ?? 0) + 1);
  return [...statuses]
    .filter((status) => counts.has(status))
    .map((status) => `${counts.get(status)} ${statusShortLabel(status)}`)
    .join(' · ');
}

function render(workbench) {
  const tools = workbench.tools.map(renderTool).join('\n');
  const ladder = renderLadder(workbench.ladder);
  const summary = statusSummary(workbench.tools);
  const claimable = workbench.tools.reduce((total, tool) => total + tool.first_tasks.length, 0);
  const forum = safeLink(workbench.feedback.forum_href)
    ? renderLink({ label: workbench.feedback.forum_label, href: workbench.feedback.forum_href })
    : `<span class="inert-link">${escapeHtml(workbench.feedback.forum_label)}</span>`;

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
      --bg: #0b1013;
      --paper: #11191d;
      --panel: #162126;
      --panel-2: #1b292e;
      --line: #2c3a3f;
      --ink: #edf5f2;
      --muted: #9eb0ad;
      --green: #68d391;
      --green-bg: rgba(104, 211, 145, .11);
      --amber: #f6c453;
      --amber-bg: rgba(246, 196, 83, .11);
      --red: #f28b82;
      --red-bg: rgba(242, 139, 130, .11);
      --blue: #75c9f1;
      --blue-bg: rgba(117, 201, 241, .11);
      --violet: #c7a7ff;
      --wood: #d7a86e;
      --shadow: 0 18px 55px rgba(0, 0, 0, .25);
      --sans: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      --mono: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
    }
    * { box-sizing: border-box; }
    html { scroll-behavior: smooth; }
    body { margin: 0; background: radial-gradient(circle at 15% 0%, #18302e 0, transparent 28rem), var(--bg); color: var(--ink); font-family: var(--sans); line-height: 1.55; }
    a { color: var(--blue); text-underline-offset: .2em; }
    a:hover { color: #b7e7ff; }
    code { font-family: var(--mono); color: #d8ece7; overflow-wrap: anywhere; }
    .wrap { width: min(1180px, calc(100% - 32px)); margin: 0 auto; }
    .topbar { position: sticky; top: 0; z-index: 10; border-bottom: 1px solid rgba(255,255,255,.08); background: rgba(11,16,19,.92); backdrop-filter: blur(14px); }
    .topbar-inner { min-height: 58px; display: flex; align-items: center; justify-content: space-between; gap: 20px; }
    .brand { display: flex; align-items: center; gap: 10px; font-weight: 800; letter-spacing: .02em; }
    .mark { width: 26px; height: 26px; display: grid; place-items: center; border: 1px solid var(--wood); color: var(--wood); border-radius: 7px; font-family: var(--mono); }
    nav { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 14px; font-size: .85rem; }
    nav a { color: var(--muted); text-decoration: none; }
    nav a[aria-current="page"] { color: var(--ink); }
    .hero { padding: 72px 0 38px; }
    .eyebrow { color: var(--wood); text-transform: uppercase; letter-spacing: .16em; font-size: .76rem; font-weight: 800; }
    h1 { max-width: 920px; margin: 12px 0 18px; font-size: clamp(2.25rem, 7vw, 5.7rem); line-height: .94; letter-spacing: -.055em; }
    .headline { max-width: 900px; color: var(--amber); font-family: var(--mono); font-size: clamp(.9rem, 2vw, 1.18rem); font-weight: 800; }
    .lede { max-width: 920px; margin: 24px 0; color: #c4d2cf; font-size: 1.06rem; }
    .hero-meta { display: flex; flex-wrap: wrap; gap: 8px; }
    .hero-meta span { border: 1px solid var(--line); border-radius: 999px; padding: 6px 10px; color: var(--muted); background: rgba(255,255,255,.025); font-size: .78rem; }
    section { padding: 38px 0; }
    section + section { border-top: 1px solid rgba(255,255,255,.07); }
    .section-head { display: grid; grid-template-columns: minmax(160px, .35fr) 1fr; gap: 28px; margin-bottom: 24px; }
    .section-number { color: var(--wood); font-family: var(--mono); font-size: .78rem; letter-spacing: .12em; }
    h2 { margin: 0 0 8px; font-size: clamp(1.45rem, 3vw, 2.35rem); letter-spacing: -.025em; }
    h3 { margin: 0; line-height: 1.25; }
    .section-copy { color: var(--muted); max-width: 780px; }
    ul { margin: 10px 0 0; padding-left: 1.15rem; }
    li { margin: 7px 0; }
    .pill { display: inline-flex; align-items: center; flex: 0 0 auto; padding: 4px 8px; border-radius: 999px; font-family: var(--mono); font-size: .67rem; font-weight: 900; letter-spacing: .06em; border: 1px solid currentColor; white-space: nowrap; }
    .pill.live { color: var(--green); background: var(--green-bg); }
    .pill.dev-only { color: var(--blue); background: var(--blue-bg); }
    .pill.local-only { color: var(--amber); background: var(--amber-bg); }
    .pill.recoverable-not-running { color: var(--red); background: var(--red-bg); }
    .pill.ownership { font-size: .62rem; }
    .pill.ownership.unclaimed { color: var(--muted); background: rgba(158,176,173,.08); }
    .pill.ownership.trying { color: var(--amber); background: var(--amber-bg); }
    .pill.ownership.claimed { color: var(--blue); background: var(--blue-bg); }
    .pill.ownership.owned { color: var(--green); background: var(--green-bg); }
    .inert-link { color: #70827e; font-style: italic; }
    .not-a-verdict { max-width: 920px; margin: 4px 0 24px; padding: 16px 18px; border-left: 3px solid var(--wood); background: rgba(215,168,110,.09); color: #d9cdb8; font-size: .92rem; }
    .not-a-verdict strong { display: block; margin-bottom: 4px; color: var(--wood); font-family: var(--mono); font-size: .66rem; text-transform: uppercase; letter-spacing: .08em; }
    .ladder { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 9px; }
    .ladder-step { position: relative; min-width: 0; display: grid; align-content: start; gap: 6px; padding: 14px; border: 1px solid var(--line); border-top: 3px solid var(--blue); border-radius: 9px; background: rgba(0,0,0,.16); }
    .ladder-step.stage-0 { border-top-color: var(--muted); }
    .ladder-step.stage-3 { border-top-color: var(--violet); }
    .ladder-step.stage-4 { border-top-color: var(--green); }
    .ladder-step:not(:last-child)::after { content: "→"; position: absolute; right: -8px; top: 46px; z-index: 2; color: var(--wood); font-weight: 900; }
    .ladder-stage { color: var(--wood); font-family: var(--mono); font-size: .63rem; font-weight: 900; letter-spacing: .07em; }
    .ladder-step h3 { font-size: .95rem; }
    .ladder-did { margin: 0; color: #c5d2cf; font-size: .78rem; }
    .ladder-get { padding-top: 8px; border-top: 1px solid var(--line); color: var(--muted); font-size: .74rem; }
    .ladder-get strong { display: block; color: var(--green); font-family: var(--mono); font-size: .58rem; text-transform: uppercase; letter-spacing: .06em; }
    .ladder-recorded { color: #70827e; font-family: var(--mono); font-size: .63rem; }
    .tool-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; align-items: start; }
    .tool { padding: 22px; border: 1px solid var(--line); border-top: 4px solid var(--blue); border-radius: 13px; background: linear-gradient(155deg, var(--panel), var(--paper)); box-shadow: var(--shadow); }
    .tool.live { border-top-color: var(--green); }
    .tool.dev-only { border-top-color: var(--blue); }
    .tool.local-only { border-top-color: var(--amber); }
    .tool.recoverable-not-running { border-top-color: var(--red); }
    .tool-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 14px; }
    .tool-head > div { display: grid; gap: 2px; }
    .tool-id { color: var(--wood); font-family: var(--mono); font-size: .72rem; font-weight: 800; }
    .one-liner { margin: 12px 0; color: #cad7d4; }
    .status-detail { padding: 13px 14px; border-left: 3px solid var(--blue); background: var(--blue-bg); color: #c8d7d3; font-size: .84rem; }
    .status-detail strong { display: block; margin-bottom: 3px; color: var(--blue); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; letter-spacing: .07em; }
    .status-detail.live { border-left-color: var(--green); background: var(--green-bg); }
    .status-detail.live strong { color: var(--green); }
    .status-detail.local-only { border-left-color: var(--amber); background: var(--amber-bg); }
    .status-detail.local-only strong { color: var(--amber); }
    .status-detail.recoverable-not-running { border-left-color: var(--red); background: var(--red-bg); }
    .status-detail.recoverable-not-running strong { color: var(--red); }
    .tool-facts { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin: 12px 0; }
    .tool-facts > div { padding: 10px 12px; border: 1px solid var(--line); border-radius: 7px; color: var(--muted); font-size: .78rem; }
    .tool-facts strong { display: block; margin-bottom: 2px; color: var(--ink); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; letter-spacing: .06em; }
    .meta-line { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin: 12px 0; color: var(--muted); font-family: var(--mono); font-size: .72rem; text-transform: uppercase; }
    .deps { display: flex; align-items: center; gap: 5px; }
    .dep { padding: 1px 5px; background: rgba(255,255,255,.06); border-radius: 4px; color: var(--ink); }
    .tool-columns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; align-items: start; }
    .requires, .access { padding: 12px 14px; border: 1px solid var(--line); border-radius: 8px; background: rgba(0,0,0,.14); }
    .requires strong { display: block; color: var(--muted); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; letter-spacing: .06em; }
    .requires-list { margin: 5px 0 0; color: #c5d2cf; font-size: .78rem; }
    .requires-list li { margin: 4px 0; }
    .access-button { display: inline-block; padding: 8px 13px; border: 1px solid var(--green); border-radius: 7px; color: var(--green); background: var(--green-bg); font-family: var(--mono); font-size: .74rem; font-weight: 800; text-decoration: none; }
    .access-button:hover { color: var(--ink); border-color: var(--ink); }
    .access.public-repo .access-button { border-color: var(--blue); color: var(--blue); background: var(--blue-bg); }
    .access.live-service .access-button { border-color: var(--amber); color: var(--amber); background: var(--amber-bg); }
    .access-button.disabled { border-color: var(--line); color: #70827e; background: rgba(158,176,173,.06); cursor: not-allowed; }
    .access-note { margin: 8px 0 0; color: var(--muted); font-size: .74rem; }
    .access-detail { display: flex; gap: 8px; margin-top: 7px; font-size: .68rem; }
    .access-detail span { flex: 0 0 68px; color: var(--muted); font-family: var(--mono); text-transform: uppercase; }
    .tool-link-row { display: grid; gap: 6px; margin-top: 12px; }
    .tool-links { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; font-size: .78rem; }
    .tool-links strong { flex: 0 0 76px; color: var(--muted); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; letter-spacing: .06em; }
    .source-note { margin: 8px 0 0; color: var(--muted); font-size: .76rem; }
    .first-tasks { margin-top: 16px; padding: 14px; border-left: 3px solid var(--wood); background: rgba(215,168,110,.08); }
    .first-tasks > strong { color: var(--wood); font-family: var(--mono); font-size: .66rem; text-transform: uppercase; letter-spacing: .08em; }
    .first-task-list { list-style: none; margin: 8px 0 0; padding: 0; display: grid; gap: 8px; }
    .first-task { display: grid; gap: 3px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 7px; background: rgba(0,0,0,.18); }
    .first-task-head { display: flex; align-items: center; gap: 8px; }
    .task-id { color: var(--wood); font-family: var(--mono); font-size: .7rem; font-weight: 900; }
    .task-size { padding: 1px 6px; border-radius: 999px; color: var(--muted); background: rgba(255,255,255,.05); font-family: var(--mono); font-size: .6rem; text-transform: uppercase; }
    .task-size.small { color: var(--green); background: var(--green-bg); }
    .task-size.medium { color: var(--amber); background: var(--amber-bg); }
    .first-task strong { font-size: .86rem; }
    .done-when { color: var(--muted); font-size: .76rem; }
    .done-when em { color: var(--ink); font-style: normal; font-weight: 700; }
    .recovery { margin-top: 12px; padding: 13px 14px; border-left: 3px solid var(--red); background: var(--red-bg); }
    .recovery > strong { color: var(--red); font-family: var(--mono); font-size: .64rem; text-transform: uppercase; letter-spacing: .07em; }
    .recovery p { margin: 6px 0 0; color: #d5c3c1; font-size: .78rem; }
    .recovery-origin { font-family: var(--mono); font-size: .72rem; overflow-wrap: anywhere; }
    .recovery-paths { margin: 8px 0 0; padding-left: 1.05rem; color: var(--muted); font-size: .72rem; }
    .recovery-paths li { margin: 3px 0; }
    .ownership-state { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 14px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 8px; color: var(--muted); font-size: .78rem; }
    .ownership-record { color: #70827e; font-family: var(--mono); font-size: .68rem; }
    .tool-footnotes { display: grid; gap: 6px; margin-top: 12px; font-size: .74rem; color: var(--muted); }
    .tool-footnotes strong { margin-right: 6px; color: var(--ink); font-family: var(--mono); font-size: .6rem; text-transform: uppercase; letter-spacing: .06em; }
    .privacy { padding: 9px 11px; border-left: 3px solid var(--violet); background: rgba(199,167,255,.08); }
    footer { padding: 38px 0 60px; color: var(--muted); font-size: .82rem; }
    .feedback-rhythm { margin-bottom: 10px; padding: 12px 14px; border-left: 3px solid var(--blue); background: var(--blue-bg); color: #c8d7d3; }
    .generated { color: #6f817e; font-family: var(--mono); font-size: .7rem; }
    .baseline-provenance { margin-top: 14px; color: #6f817e; font-family: var(--mono); font-size: .62rem; letter-spacing: .015em; }
    .baseline-provenance a { color: inherit; text-decoration: none; border-bottom: 1px dotted currentColor; }
    @media (max-width: 820px) {
      .section-head { grid-template-columns: 1fr; gap: 10px; }
      .tool-grid, .tool-facts, .tool-columns { grid-template-columns: 1fr; }
      .ladder { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .ladder-step::after { display: none; }
      .topbar-inner { align-items: flex-start; padding: 12px 0; }
      nav { gap: 8px; }
      .hero { padding-top: 48px; }
    }
    @media (max-width: 560px) {
      .wrap { width: min(1180px, calc(100% - 20px)); }
      .topbar-inner { display: block; }
      nav { justify-content: flex-start; margin-top: 8px; }
      .ladder { grid-template-columns: 1fr; }
      .tool-head { display: grid; }
      .tool-links strong { flex: 1 1 100%; }
    }
    @media print {
      :root { color-scheme: light; --bg: #fff; --paper: #fff; --panel: #f6f7f7; --panel-2: #eee; --line: #ccd2d0; --ink: #15201d; --muted: #53635e; }
      body { background: #fff; }
      .topbar { position: static; background: #fff; }
      .tool, .ladder-step { break-inside: avoid; box-shadow: none; }
      nav { display: none; }
      a { color: #075985; }
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
        <a href="/networksense">NetworkSense</a>
        <a href="/events">Events</a>
        <a href="/testing">Testing</a>
      </nav>
    </div>
  </header>

  <main>
    <div class="wrap hero">
      <div class="eyebrow">Comfy × Valheim · Lumberjacks P7</div>
      <h1>${escapeHtml(workbench.title.replace('Comfy × Valheim — ', ''))}</h1>
      <div class="headline">${escapeHtml(workbench.headline)}</div>
      <p class="lede">${escapeHtml(workbench.honesty_statement)}</p>
      <div class="not-a-verdict"><strong>This is not a verdict on anyone</strong>${escapeHtml(workbench.not_a_verdict)}</div>
      <div class="hero-meta">
        <span>Updated ${escapeHtml(workbench.updated_at)}</span>
        <span>${escapeHtml(String(workbench.tools.length))} tools</span>
        <span>${escapeHtml(summary)}</span>
        <span>${escapeHtml(String(claimable))} first tasks open</span>
      </div>
    </div>

    <section id="ladder">
      <div class="wrap">
        <div class="section-head"><div class="section-number">01 · THE LADDER</div><div><h2>Five rungs, and you can stop on any of them.</h2><p class="section-copy">Nobody starts as an owner. Each rung says what you did, what you get for it, and where it is written down. Stopping at stage 1 is a complete and finished contribution.</p></div></div>
        ${ladder}
      </div>
    </section>

    <section id="tools">
      <div class="wrap">
        <div class="section-head"><div class="section-number">02 · THE TOOLS</div><div><h2>${escapeHtml(workbench.honesty_statement)}</h2><p class="section-copy">A status chip here is never a mood. It is one of four declared states, and every one of them is qualified in prose by the person who wrote the code. Where something does not run, the card says so and points at where the pieces are.</p></div></div>
        <div class="tool-grid">${tools}</div>
      </div>
    </section>
  </main>

  <footer class="wrap">
    <div class="feedback-rhythm">${escapeHtml(workbench.feedback.rhythm)} Threads live in the ${forum}.</div>
    <div>If a card on this page is wrong, that is the most useful bug report you can file — the whole point is that the status matches reality.</div>
    <div class="generated">Generated deterministically from ${escapeHtml(workbenchRelative)} · do not hand-edit this file.</div>
    <nav class="baseline-provenance" aria-label="Project provenance"><a href="https://github.com/djcdevelopment/baseline" target="_blank" rel="noreferrer">Baseline</a><span aria-hidden="true"> · </span><a href="https://github.com/djcdevelopment/Lumberjacks" target="_blank" rel="noreferrer">Lumberjacks</a><span aria-hidden="true"> · </span><a href="https://github.com/djcdevelopment/comfy" target="_blank" rel="noreferrer">Comfy</a><span aria-hidden="true"> · </span><a href="https://github.com/djcdevelopment/baseline/blob/main/LICENSING.md" target="_blank" rel="noreferrer">license details</a></nav>
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

  const expected = render(workbench);
  let actual;
  try {
    actual = fs.readFileSync(outputPath, 'utf8');
  } catch {
    fail(`${outputRelative} is missing; run npm run workbench:render`);
  }
  if (actual !== expected) fail(`${outputRelative} is stale; run npm run workbench:render`);

  if (!actual.includes('id="ladder"') || !actual.includes('id="tools"') || !actual.includes('02 · THE TOOLS')) {
    fail('generated workbench is missing a required section');
  }
  // The Gateway serves this file verbatim under a self-only CSP; a <script> or <link> would
  // either be blocked or, worse, turn a static catalog into a page that fetches something.
  if (/<(?:script|link)\b/i.test(actual)) fail('workbench HTML must remain self-contained and script-free');

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

The workbench has no note/journal command: docs/workbench/workbench.json is the whole source
of truth, and its history is the file's own diff.`);
}

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
