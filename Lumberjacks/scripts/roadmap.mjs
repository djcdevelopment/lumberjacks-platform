#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const roadmapRelative = 'docs/roadmap/valheim-volunteer-roadmap.json';
const notesRelative = 'docs/roadmap/commit-notes.jsonl';
const outputRelative = 'src/Game.Gateway/Community/roadmap.html';
const roadmapPath = path.join(repoRoot, roadmapRelative);
const notesPath = path.join(repoRoot, notesRelative);
const outputPath = path.join(repoRoot, outputRelative);

const statuses = new Set(['active', 'queued', 'gated', 'later', 'complete']);
const noteKinds = new Set([
  'planning',
  'implementation',
  'verification',
  'deployment',
  'decision',
  'rollback',
  'documentation',
]);

function fail(message) {
  throw new Error(message);
}

function readSources() {
  const roadmapRaw = fs.readFileSync(roadmapPath, 'utf8');
  const notesRaw = fs.readFileSync(notesPath, 'utf8');
  const roadmap = JSON.parse(roadmapRaw);
  const noteLines = notesRaw.split(/\r?\n/).filter((line) => line.trim().length > 0);
  const notes = noteLines.map((line, index) => {
    try {
      return JSON.parse(line);
    } catch (error) {
      fail(`${notesRelative}:${index + 1}: invalid JSON: ${error.message}`);
    }
  });
  return { roadmap, notes, roadmapRaw, notesRaw };
}

function requireString(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    fail(`${label} must be a non-empty string`);
  }
}

function requireStringArray(value, label) {
  if (!Array.isArray(value)) fail(`${label} must be an array`);
  value.forEach((item, index) => requireString(item, `${label}[${index}]`));
}

function requireLinkArray(value, label) {
  if (!Array.isArray(value)) fail(`${label} must be an array`);
  value.forEach((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) fail(`${label}[${index}] must be an object`);
    requireString(item.label, `${label}[${index}].label`);
    if (item.href !== undefined && typeof item.href !== 'string') fail(`${label}[${index}].href must be a string when provided`);
  });
}

function requireEvidenceArray(value, label) {
  if (!Array.isArray(value)) fail(`${label} must be an array`);
  value.forEach((item, index) => {
    if (typeof item === 'string') {
      requireString(item, `${label}[${index}]`);
      return;
    }
    if (!item || typeof item !== 'object' || Array.isArray(item)) fail(`${label}[${index}] must be a string or link object`);
    requireString(item.label, `${label}[${index}].label`);
    if (item.href !== undefined && typeof item.href !== 'string') fail(`${label}[${index}].href must be a string when provided`);
  });
}

function requireObjectArray(value, label, fields) {
  if (!Array.isArray(value)) fail(`${label} must be an array`);
  value.forEach((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) fail(`${label}[${index}] must be an object`);
    fields.forEach((field) => requireString(item[field], `${label}[${index}].${field}`));
  });
}

function dependencyLayers(milestones) {
  const byId = new Map(milestones.map((milestone, index) => [milestone.id, { milestone, index }]));
  const indegree = new Map(milestones.map((milestone) => [milestone.id, milestone.depends_on.length]));
  const dependents = new Map(milestones.map((milestone) => [milestone.id, []]));
  const rank = new Map(milestones.map((milestone) => [milestone.id, 0]));

  for (const milestone of milestones) {
    for (const dependency of milestone.depends_on) {
      if (dependents.has(dependency)) dependents.get(dependency).push(milestone.id);
    }
  }

  const ready = milestones.filter((milestone) => indegree.get(milestone.id) === 0).map((milestone) => milestone.id);
  const processed = [];
  while (ready.length > 0) {
    ready.sort((left, right) => byId.get(left).index - byId.get(right).index);
    const id = ready.shift();
    processed.push(id);
    for (const dependent of dependents.get(id)) {
      rank.set(dependent, Math.max(rank.get(dependent), rank.get(id) + 1));
      indegree.set(dependent, indegree.get(dependent) - 1);
      if (indegree.get(dependent) === 0) ready.push(dependent);
    }
  }

  if (processed.length !== milestones.length) {
    const cyclic = milestones.filter((milestone) => !processed.includes(milestone.id)).map((milestone) => milestone.id);
    fail(`milestone dependency graph contains a cycle involving: ${cyclic.join(', ')}`);
  }

  const layers = [];
  for (const id of processed) {
    const layer = rank.get(id);
    if (!layers[layer]) layers[layer] = [];
    layers[layer].push(byId.get(id).milestone);
  }
  return layers;
}

function validateNoSecrets(text) {
  const patterns = [
    { label: 'SteamID', regex: /\b7656119\d{10}\b/ },
    { label: 'credential assignment', regex: /\b(?:client[_ -]?access[_ -]?key|enrollment[_ -]?(?:key|token)|bearer|password|invite[_ -]?token)\s*[=:]\s*["']?[A-Za-z0-9+/_=-]{12,}/i },
    { label: 'credential query string', regex: /[?&](?:access[_-]?key|bearer|invite|key|token)=[^&\s]{8,}/i },
  ];

  for (const pattern of patterns) {
    if (pattern.regex.test(text)) {
      fail(`public roadmap source contains a possible ${pattern.label}`);
    }
  }
}

function validate(roadmap, notes, rawText = '') {
  if (roadmap.schema_version !== 2) fail('roadmap.schema_version must be 2');
  requireString(roadmap.title, 'roadmap.title');
  requireString(roadmap.headline, 'roadmap.headline');
  requireString(roadmap.updated_at, 'roadmap.updated_at');
  if (Number.isNaN(Date.parse(roadmap.updated_at))) fail('roadmap.updated_at must be an ISO timestamp');
  requireString(roadmap.claim, 'roadmap.claim');
  requireStringArray(roadmap.current_focus, 'roadmap.current_focus');
  if (!roadmap.primer || typeof roadmap.primer !== 'object') fail('roadmap.primer is required');
  requireString(roadmap.primer.title, 'roadmap.primer.title');
  requireString(roadmap.primer.summary, 'roadmap.primer.summary');
  requireString(roadmap.primer.denominator, 'roadmap.primer.denominator');
  requireObjectArray(roadmap.primer.dataflow, 'roadmap.primer.dataflow', ['stage', 'owner', 'action', 'boundary']);
  requireObjectArray(roadmap.glossary, 'roadmap.glossary', ['term', 'definition']);
  if (!Array.isArray(roadmap.status_axes) || roadmap.status_axes.length < 3) fail('roadmap.status_axes must define milestone, live-session, and sealed-result axes');
  const axisIds = new Set();
  roadmap.status_axes.forEach((axis, index) => {
    const label = `roadmap.status_axes[${index}]`;
    requireString(axis.id, `${label}.id`);
    if (axisIds.has(axis.id)) fail(`${label}.id is duplicated: ${axis.id}`);
    axisIds.add(axis.id);
    requireString(axis.label, `${label}.label`);
    requireString(axis.question, `${label}.question`);
    requireObjectArray(axis.states, `${label}.states`, ['name', 'meaning']);
  });
  if (!roadmap.proof_pipeline || typeof roadmap.proof_pipeline !== 'object') fail('roadmap.proof_pipeline is required');
  requireObjectArray(roadmap.proof_pipeline.stages, 'roadmap.proof_pipeline.stages', ['stage', 'event', 'proof']);
  requireStringArray(roadmap.proof_pipeline.conservation, 'roadmap.proof_pipeline.conservation');
  requireStringArray(roadmap.proof_pipeline.strict_closure, 'roadmap.proof_pipeline.strict_closure');
  if (!Array.isArray(roadmap.tracks) || roadmap.tracks.length < 2) fail('roadmap.tracks must contain at least two tracks');
  const trackIds = new Set(['shared']);
  roadmap.tracks.forEach((track, index) => {
    const label = `roadmap.tracks[${index}]`;
    requireString(track.id, `${label}.id`);
    if (trackIds.has(track.id)) fail(`${label}.id is duplicated or reserved: ${track.id}`);
    trackIds.add(track.id);
    requireString(track.name, `${label}.name`);
    requireString(track.description, `${label}.description`);
    if (Object.hasOwn(track, 'path')) fail(`${label}.path is hand-authored dependency data; remove it and use milestone.depends_on`);
  });
  if (!Array.isArray(roadmap.milestones) || roadmap.milestones.length === 0) fail('roadmap.milestones must not be empty');

  const milestoneIds = new Set();
  for (const [index, milestone] of roadmap.milestones.entries()) {
    const label = `roadmap.milestones[${index}]`;
    requireString(milestone.id, `${label}.id`);
    if (milestoneIds.has(milestone.id)) fail(`duplicate milestone id ${milestone.id}`);
    milestoneIds.add(milestone.id);
    requireString(milestone.title, `${label}.title`);
    requireString(milestone.track, `${label}.track`);
    if (!trackIds.has(milestone.track)) fail(`${label}.track is unknown: ${milestone.track}`);
    if (!statuses.has(milestone.status)) fail(`${label}.status is not allowed: ${milestone.status}`);
    requireString(milestone.owns, `${label}.owns`);
    requireString(milestone.does_not_own, `${label}.does_not_own`);
    requireString(milestone.summary, `${label}.summary`);
    requireStringArray(milestone.depends_on, `${label}.depends_on`);
    requireStringArray(milestone.work, `${label}.work`);
    requireStringArray(milestone.exit_criteria, `${label}.exit_criteria`);
    requireStringArray(milestone.inputs, `${label}.inputs`);
    requireEvidenceArray(milestone.exit_evidence, `${label}.exit_evidence`);
  }

  for (const milestone of roadmap.milestones) {
    for (const dependency of milestone.depends_on) {
      if (!milestoneIds.has(dependency)) fail(`${milestone.id} has unknown dependency ${dependency}`);
      if (dependency === milestone.id) fail(`${milestone.id} cannot depend on itself`);
    }
    if (milestone.status === 'complete') {
      const incomplete = milestone.depends_on.filter((dependency) => roadmap.milestones.find((item) => item.id === dependency)?.status !== 'complete');
      if (incomplete.length > 0) fail(`${milestone.id} cannot be complete while dependencies are incomplete: ${incomplete.join(', ')}`);
    }
  }
  dependencyLayers(roadmap.milestones);

  if (!roadmap.latest_observation || typeof roadmap.latest_observation !== 'object') fail('roadmap.latest_observation is required');
  for (const field of ['id', 'kind', 'label', 'captured_at', 'scope', 'delivery_result', 'system_verdict', 'verdict_reason', 'milestone_effect', 'operational_observation']) {
    requireString(roadmap.latest_observation[field], `roadmap.latest_observation.${field}`);
  }
  if (!roadmap.latest_observation.conservation || typeof roadmap.latest_observation.conservation !== 'object') {
    fail('roadmap.latest_observation.conservation is required');
  }
  for (const field of ['eligible', 'durable', 'applied', 'superseded', 'acknowledged', 'pending', 'native']) {
    const value = roadmap.latest_observation.conservation[field];
    if (!Number.isSafeInteger(value) || value < 0) {
      fail(`roadmap.latest_observation.conservation.${field} must be a non-negative safe integer`);
    }
  }
  requireStringArray(roadmap.latest_observation.informs, 'roadmap.latest_observation.informs');
  for (const milestoneId of roadmap.latest_observation.informs) {
    if (!milestoneIds.has(milestoneId)) fail(`roadmap.latest_observation.informs references unknown milestone ${milestoneId}`);
  }
  const verdictAxis = roadmap.status_axes.find((axis) => axis.id === 'system_verdict');
  if (!verdictAxis?.states.some((state) => state.name === roadmap.latest_observation.system_verdict)) {
    fail('roadmap.latest_observation.system_verdict must be a declared system_verdict state');
  }
  if (!roadmap.latest_observation.evidence || typeof roadmap.latest_observation.evidence !== 'object') {
    fail('roadmap.latest_observation.evidence is required');
  }
  for (const field of ['status', 'label', 'detail']) {
    requireString(roadmap.latest_observation.evidence[field], `roadmap.latest_observation.evidence.${field}`);
  }
  if (!['local_only', 'immutable'].includes(roadmap.latest_observation.evidence.status)) {
    fail('roadmap.latest_observation.evidence.status must be local_only or immutable');
  }
  if (roadmap.latest_observation.delivery_result === 'COMPLETE') {
    const result = roadmap.latest_observation.conservation;
    if (result.eligible !== result.durable || result.durable !== result.acknowledged) {
      fail('a COMPLETE latest observation must conserve eligible = durable = acknowledged');
    }
    if (result.applied + result.superseded !== result.acknowledged) {
      fail('a COMPLETE latest observation must conserve applied + superseded = acknowledged');
    }
    if (result.pending !== 0 || result.native !== 0) {
      fail('a COMPLETE latest observation must close with zero pending and native eligible sends');
    }
  }

  if (!Array.isArray(roadmap.readiness) || roadmap.readiness.length < 2) fail('roadmap.readiness must describe single and concurrent readiness');
  if (!roadmap.volunteer_success) fail('roadmap.volunteer_success is required');
  requireString(roadmap.volunteer_success.headline, 'roadmap.volunteer_success.headline');
  requireString(roadmap.volunteer_success.definition, 'roadmap.volunteer_success.definition');
  requireString(roadmap.volunteer_success.catalog_revision, 'roadmap.volunteer_success.catalog_revision');
  requireString(roadmap.volunteer_success.availability, 'roadmap.volunteer_success.availability');
  requireStringArray(roadmap.volunteer_success.receipt_axes, 'roadmap.volunteer_success.receipt_axes');
  for (const axisId of roadmap.volunteer_success.receipt_axes) {
    if (!axisIds.has(axisId)) fail(`roadmap.volunteer_success.receipt_axes references unknown status axis: ${axisId}`);
  }
  for (const requiredAxis of ['participation', 'system_verdict']) {
    if (!roadmap.volunteer_success.receipt_axes.includes(requiredAxis)) fail(`roadmap.volunteer_success.receipt_axes must include ${requiredAxis}`);
  }
  if (!Array.isArray(roadmap.volunteer_success.journey) || roadmap.volunteer_success.journey.length < 4) fail('roadmap.volunteer_success.journey must contain the complete volunteer loop');
  if (!Array.isArray(roadmap.volunteer_success.test_cards) || roadmap.volunteer_success.test_cards.length === 0) fail('roadmap.volunteer_success.test_cards must not be empty');
  requireObjectArray(roadmap.volunteer_success.first_canary_time_budget, 'roadmap.volunteer_success.first_canary_time_budget', ['phase', 'commitment', 'duration', 'detail']);
  requireStringArray(roadmap.volunteer_success.receipt, 'roadmap.volunteer_success.receipt');
  roadmap.volunteer_success.journey.forEach((step, index) => {
    requireString(step.stage, `roadmap.volunteer_success.journey[${index}].stage`);
    requireString(step.title, `roadmap.volunteer_success.journey[${index}].title`);
    requireString(step.done_when, `roadmap.volunteer_success.journey[${index}].done_when`);
  });
  const cardIds = new Set();
  roadmap.volunteer_success.test_cards.forEach((card, index) => {
    for (const field of ['id', 'title', 'duration', 'exercise', 'observe', 'proof', 'availability']) {
      requireString(card[field], `roadmap.volunteer_success.test_cards[${index}].${field}`);
    }
    if (cardIds.has(card.id)) fail(`duplicate volunteer test card id ${card.id}`);
    cardIds.add(card.id);
  });
  if (!roadmap.golden_proof || !Array.isArray(roadmap.golden_proof.metrics)) fail('roadmap.golden_proof.metrics is required');
  requireString(roadmap.golden_proof.label, 'roadmap.golden_proof.label');
  requireString(roadmap.golden_proof.captured_at, 'roadmap.golden_proof.captured_at');
  requireString(roadmap.golden_proof.scope, 'roadmap.golden_proof.scope');
  if (!roadmap.golden_proof.denominator || typeof roadmap.golden_proof.denominator !== 'object') fail('roadmap.golden_proof.denominator is required');
  for (const field of ['eligible', 'redirected', 'acknowledged', 'native']) {
    const value = roadmap.golden_proof.denominator[field];
    if (!Number.isSafeInteger(value) || value < 0) fail(`roadmap.golden_proof.denominator.${field} must be a non-negative safe integer`);
  }
  requireString(roadmap.golden_proof.denominator.statement, 'roadmap.golden_proof.denominator.statement');
  roadmap.golden_proof.metrics.forEach((metric, index) => {
    if (!Array.isArray(metric) || metric.length !== 2) fail(`roadmap.golden_proof.metrics[${index}] must contain label and value`);
    requireString(metric[0], `roadmap.golden_proof.metrics[${index}][0]`);
    requireString(metric[1], `roadmap.golden_proof.metrics[${index}][1]`);
  });
  if (!roadmap.golden_proof.publication || typeof roadmap.golden_proof.publication !== 'object') fail('roadmap.golden_proof.publication is required');
  requireString(roadmap.golden_proof.publication.status, 'roadmap.golden_proof.publication.status');
  requireString(roadmap.golden_proof.publication.detail, 'roadmap.golden_proof.publication.detail');
  if (!Array.isArray(roadmap.authority_planes)) fail('roadmap.authority_planes must be an array');
  requireStringArray(roadmap.no_go, 'roadmap.no_go');

  const noteIds = new Set();
  let previousTime = -Infinity;
  for (const [index, note] of notes.entries()) {
    const label = `${notesRelative}:${index + 1}`;
    if (note.schema_version !== 1) fail(`${label}: schema_version must be 1`);
    requireString(note.id, `${label}.id`);
    if (noteIds.has(note.id)) fail(`${label}: duplicate note id ${note.id}`);
    noteIds.add(note.id);
    requireString(note.at, `${label}.at`);
    const time = Date.parse(note.at);
    if (Number.isNaN(time)) fail(`${label}: at must be an ISO timestamp`);
    if (time < previousTime) fail(`${label}: notes must be append-only chronological records`);
    previousTime = time;
    requireString(note.author, `${label}.author`);
    requireString(note.repository, `${label}.repository`);
    requireStringArray(note.milestones, `${label}.milestones`);
    for (const milestoneId of note.milestones) {
      if (!milestoneIds.has(milestoneId)) fail(`${label}: unknown milestone ${milestoneId}`);
    }
    if (!noteKinds.has(note.kind)) fail(`${label}: unsupported kind ${note.kind}`);
    requireString(note.summary, `${label}.summary`);
    requireString(note.impact, `${label}.impact`);
    requireStringArray(note.verification, `${label}.verification`);
    requireStringArray(note.evidence, `${label}.evidence`);
  }

  validateNoSecrets(rawText || `${JSON.stringify(roadmap)}\n${notes.map((note) => JSON.stringify(note)).join('\n')}`);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function safeLink(href) {
  return typeof href === 'string' && (href.startsWith('https://github.com/') || href.startsWith('/'));
}

function renderLink(item) {
  if (!safeLink(item.href)) return `<span>${escapeHtml(item.label)}</span>`;
  const external = item.href.startsWith('https://');
  return `<a href="${escapeHtml(item.href)}"${external ? ' target="_blank" rel="noreferrer"' : ''}>${escapeHtml(item.label)}</a>`;
}

function renderEvidenceItem(item) {
  return typeof item === 'string' ? `<span>${escapeHtml(item)}</span>` : renderLink(item);
}

function list(items, className = '') {
  return `<ul${className ? ` class="${className}"` : ''}>${items.map((item) => `<li>${escapeHtml(item)}</li>`).join('')}</ul>`;
}

function cssToken(value) {
  return String(value).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'unknown';
}

function statusLabel(status) {
  return {
    active: 'IN PROGRESS',
    queued: 'QUEUED',
    gated: 'GATED',
    later: 'LATER',
    complete: 'COMPLETE',
    not_ready: 'NOT READY',
  }[status] ?? String(status).toUpperCase();
}

function renderMilestone(milestone) {
  const dependencies = milestone.depends_on.length > 0
    ? milestone.depends_on.map((id) => `<span class="dep">${escapeHtml(id)}</span>`).join('')
    : '<span class="dep">foundation</span>';
  const inputs = milestone.inputs.length > 0
    ? milestone.inputs.map((item) => `<li>${escapeHtml(item)}</li>`).join('')
    : '<span class="empty-evidence">No external input is required.</span>';
  const exitEvidence = milestone.exit_evidence.length > 0
    ? milestone.exit_evidence.map((item) => `<li>${renderEvidenceItem(item)}</li>`).join('')
    : '<span class="empty-evidence">None recorded; this exit gate remains open.</span>';
  const gateClosed = milestone.status === 'complete';

  return `<article class="milestone ${escapeHtml(milestone.status)}" id="${escapeHtml(milestone.id)}">
    <div class="milestone-head">
      <div><span class="milestone-id">${escapeHtml(milestone.id)}</span><h3>${escapeHtml(milestone.title)}</h3></div>
      <span class="pill ${escapeHtml(milestone.status)}">${statusLabel(milestone.status)}</span>
    </div>
    <div class="meta-line"><span>${escapeHtml(milestone.track)}</span><span class="deps">depends on ${dependencies}</span></div>
    <p>${escapeHtml(milestone.summary)}</p>
    <div class="ownership"><span><strong>Owns</strong>${escapeHtml(milestone.owns)}</span><span><strong>Does not own</strong>${escapeHtml(milestone.does_not_own)}</span></div>
    ${list(milestone.work, 'work-list')}
    <div class="gate ${gateClosed ? 'closed' : 'open'}"><strong>${gateClosed ? 'Exit gate · met' : 'Exit gate · not yet met'}</strong>${list(milestone.exit_criteria, 'exit-criteria')}</div>
    <div class="milestone-evidence">
      <div><strong>Inputs</strong>${milestone.inputs.length > 0 ? `<ul>${inputs}</ul>` : inputs}</div>
      <div><strong>Closing evidence</strong>${milestone.exit_evidence.length > 0 ? `<ul>${exitEvidence}</ul>` : exitEvidence}</div>
    </div>
  </article>`;
}

function renderDependencyDag(milestones) {
  const layers = dependencyLayers(milestones);
  return `<div class="dag" role="list" aria-label="Milestone dependency graph">${layers.map((layer, index) => {
    const nodes = layer.map((milestone) => {
      const dependencies = milestone.depends_on.length > 0
        ? `<span>after ${milestone.depends_on.map((id) => escapeHtml(id)).join(' + ')}</span>`
        : '<span>foundation</span>';
      return `<a class="dag-node ${escapeHtml(milestone.track)} ${escapeHtml(milestone.status)}" href="#${escapeHtml(milestone.id)}" role="listitem">
        <span class="dag-id">${escapeHtml(milestone.id)}</span>
        <strong>${escapeHtml(milestone.title)}</strong>
        ${dependencies}
      </a>`;
    }).join('');
    return `${index > 0 ? '<div class="dag-arrow" aria-hidden="true">↓</div>' : ''}<div class="dag-layer">${nodes}</div>`;
  }).join('')}</div>`;
}

function renderNote(note) {
  const verification = note.verification.length
    ? `<details><summary>Verification (${note.verification.length})</summary>${list(note.verification)}</details>`
    : '';
  const evidence = note.evidence.length
    ? `<div class="journal-evidence"><strong>Evidence:</strong> ${note.evidence.map((item) => `<code>${escapeHtml(item)}</code>`).join(' ')}</div>`
    : '';

  return `<article class="journal-entry" id="note-${escapeHtml(note.id)}">
    <div class="journal-rail" aria-hidden="true"></div>
    <div class="journal-body">
      <div class="journal-meta">
        <time datetime="${escapeHtml(note.at)}">${escapeHtml(note.at)}</time>
        <span class="pill note-kind">${escapeHtml(note.kind)}</span>
        <span>${escapeHtml(note.repository)}</span>
        <span>${note.milestones.map((id) => escapeHtml(id)).join(' · ')}</span>
      </div>
      <h3>${escapeHtml(note.summary)}</h3>
      <p><strong>Impact:</strong> ${escapeHtml(note.impact)}</p>
      ${verification}
      ${evidence}
      <div class="containing-commit">Recorded by ${escapeHtml(note.author)} · associated with the Git commit containing this note</div>
    </div>
  </article>`;
}

function render(roadmap, notes) {
  const release = roadmap.current_release;
  const primerFlow = roadmap.primer.dataflow.map((step) => `<article class="flow-step ${cssToken(step.owner)}">
    <div class="flow-stage">${escapeHtml(step.stage)}</div>
    <h3>${escapeHtml(step.owner)}</h3>
    <p>${escapeHtml(step.action)}</p>
    <div class="flow-boundary"><strong>Boundary</strong>${escapeHtml(step.boundary)}</div>
  </article>`).join('');
  const glossary = roadmap.glossary.map((item) => `<div><dt>${escapeHtml(item.term)}</dt><dd>${escapeHtml(item.definition)}</dd></div>`).join('');
  const statusAxes = roadmap.status_axes.map((axis) => `<article class="status-axis" id="status-${escapeHtml(axis.id)}">
    <div class="status-axis-head"><h3>${escapeHtml(axis.label)}</h3><p>${escapeHtml(axis.question)}</p></div>
    <div class="axis-states">${axis.states.map((state) => `<div class="axis-state ${cssToken(state.name)}"><strong>${escapeHtml(state.name)}</strong><span>${escapeHtml(state.meaning)}</span></div>`).join('')}</div>
  </article>`).join('');
  const milestones = roadmap.milestones.map(renderMilestone).join('\n');
  const dependencyDag = renderDependencyDag(roadmap.milestones);
  const journal = [...notes].reverse().map(renderNote).join('\n');
  const focus = list(roadmap.current_focus, 'focus-list');
  const tracks = roadmap.tracks.map((track) => `<article class="track ${escapeHtml(track.id)}">
    <div class="track-label">${escapeHtml(track.name)}</div>
    <p>${escapeHtml(track.description)}</p>
  </article>`).join('');
  const readiness = roadmap.readiness.map((item) => `<article class="readiness-card">
    <div class="readiness-head"><h3>${escapeHtml(item.name)}</h3><span class="pill gated">${statusLabel(item.status)}</span></div>
    ${list(item.requirements, 'check-list')}
  </article>`).join('');
  const success = roadmap.volunteer_success;
  const journey = success.journey.map((step) => `<article class="journey-step">
    <div class="journey-stage">${escapeHtml(step.stage)}</div>
    <h3>${escapeHtml(step.title)}</h3>
    <p>${escapeHtml(step.done_when)}</p>
  </article>`).join('');
  const testCards = success.test_cards.map((card) => `<article class="test-card">
    <div class="test-card-head"><span class="test-id">${escapeHtml(card.id)}</span><span class="availability">${escapeHtml(card.availability)}</span></div>
    <h3>${escapeHtml(card.title)}</h3>
    <div class="test-duration">${escapeHtml(card.duration)}</div>
    <dl>
      <dt>Exercise</dt><dd>${escapeHtml(card.exercise)}</dd>
      <dt>Notice</dt><dd>${escapeHtml(card.observe)}</dd>
      <dt>Evidence</dt><dd>${escapeHtml(card.proof)}</dd>
    </dl>
  </article>`).join('');
  const axesById = new Map(roadmap.status_axes.map((axis) => [axis.id, axis]));
  const receiptAxes = success.receipt_axes.map((axisId) => {
    const axis = axesById.get(axisId);
    return `<article class="receipt-axis ${escapeHtml(axis.id)}">
      <div><h4>${escapeHtml(axis.label)}</h4><p>${escapeHtml(axis.question)}</p></div>
      <div>${axis.states.map((state) => `<div class="receipt-outcome ${cssToken(state.name)}"><strong>${escapeHtml(state.name)}</strong><span>${escapeHtml(state.meaning)}</span></div>`).join('')}</div>
    </article>`;
  }).join('');
  const canaryBudget = success.first_canary_time_budget.map((phase) => `<article class="budget-phase ${cssToken(phase.commitment)}">
    <div class="budget-head"><span>${escapeHtml(phase.commitment)}</span><strong>${escapeHtml(phase.duration)}</strong></div>
    <h4>${escapeHtml(phase.phase)}</h4>
    <p>${escapeHtml(phase.detail)}</p>
  </article>`).join('');
  const proofStages = roadmap.proof_pipeline.stages.map((stage) => `<article class="proof-stage">
    <div class="proof-stage-name">${escapeHtml(stage.stage)}</div>
    <strong>${escapeHtml(stage.event)}</strong>
    <span>${escapeHtml(stage.proof)}</span>
  </article>`).join('');
  const observation = roadmap.latest_observation;
  const observationCounts = observation.conservation;
  const observationConservation = `<div class="observation-conservation" aria-label="Latest owner observation conservation result">
    <div><span>Eligible</span><strong>${escapeHtml(observationCounts.eligible.toLocaleString('en-US'))}</strong></div>
    <b aria-hidden="true">→</b>
    <div><span>Durable</span><strong>${escapeHtml(observationCounts.durable.toLocaleString('en-US'))}</strong></div>
    <b aria-hidden="true">→</b>
    <div><span>Applied</span><strong>${escapeHtml(observationCounts.applied.toLocaleString('en-US'))}</strong></div>
    <span class="observation-plus" aria-hidden="true">+</span>
    <div><span>Superseded</span><strong>${escapeHtml(observationCounts.superseded.toLocaleString('en-US'))}</strong></div>
    <b aria-hidden="true">=</b>
    <div><span>Acknowledged</span><strong>${escapeHtml(observationCounts.acknowledged.toLocaleString('en-US'))}</strong></div>
    <div class="observation-zero"><span>Pending / native</span><strong>${escapeHtml(observationCounts.pending)} / ${escapeHtml(observationCounts.native)}</strong></div>
  </div>`;
  const denominator = roadmap.golden_proof.denominator;
  const proofConservation = `<div class="conservation-flow" aria-label="Validated P7 conservation result">
    <div><span>Eligible revisions</span><strong>${escapeHtml(denominator.eligible.toLocaleString('en-US'))}</strong></div>
    <b aria-hidden="true">→</b>
    <div><span>Durably redirected</span><strong>${escapeHtml(denominator.redirected.toLocaleString('en-US'))}</strong></div>
    <b aria-hidden="true">→</b>
    <div><span>Terminally acknowledged</span><strong>${escapeHtml(denominator.acknowledged.toLocaleString('en-US'))}</strong></div>
    <div class="native-zero"><span>Eligible native sends</span><strong>${escapeHtml(denominator.native.toLocaleString('en-US'))}</strong></div>
  </div>`;
  const proofMetrics = roadmap.golden_proof.metrics.map(([label, value]) => `<div class="metric"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong></div>`).join('');
  const authorityRows = roadmap.authority_planes.map(([plane, state, boundary]) => `<tr><th scope="row">${escapeHtml(plane)}</th><td><span class="state-text ${cssToken(state)}">${escapeHtml(state)}</span></td><td>${escapeHtml(boundary)}</td></tr>`).join('');
  const links = roadmap.links.map(renderLink).join('<span aria-hidden="true"> · </span>');

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta http-equiv="Content-Security-Policy" content="default-src 'self'; style-src 'unsafe-inline';">
  <meta name="generator" content="scripts/roadmap.mjs">
  <title>${escapeHtml(roadmap.title)}</title>
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
    .truth-grid, .readiness-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
    .truth-card, .readiness-card, .proof-card, .no-go-card { padding: 22px; border: 1px solid var(--line); border-radius: 14px; background: linear-gradient(150deg, var(--panel), var(--paper)); box-shadow: var(--shadow); }
    .truth-card.proved { border-color: rgba(104,211,145,.38); }
    .truth-card.not-yet { border-color: rgba(246,196,83,.38); }
    .truth-card h3 { margin-bottom: 11px; color: var(--green); }
    .truth-card.not-yet h3 { color: var(--amber); }
    ul { margin: 10px 0 0; padding-left: 1.15rem; }
    li { margin: 7px 0; }
    .primer { margin-top: 16px; padding: 22px; border: 1px solid var(--line); border-radius: 14px; background: linear-gradient(145deg, var(--paper), var(--panel)); }
    .primer-head { display: grid; grid-template-columns: .75fr 1.25fr; gap: 22px; align-items: start; }
    .primer-head h3 { color: var(--blue); font-size: 1.22rem; }
    .primer-head p { margin: 0; color: #cad7d4; }
    .denominator { margin: 16px 0; padding: 12px 14px; border-left: 3px solid var(--green); background: var(--green-bg); color: #cce1da; font-size: .86rem; }
    .denominator strong { display: block; margin-bottom: 3px; color: var(--green); font-family: var(--mono); font-size: .67rem; text-transform: uppercase; letter-spacing: .07em; }
    .dataflow { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 9px; }
    .flow-step { position: relative; min-width: 0; padding: 13px; border: 1px solid var(--line); border-top: 3px solid var(--blue); border-radius: 8px; background: rgba(0,0,0,.16); }
    .flow-step:first-child { border-top-color: var(--muted); }
    .flow-step:not(:last-child)::after { content: "→"; position: absolute; right: -8px; top: 44px; z-index: 2; color: var(--wood); font-weight: 900; }
    .flow-stage { color: var(--wood); font-family: var(--mono); font-size: .63rem; font-weight: 900; }
    .flow-step h3 { margin-top: 5px; font-size: .86rem; }
    .flow-step p { color: #c5d2cf; font-size: .75rem; }
    .flow-boundary { margin-top: 9px; padding-top: 8px; border-top: 1px solid var(--line); color: var(--muted); font-size: .68rem; }
    .flow-boundary strong { display: block; color: var(--amber); font-family: var(--mono); font-size: .58rem; text-transform: uppercase; letter-spacing: .06em; }
    .glossary { margin-top: 14px; padding: 0; }
    .glossary summary { display: inline-block; }
    .glossary dl { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
    .glossary dl > div { padding: 10px 12px; border: 1px solid var(--line); border-radius: 7px; background: rgba(0,0,0,.12); }
    .glossary dt { color: var(--ink); font-family: var(--mono); font-size: .72rem; font-weight: 900; }
    .glossary dd { margin: 3px 0 0; color: var(--muted); font-size: .75rem; }
    .status-legend { display: grid; gap: 9px; margin: 16px 0; }
    .status-axis { display: grid; grid-template-columns: 220px 1fr; gap: 14px; padding: 13px 15px; border: 1px solid var(--line); border-radius: 9px; background: rgba(0,0,0,.13); }
    .status-axis-head h3 { font-size: .9rem; }
    .status-axis-head p { margin: 3px 0 0; color: var(--muted); font-size: .72rem; }
    .axis-states { display: flex; flex-wrap: wrap; gap: 7px; }
    .axis-state { flex: 1 1 170px; padding: 8px 10px; border-left: 3px solid var(--blue); background: var(--blue-bg); }
    .axis-state strong { display: block; font-family: var(--mono); font-size: .65rem; }
    .axis-state span { display: block; margin-top: 2px; color: var(--muted); font-size: .68rem; }
    .axis-state.not-ready, .axis-state.fault, .axis-state.degraded, .axis-state.participation-incomplete { border-left-color: var(--red); background: var(--red-bg); }
    .axis-state.ready-to-join, .axis-state.lumberjacks-active, .axis-state.flowing, .axis-state.participation-complete, .axis-state.proven { border-left-color: var(--green); background: var(--green-bg); }
    .axis-state.idle, .axis-state.pending, .axis-state.no-active-session { border-left-color: var(--amber); background: var(--amber-bg); }
    .axis-state.native-recovery { border-left-color: var(--muted); background: rgba(158,176,173,.08); }
    .tracks { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; margin-bottom: 14px; }
    .track { padding: 14px 16px; background: var(--panel); border: 1px solid var(--line); border-left: 4px solid var(--blue); border-radius: 10px; }
    .track.multiplayer { border-left-color: var(--violet); }
    .track.authority { border-left-color: var(--wood); }
    .track-label { font-weight: 800; }
    .track p { color: var(--muted); margin: 4px 0 0; font-size: .82rem; }
    .dag { padding: 18px; border: 1px solid var(--line); border-radius: 12px; background: rgba(0,0,0,.14); }
    .dag-layer { display: flex; flex-wrap: wrap; justify-content: center; gap: 10px; }
    .dag-arrow { height: 28px; display: grid; place-items: center; color: var(--muted); font-family: var(--mono); font-weight: 900; }
    .dag-node { min-width: 175px; max-width: 280px; flex: 1 1 190px; display: grid; grid-template-columns: auto 1fr; gap: 3px 9px; padding: 11px 13px; color: var(--ink); text-decoration: none; border: 1px solid var(--line); border-top: 3px solid var(--blue); border-radius: 8px; background: var(--panel); }
    .dag-node.multiplayer { border-top-color: var(--violet); }
    .dag-node.authority { border-top-color: var(--wood); }
    .dag-node.gated { opacity: .78; }
    .dag-node.complete { border-top-color: var(--green); }
    .dag-node:hover { color: var(--ink); border-color: var(--blue); }
    .dag-id { grid-row: 1 / span 2; color: var(--wood); font-family: var(--mono); font-size: .72rem; font-weight: 900; }
    .dag-node strong { font-size: .8rem; }
    .dag-node > span:last-child { color: var(--muted); font-family: var(--mono); font-size: .63rem; }
    .milestone-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; align-items: start; }
    .milestone { padding: 22px; border: 1px solid var(--line); border-top: 4px solid var(--blue); border-radius: 13px; background: linear-gradient(155deg, var(--panel), var(--paper)); }
    .milestone.active { border-top-color: var(--amber); }
    .milestone.gated { border-top-color: var(--amber); }
    .milestone.later { border-top-color: var(--muted); }
    .milestone.complete { border-top-color: var(--green); }
    .milestone-head, .readiness-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 14px; }
    .milestone-head > div { display: flex; align-items: baseline; gap: 10px; }
    .milestone-id { color: var(--wood); font-family: var(--mono); font-size: .8rem; font-weight: 800; }
    .pill { display: inline-flex; align-items: center; flex: 0 0 auto; padding: 4px 8px; border-radius: 999px; font-family: var(--mono); font-size: .67rem; font-weight: 900; letter-spacing: .06em; border: 1px solid currentColor; }
    .pill.active { color: var(--amber); background: var(--amber-bg); }
    .pill.queued { color: var(--blue); background: var(--blue-bg); }
    .pill.gated { color: var(--amber); background: var(--amber-bg); }
    .pill.later { color: var(--muted); background: rgba(158,176,173,.08); }
    .pill.complete { color: var(--green); background: var(--green-bg); }
    .meta-line { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin: 12px 0; color: var(--muted); font-family: var(--mono); font-size: .72rem; text-transform: uppercase; }
    .deps { display: flex; align-items: center; gap: 5px; }
    .dep { padding: 1px 5px; background: rgba(255,255,255,.06); border-radius: 4px; color: var(--ink); }
    .milestone > p { color: #cad7d4; }
    .ownership { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin: 12px 0; }
    .ownership > span { padding: 9px 10px; border: 1px solid var(--line); border-radius: 7px; color: var(--muted); font-size: .75rem; }
    .ownership strong { display: block; margin-bottom: 2px; color: var(--ink); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; letter-spacing: .06em; }
    .work-list { color: var(--muted); font-size: .9rem; }
    .gate { margin-top: 18px; padding: 14px; display: grid; gap: 5px; border-left: 3px solid var(--amber); background: var(--amber-bg); font-size: .88rem; }
    .gate strong { color: var(--amber); text-transform: uppercase; letter-spacing: .08em; font-size: .7rem; }
    .gate.closed { border-left-color: var(--green); background: var(--green-bg); }
    .gate.closed strong { color: var(--green); }
    .exit-criteria { margin-top: 3px; padding-left: 1rem; }
    .exit-criteria li { margin: 4px 0; }
    .milestone-evidence { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin-top: 10px; font-size: .76rem; }
    .milestone-evidence > div { display: grid; align-content: start; gap: 4px; padding: 10px; border: 1px solid var(--line); border-radius: 7px; background: rgba(0,0,0,.13); }
    .milestone-evidence strong { color: var(--muted); font-family: var(--mono); font-size: .65rem; text-transform: uppercase; letter-spacing: .06em; }
    .milestone-evidence ul { margin: 2px 0 0; padding-left: 1rem; }
    .milestone-evidence li { margin: 3px 0; }
    .empty-evidence { color: #70827e; font-style: italic; }
    .readiness-head { margin-bottom: 12px; }
    .check-list { list-style: none; padding: 0; color: var(--muted); }
    .check-list li { position: relative; padding-left: 24px; }
    .check-list li::before { content: "□"; position: absolute; left: 0; color: var(--amber); font-family: var(--mono); }
    .success-contract { margin-top: 28px; padding: 24px; border: 1px solid rgba(104,211,145,.34); border-radius: 14px; background: linear-gradient(145deg, rgba(104,211,145,.07), var(--paper) 34%); }
    .success-contract > h3 { margin-bottom: 7px; color: var(--green); font-size: 1.35rem; }
    .success-headline { color: var(--green); font-family: var(--mono); font-size: .85rem; font-weight: 900; letter-spacing: .04em; }
    .success-definition { max-width: 920px; color: #c8d7d3; }
    .catalog-state { display: inline-block; margin: 0 0 4px; padding: 6px 9px; border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-family: var(--mono); font-size: .68rem; }
    .journey-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; margin: 20px 0; }
    .journey-step { position: relative; padding: 16px; border: 1px solid var(--line); border-radius: 9px; background: rgba(0,0,0,.18); }
    .journey-step:not(:last-child)::after { content: "→"; position: absolute; right: -10px; top: 50%; z-index: 2; color: var(--wood); font-weight: 900; }
    .journey-stage { margin-bottom: 7px; color: var(--wood); font-family: var(--mono); font-size: .7rem; font-weight: 900; }
    .journey-step h3 { font-size: .98rem; }
    .journey-step p { margin-bottom: 0; color: var(--muted); font-size: .82rem; }
    .receipt-axis-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin: 18px 0 24px; }
    .receipt-axis { padding: 14px; border: 1px solid var(--line); border-radius: 9px; background: rgba(0,0,0,.15); }
    .receipt-axis h4 { margin: 0; font-size: .88rem; }
    .receipt-axis > div:first-child p { margin: 3px 0 10px; color: var(--muted); font-size: .73rem; }
    .receipt-outcome { display: grid; gap: 2px; padding: 9px 11px; border-left: 3px solid var(--blue); background: var(--blue-bg); }
    .receipt-outcome + .receipt-outcome { margin-top: 6px; }
    .receipt-outcome strong { font-family: var(--mono); font-size: .67rem; }
    .receipt-outcome span { color: var(--muted); font-size: .74rem; }
    .receipt-outcome.participation-complete, .receipt-outcome.proven { border-left-color: var(--green); background: var(--green-bg); }
    .receipt-outcome.participation-incomplete, .receipt-outcome.degraded { border-left-color: var(--red); background: var(--red-bg); }
    .receipt-outcome.pending, .receipt-outcome.inconclusive { border-left-color: var(--amber); background: var(--amber-bg); }
    .budget-summary { margin: 18px 0 24px; }
    .budget-summary > h3 { margin-bottom: 3px; }
    .budget-summary > p { margin-top: 0; color: var(--muted); font-size: .82rem; }
    .budget-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; }
    .budget-phase { padding: 12px; border: 1px solid rgba(117,201,241,.28); border-radius: 8px; background: var(--blue-bg); }
    .budget-phase.optional-opt-in { border-color: var(--line); background: rgba(158,176,173,.07); }
    .budget-head { display: flex; justify-content: space-between; gap: 8px; color: var(--blue); font-family: var(--mono); font-size: .64rem; text-transform: uppercase; }
    .budget-phase.optional-opt-in .budget-head { color: var(--muted); }
    .budget-phase h4 { margin: 9px 0 3px; font-size: .86rem; }
    .budget-phase p { margin: 0; color: var(--muted); font-size: .73rem; }
    .subhead { margin: 25px 0 6px; color: var(--ink); }
    .subcopy { margin-top: 0; color: var(--muted); }
    .test-card-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(275px, 1fr)); gap: 10px; }
    .test-card { padding: 15px; border: 1px solid var(--line); border-radius: 9px; background: var(--panel); }
    .test-card-head { display: flex; justify-content: space-between; gap: 8px; align-items: center; }
    .test-id { color: var(--wood); font-family: var(--mono); font-weight: 900; }
    .availability { padding: 2px 6px; border-radius: 999px; color: var(--muted); background: rgba(255,255,255,.05); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; }
    .test-card h3 { margin-top: 8px; font-size: 1rem; }
    .test-duration { color: var(--blue); font-family: var(--mono); font-size: .7rem; }
    .test-card dl { display: grid; grid-template-columns: 56px 1fr; gap: 5px 8px; margin: 12px 0 0; font-size: .78rem; }
    .test-card dt { color: var(--ink); font-weight: 800; }
    .test-card dd { margin: 0; color: var(--muted); }
    .receipt-box { margin-top: 16px; padding: 16px 18px; border: 1px solid rgba(104,211,145,.28); border-radius: 9px; background: var(--green-bg); }
    .receipt-box h3 { color: var(--green); }
    .receipt-box ul { columns: 2; column-gap: 32px; color: #c8d7d3; font-size: .84rem; }
    .latest-observation { margin-bottom: 16px; padding: 22px; border: 1px solid rgba(246,196,83,.4); border-radius: 14px; background: linear-gradient(145deg, rgba(246,196,83,.07), var(--paper) 38%); box-shadow: var(--shadow); }
    .observation-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 18px; }
    .observation-kind { color: var(--wood); font-family: var(--mono); font-size: .65rem; font-weight: 900; letter-spacing: .09em; }
    .observation-head h3 { margin: 5px 0 3px; font-size: 1.25rem; }
    .observation-meta { margin: 0; color: var(--muted); font-size: .77rem; }
    .observation-informs { color: var(--muted); font-family: var(--mono); font-size: .68rem; white-space: nowrap; }
    .observation-axes { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin: 16px 0; }
    .observation-axis { padding: 12px 14px; border-left: 3px solid var(--green); background: var(--green-bg); }
    .observation-axis.verdict { border-left-color: var(--amber); background: var(--amber-bg); }
    .observation-axis span { display: block; color: var(--muted); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; }
    .observation-axis strong { display: block; margin-top: 2px; font-family: var(--mono); font-size: .9rem; }
    .observation-conservation { display: flex; flex-wrap: wrap; align-items: stretch; gap: 6px; padding: 12px; border: 1px solid var(--line); border-radius: 9px; background: rgba(0,0,0,.16); }
    .observation-conservation > div { flex: 1 1 105px; padding: 8px 9px; border-radius: 6px; background: var(--panel-2); }
    .observation-conservation > div span { display: block; color: var(--muted); font-size: .62rem; }
    .observation-conservation > div strong { display: block; font-family: var(--mono); font-size: .92rem; }
    .observation-conservation > b, .observation-plus { align-self: center; color: var(--wood); font-family: var(--mono); }
    .observation-conservation .observation-zero { border: 1px solid rgba(104,211,145,.32); background: var(--green-bg); }
    .observation-explanation { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin-top: 10px; }
    .observation-explanation > div { padding: 11px 13px; border: 1px solid var(--line); border-radius: 8px; color: var(--muted); font-size: .78rem; }
    .observation-explanation strong { display: block; margin-bottom: 3px; color: var(--ink); font-family: var(--mono); font-size: .65rem; text-transform: uppercase; }
    .observation-telemetry { margin: 10px 0 0; color: var(--muted); font-size: .77rem; }
    .observation-evidence { margin-top: 10px; padding: 10px 12px; border-left: 3px solid var(--amber); background: var(--amber-bg); color: #d7cba9; font-size: .74rem; }
    .observation-evidence strong { display: block; color: var(--amber); font-family: var(--mono); font-size: .62rem; text-transform: uppercase; }
    .proof-layout { display: grid; grid-template-columns: .85fr 1.15fr; gap: 16px; }
    .proof-card h3 { color: var(--green); }
    .proof-scope { margin: 8px 0 18px; color: var(--muted); }
    .proof-pipeline-card { margin-bottom: 16px; padding: 22px; border: 1px solid var(--line); border-radius: 14px; background: linear-gradient(150deg, var(--panel), var(--paper)); }
    .proof-pipeline-card > h3 { color: var(--blue); }
    .proof-pipeline-card > p { color: var(--muted); }
    .proof-stage-grid { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 7px; }
    .proof-stage { position: relative; padding: 11px; border: 1px solid var(--line); border-radius: 8px; background: rgba(0,0,0,.16); }
    .proof-stage:not(:last-child)::after { content: "→"; position: absolute; top: 38px; right: -7px; z-index: 2; color: var(--wood); font-weight: 900; }
    .proof-stage-name { color: var(--wood); font-family: var(--mono); font-size: .61rem; text-transform: uppercase; }
    .proof-stage strong { display: block; margin: 5px 0; font-size: .72rem; }
    .proof-stage span { display: block; color: var(--muted); font-size: .67rem; }
    .equation-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin-top: 10px; }
    .equation-box { padding: 10px 12px; border: 1px solid var(--line); border-radius: 7px; background: rgba(0,0,0,.12); }
    .equation-box strong { color: var(--muted); font-family: var(--mono); font-size: .63rem; text-transform: uppercase; }
    .equation-box ul { margin-top: 4px; color: #c5d2cf; font-family: var(--mono); font-size: .68rem; }
    .conservation-flow { display: grid; grid-template-columns: 1fr auto 1fr auto 1fr; gap: 8px; align-items: stretch; margin-bottom: 14px; }
    .conservation-flow > div { display: grid; place-content: center; min-height: 82px; padding: 10px; text-align: center; border: 1px solid rgba(104,211,145,.35); border-radius: 8px; background: var(--green-bg); }
    .conservation-flow > b { display: grid; place-items: center; color: var(--wood); }
    .conservation-flow span { color: var(--muted); font-size: .68rem; }
    .conservation-flow strong { font-family: var(--mono); font-size: 1.25rem; }
    .conservation-flow .native-zero { grid-column: 1 / -1; min-height: auto; display: flex; justify-content: center; gap: 10px; border-color: var(--line); background: rgba(0,0,0,.14); }
    .proof-denominator { color: #c8d7d3; font-size: .82rem; }
    .metric-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
    .metric { padding: 12px; background: rgba(0,0,0,.2); border: 1px solid rgba(255,255,255,.05); border-radius: 8px; }
    .metric span { display: block; color: var(--muted); font-size: .72rem; }
    .metric strong { display: block; margin-top: 2px; font-family: var(--mono); font-size: 1.05rem; }
    .no-go-card h3 { color: var(--red); }
    .no-go-card ul { color: #d2bfbc; }
    .publication { margin-top: 12px; padding: 11px 13px; border-left: 3px solid var(--amber); background: var(--amber-bg); color: #d7cba9; font-size: .76rem; }
    .publication strong { display: block; color: var(--amber); font-family: var(--mono); font-size: .64rem; text-transform: uppercase; letter-spacing: .06em; }
    .table-wrap { overflow-x: auto; border: 1px solid var(--line); border-radius: 12px; }
    table { width: 100%; border-collapse: collapse; min-width: 720px; background: var(--paper); }
    th, td { padding: 13px 15px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
    thead th { color: var(--muted); font-size: .72rem; text-transform: uppercase; letter-spacing: .08em; background: var(--panel); }
    tbody th { width: 25%; }
    tbody tr:last-child th, tbody tr:last-child td { border-bottom: 0; }
    .state-text { display: inline-block; padding: 3px 7px; border: 1px solid currentColor; border-radius: 999px; color: var(--amber); background: var(--amber-bg); font-family: var(--mono); font-size: .72rem; font-weight: 800; white-space: nowrap; }
    .state-text.proved-for-one-client { color: var(--green); background: var(--green-bg); }
    .state-text.lumberjacks-adapter { color: var(--blue); background: var(--blue-bg); }
    .state-text.native, .state-text.native-by-design { color: var(--muted); background: rgba(158,176,173,.08); }
    .journal { max-width: 980px; }
    .journal-entry { position: relative; display: grid; grid-template-columns: 22px 1fr; gap: 15px; }
    .journal-entry + .journal-entry { margin-top: 22px; }
    .journal-rail { position: relative; }
    .journal-rail::before { content: ""; position: absolute; left: 8px; top: 5px; bottom: -28px; width: 1px; background: var(--line); }
    .journal-rail::after { content: ""; position: absolute; left: 3px; top: 5px; width: 11px; height: 11px; border-radius: 50%; background: var(--wood); box-shadow: 0 0 0 4px rgba(215,168,110,.12); }
    .journal-entry:last-child .journal-rail::before { bottom: 0; }
    .journal-body { padding: 18px; border: 1px solid var(--line); border-radius: 10px; background: var(--panel); }
    .journal-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; color: var(--muted); font-family: var(--mono); font-size: .69rem; }
    .pill.note-kind { color: var(--blue); background: var(--blue-bg); }
    .journal-body h3 { margin: 10px 0 8px; }
    .journal-body p { margin: 0; color: #c5d2cf; }
    details { margin-top: 12px; color: var(--muted); }
    summary { cursor: pointer; color: var(--blue); }
    .journal-evidence { margin-top: 12px; color: var(--muted); font-size: .78rem; }
    .journal-evidence code { display: inline-block; margin: 3px 4px 0 0; padding: 3px 6px; border: 1px solid var(--line); border-radius: 4px; }
    .containing-commit { margin-top: 12px; color: #728783; font-size: .7rem; }
    footer { padding: 38px 0 60px; color: var(--muted); font-size: .82rem; }
    .footer-links { margin-bottom: 10px; }
    .generated { color: #6f817e; font-family: var(--mono); font-size: .7rem; }
    @media (max-width: 820px) {
      .section-head, .proof-layout, .primer-head, .status-axis, .observation-explanation { grid-template-columns: 1fr; gap: 10px; }
      .truth-grid, .readiness-grid, .milestone-grid, .tracks { grid-template-columns: 1fr; }
      .dataflow { grid-template-columns: 1fr; }
      .flow-step::after, .proof-stage::after { display: none; }
      .proof-stage-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .budget-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .journey-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .journey-step::after { display: none; }
      .topbar-inner { align-items: flex-start; padding: 12px 0; }
      nav { gap: 8px; }
      .hero { padding-top: 48px; }
    }
    @media (max-width: 560px) {
      .wrap { width: min(1180px, calc(100% - 20px)); }
      .topbar-inner { display: block; }
      nav { justify-content: flex-start; margin-top: 8px; }
      .metric-grid { grid-template-columns: 1fr; }
      .journey-grid, .receipt-axis-grid, .budget-grid, .proof-stage-grid, .equation-grid, .observation-axes { grid-template-columns: 1fr; }
      .observation-head { display: grid; }
      .observation-informs { white-space: normal; }
      .observation-conservation > b, .observation-plus { display: none; }
      .glossary dl { grid-template-columns: 1fr; }
      .conservation-flow { grid-template-columns: 1fr; }
      .conservation-flow > b { transform: rotate(90deg); }
      .conservation-flow .native-zero { grid-column: auto; }
      .receipt-box ul { columns: 1; }
      .milestone-evidence, .ownership { grid-template-columns: 1fr; }
      .milestone-head { display: grid; }
    }
    @media print {
      :root { color-scheme: light; --bg: #fff; --paper: #fff; --panel: #f6f7f7; --panel-2: #eee; --line: #ccd2d0; --ink: #15201d; --muted: #53635e; }
      body { background: #fff; }
      .topbar { position: static; background: #fff; }
      .milestone, .truth-card, .proof-card, .no-go-card, .readiness-card { break-inside: avoid; box-shadow: none; }
      nav { display: none; }
      a { color: #075985; }
    }
  </style>
</head>
<body>
  <!-- GENERATED FILE: edit docs/roadmap sources and run npm run roadmap:render -->
  <header class="topbar">
    <div class="wrap topbar-inner">
      <div class="brand"><span class="mark">LJ</span><span>Valheim volunteer roadmap</span></div>
      <nav aria-label="Gateway surfaces">
        <a href="/roadmap" aria-current="page">Roadmap</a>
        <a href="/community">Community</a>
        <a href="/networksense">NetworkSense</a>
        <a href="/events">Events</a>
        <a href="/testing">Testing</a>
      </nav>
    </div>
  </header>

  <main>
    <div class="wrap hero">
      <div class="eyebrow">Comfy × Valheim · Lumberjacks P7</div>
      <h1>${escapeHtml(roadmap.title.replace('Comfy × Valheim — ', ''))}</h1>
      <div class="headline">${escapeHtml(roadmap.headline)}</div>
      <p class="lede">${escapeHtml(roadmap.claim)}</p>
      <div class="hero-meta">
        <span>Updated ${escapeHtml(roadmap.updated_at)}</span>
        <span>${escapeHtml(release.mod)}</span>
        <span>${escapeHtml(release.deployment)}</span>
        <span>authoritative consumer ceiling ${escapeHtml(release.authoritative_consumer_ceiling)}</span>
        <span>window ${escapeHtml(release.window)}</span>
      </div>
    </div>

    <section>
      <div class="wrap">
        <div class="section-head"><div class="section-number">01 · CURRENT TRUTH</div><div><h2>Protect the proof; earn the platform.</h2><p class="section-copy">The validated, hash-recorded one-client result is real. Volunteer readiness and concurrent correctness have separate, visible gates.</p></div></div>
        <div class="truth-grid">
          <article class="truth-card proved"><h3>Proved now</h3><p>One enrolled client closed the complete observed all-prefab ZDO window through Lumberjacks priority delivery with exact durable accounting.</p><p><a href="#proof">See the validated result ↓</a></p></article>
          <article class="truth-card not-yet"><h3>Not proved yet</h3>${list(['Safe public credential transport and authoritative admission.', 'A clean no-hand-edit package for a non-developer.', 'Per-session and per-recipient retained proof.', 'Two simultaneous consumers with isolated pending and ACK state.', 'Lumberjacks-owned candidate relevance, simulation, or non-ZDO RPCs.'])}</article>
        </div>
        <div class="truth-card" style="margin-top:16px"><h3 style="color:var(--blue)">Next actions</h3>${focus}</div>
        <article class="primer">
          <div class="primer-head"><h3>${escapeHtml(roadmap.primer.title)}</h3><p>${escapeHtml(roadmap.primer.summary)}</p></div>
          <div class="denominator"><strong>The exact denominator</strong>${escapeHtml(roadmap.primer.denominator)}</div>
          <div class="dataflow" aria-label="Current ZDO cutover dataflow">${primerFlow}</div>
          <details class="glossary"><summary>Terms used on this page</summary><dl>${glossary}</dl></details>
        </article>
      </div>
    </section>

    <section>
      <div class="wrap">
        <div class="section-head"><div class="section-number">02 · CRITICAL PATH</div><div><h2>Two lanes, one honest widening point.</h2><p class="section-copy">The graph is generated from each milestone's declared dependencies. Build the single-volunteer experience without waiting for the queue redesign; keep capacity at one until recipient isolation passes.</p></div></div>
        <div class="tracks">${tracks}</div>
        ${dependencyDag}
      </div>
    </section>

    <section>
      <div class="wrap">
        <div class="section-head"><div class="section-number">03 · MILESTONES</div><div><h2>State is a gate, not a percentage.</h2><p class="section-copy">A milestone moves only when its exit statement is supported by reproducible evidence.</p></div></div>
        <div class="milestone-grid">${milestones}</div>
      </div>
    </section>

    <section>
      <div class="wrap">
        <div class="section-head"><div class="section-number">04 · READINESS</div><div><h2>Who may we responsibly invite—and how do they know it counted?</h2><p class="section-copy">Host preflight happens before an invitation is sent. A known-red deployment never becomes volunteer troubleshooting, and joining alone is not participation completion.</p></div></div>
        <div class="status-legend" aria-label="Independent roadmap and test status axes">${statusAxes}</div>
        <div class="readiness-grid">${readiness}</div>
        <div class="success-contract" id="success">
          <div class="success-headline">${escapeHtml(success.headline)}</div>
          <h3>Volunteer participation contract</h3>
          <p class="success-definition">${escapeHtml(success.definition)}</p>
          <p class="catalog-state"><strong>${escapeHtml(success.catalog_revision)}</strong> · ${escapeHtml(success.availability)}</p>
          <div class="journey-grid">${journey}</div>
          <h3 class="subhead">Two independent receipt outcomes</h3>
          <p class="subcopy">Participation records whether the volunteer's effort counted. The system verdict separately records what the sealed networking run proved.</p>
          <div class="receipt-axis-grid">${receiptAxes}</div>
          <div class="budget-summary">
            <h3>First-canary time budget</h3>
            <p>The required commitment is explicit; the ordinary-play extension is optional and declining it never reduces participation status.</p>
            <div class="budget-grid">${canaryBudget}</div>
          </div>
          <h3 class="subhead">Versioned test cards</h3>
          <p class="subcopy">The invite assigns a small subset. Each card tells the volunteer what to do, what to notice, how long it takes, and what the instrumentation proves.</p>
          <div class="test-card-grid">${testCards}</div>
          <div class="receipt-box"><h3>Participation receipt</h3>${list(success.receipt)}</div>
        </div>
      </div>
    </section>

    <section id="proof">
      <div class="wrap">
        <div class="section-head"><div class="section-number">05 · PROOF & RISK</div><div><h2>Validated result beside the remaining no-go facts.</h2><p class="section-copy">The latest owner observation corroborates the data path but does not replace or widen the historical 83,220-revision baseline. Live delivery and the formal sealed verdict remain visibly separate.</p></div></div>
        <article class="latest-observation" id="observation-${escapeHtml(observation.id)}">
          <div class="observation-head">
            <div><div class="observation-kind">${escapeHtml(observation.kind)}</div><h3>${escapeHtml(observation.label)}</h3><p class="observation-meta">${escapeHtml(observation.scope)} · ${escapeHtml(observation.captured_at)}</p></div>
            <div class="observation-informs">informs ${observation.informs.map((id) => escapeHtml(id)).join(' + ')}</div>
          </div>
          <div class="observation-axes">
            <div class="observation-axis"><span>Observed delivery</span><strong>${escapeHtml(observation.delivery_result)}</strong></div>
            <div class="observation-axis verdict"><span>Formal system verdict</span><strong>${escapeHtml(observation.system_verdict)}</strong></div>
          </div>
          ${observationConservation}
          <div class="observation-explanation">
            <div><strong>Why not PROVEN</strong>${escapeHtml(observation.verdict_reason)}</div>
            <div><strong>Milestone effect</strong>${escapeHtml(observation.milestone_effect)}</div>
          </div>
          <p class="observation-telemetry"><strong>Operational note:</strong> ${escapeHtml(observation.operational_observation)}</p>
          <div class="observation-evidence"><strong>Evidence · ${escapeHtml(observation.evidence.status)}</strong>${escapeHtml(observation.evidence.label)} — ${escapeHtml(observation.evidence.detail)}</div>
        </article>
        <article class="proof-pipeline-card">
          <h3>What definitive traffic proof must conserve</h3>
          <p>Each eligible revision advances through an accountable route and terminal outcome. Retries do not create new unique work, and a heartbeat alone cannot close this chain.</p>
          <div class="proof-stage-grid">${proofStages}</div>
          <div class="equation-grid">
            <div class="equation-box"><strong>Conservation equations</strong>${list(roadmap.proof_pipeline.conservation)}</div>
            <div class="equation-box"><strong>Strict closure requires</strong>${list(roadmap.proof_pipeline.strict_closure)}</div>
          </div>
        </article>
        <div class="proof-layout">
          <article class="proof-card">
            <h3>${escapeHtml(roadmap.golden_proof.label)}</h3>
            <p class="proof-scope">${escapeHtml(roadmap.golden_proof.scope)} · ${escapeHtml(roadmap.golden_proof.captured_at)}</p>
            ${proofConservation}
            <p class="proof-denominator">${escapeHtml(denominator.statement)}</p>
            <div class="metric-grid">${proofMetrics}</div>
            <div class="publication"><strong>Publication · ${escapeHtml(roadmap.golden_proof.publication.status)}</strong>${escapeHtml(roadmap.golden_proof.publication.detail)}</div>
          </article>
          <article class="no-go-card">
            <h3>Known no-go findings</h3>
            ${list(roadmap.no_go)}
          </article>
        </div>
      </div>
    </section>

    <section>
      <div class="wrap">
        <div class="section-head"><div class="section-number">06 · AUTHORITY</div><div><h2>What Lumberjacks owns—and what it does not.</h2><p class="section-copy">The claim grows one authority plane at a time. Compatibility dependencies remain visible.</p></div></div>
        <div class="table-wrap"><table><thead><tr><th>Plane</th><th>Current state</th><th>Boundary</th></tr></thead><tbody>${authorityRows}</tbody></table></div>
      </div>
    </section>

    <section>
      <div class="wrap">
        <div class="section-head"><div class="section-number">07 · COMMIT NOTES</div><div><h2>Implementation journal</h2><p class="section-copy">Every non-merge commit appends one note. Newest entries appear first; history remains append-only.</p></div></div>
        <div class="journal">${journal}</div>
      </div>
    </section>
  </main>

  <footer class="wrap">
    <div class="footer-links">${links}</div>
    <div>Update milestone truth and add a journal entry in the same commit as the work it describes.</div>
    <div class="generated">Generated deterministically from ${escapeHtml(roadmapRelative)} + ${escapeHtml(notesRelative)} · do not hand-edit this file.</div>
  </footer>
</body>
</html>
`;
}

function writeRendered(roadmap, notes) {
  const html = render(roadmap, notes);
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, html, 'utf8');
  return html;
}

function parseOptions(values) {
  const options = new Map();
  for (let index = 0; index < values.length; index += 1) {
    const token = values[index];
    if (!token.startsWith('--')) fail(`unexpected argument: ${token}`);
    const name = token.slice(2);
    const value = values[index + 1];
    if (!value || value.startsWith('--')) fail(`missing value for --${name}`);
    if (!options.has(name)) options.set(name, []);
    options.get(name).push(value);
    index += 1;
  }
  return options;
}

function one(options, name, fallback = undefined) {
  const values = options.get(name);
  if (!values || values.length === 0) return fallback;
  if (values.length > 1) fail(`--${name} may be provided only once`);
  return values[0];
}

function many(options, name) {
  return options.get(name) ?? [];
}

function slug(value) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 48) || 'roadmap-note';
}

function addNote(args) {
  const options = parseOptions(args);
  const summary = one(options, 'summary');
  const impact = one(options, 'impact');
  const kind = one(options, 'kind');
  const milestones = many(options, 'milestone');
  if (!summary) fail('--summary is required');
  if (!impact) fail('--impact is required');
  if (!kind || !noteKinds.has(kind)) fail(`--kind must be one of: ${[...noteKinds].join(', ')}`);
  if (milestones.length === 0) fail('at least one --milestone is required');

  const { roadmap, notes } = readSources();
  const at = one(options, 'at', new Date().toISOString());
  if (Number.isNaN(Date.parse(at))) fail('--at must be an ISO timestamp');
  const stamp = at.replace(/[^0-9]/g, '').slice(0, 14);
  const note = {
    schema_version: 1,
    id: one(options, 'id', `${stamp}-${slug(summary)}`),
    at,
    author: one(options, 'author', 'Codex'),
    repository: one(options, 'repository', 'Lumberjacks'),
    milestones,
    kind,
    summary,
    impact,
    verification: many(options, 'verification'),
    evidence: many(options, 'evidence'),
  };
  const nextNotes = [...notes, note];
  roadmap.updated_at = at;
  const roadmapRaw = `${JSON.stringify(roadmap, null, 2)}\n`;
  const notesRaw = `${nextNotes.map((item) => JSON.stringify(item)).join('\n')}\n`;
  validate(roadmap, nextNotes, `${roadmapRaw}\n${notesRaw}`);
  fs.writeFileSync(roadmapPath, roadmapRaw, 'utf8');
  fs.writeFileSync(notesPath, notesRaw, 'utf8');
  writeRendered(roadmap, nextNotes);
  console.log(`Added roadmap note ${note.id} and regenerated ${outputRelative}`);
}

function git(args, options = {}) {
  return execFileSync('git', args, { cwd: repoRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...options });
}

function checkStaged() {
  const staged = git(['diff', '--cached', '--name-only', '--diff-filter=ACMR'])
    .split(/\r?\n/)
    .filter(Boolean)
    .map((item) => item.replaceAll('\\', '/'));
  if (staged.length === 0) fail('no staged files; stage the intended commit before using --staged');
  if (!staged.includes(notesRelative)) fail(`every non-merge commit must stage an appended ${notesRelative} record`);
  if (!staged.includes(outputRelative)) fail(`every roadmap note must stage regenerated ${outputRelative}`);

  const diff = git(['diff', '--cached', '--no-color', '--unified=0', '--', notesRelative]);
  const additions = diff.split(/\r?\n/).filter((line) => line.startsWith('+{'));
  const removals = diff.split(/\r?\n/).filter((line) => line.startsWith('-{'));
  if (additions.length !== 1) fail(`staged commit must append exactly one journal record; found ${additions.length}`);
  if (removals.length !== 0) fail('historic roadmap journal records are append-only and may not be removed or modified');

  const stagedHtml = git(['show', `:${outputRelative}`]);
  const workingHtml = fs.readFileSync(outputPath, 'utf8');
  if (stagedHtml !== workingHtml) fail(`${outputRelative} has staged/working-tree drift; regenerate and stage it again`);
}

function check(args) {
  const allowed = new Set(['--staged']);
  for (const arg of args) if (!allowed.has(arg)) fail(`unknown check option ${arg}`);
  const { roadmap, notes, roadmapRaw, notesRaw } = readSources();
  validate(roadmap, notes, `${roadmapRaw}\n${notesRaw}`);
  const expected = render(roadmap, notes);
  const actual = fs.readFileSync(outputPath, 'utf8');
  if (actual !== expected) fail(`${outputRelative} is stale; run npm run roadmap:render`);
  if (!actual.includes('id="M0"') || !actual.includes('id="proof"') || !actual.includes('07 · COMMIT NOTES')) {
    fail('generated roadmap is missing a required section');
  }
  if (/<(?:script|link)\b/i.test(actual)) fail('roadmap HTML must remain self-contained and script-free');
  if (args.includes('--staged')) checkStaged();
  console.log(`Roadmap OK: ${roadmap.milestones.length} milestones, ${notes.length} append-only notes, generated HTML current${args.includes('--staged') ? ', staged commit compliant' : ''}.`);
}

function renderCommand() {
  const { roadmap, notes, roadmapRaw, notesRaw } = readSources();
  validate(roadmap, notes, `${roadmapRaw}\n${notesRaw}`);
  writeRendered(roadmap, notes);
  console.log(`Rendered ${outputRelative} from ${roadmapRelative} and ${notesRelative}`);
}

function usage() {
  console.log(`Usage:
  node scripts/roadmap.mjs render
  node scripts/roadmap.mjs check [--staged]
  node scripts/roadmap.mjs note --milestone M0 --kind implementation --summary "..." --impact "..." [--verification "..."] [--evidence "..."]`);
}

try {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'render') renderCommand();
  else if (command === 'check') check(args);
  else if (command === 'note') addNote(args);
  else {
    usage();
    process.exitCode = command ? 1 : 0;
  }
} catch (error) {
  console.error(`roadmap: ${error.message}`);
  process.exitCode = 1;
}
