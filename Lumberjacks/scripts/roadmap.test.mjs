// Guard tests for the roadmap/journal generator (node --test scripts/roadmap.test.mjs).
//
// commit-notes.jsonl is this repo's high-frequency decision record — 297 entries whose
// entire contract lived as imperative checks inside roadmap.mjs with NO schema, NO
// validator and NO test coverage, while workbench.mjs beside it had 20+. A guard that
// cannot be shown to fail is decoration, so every negative test below mutates a valid
// fixture, asserts the SPECIFIC failure message, and proves the valid case still passes.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { validate, noteKinds } from './roadmap.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const schemaPath = path.join(repoRoot, 'docs/roadmap/commit-note.schema.json');

/// The real roadmap is the fixture. validate() enforces ~30 roadmap-level invariants
/// before it reaches the notes, and a hand-rolled minimal copy would drift out of date
/// silently. Using the live document means these tests exercise the actual contract and
/// the fixture can never disagree with it.
const liveRoadmap = JSON.parse(
  fs.readFileSync(path.join(repoRoot, 'docs/roadmap/valheim-volunteer-roadmap.json'), 'utf8'),
);
const knownMilestone = liveRoadmap.milestones[0].id;

function baseRoadmap() {
  return JSON.parse(JSON.stringify(liveRoadmap));
}

function baseNote(overrides = {}) {
  return {
    schema_version: 1,
    id: 'note-1',
    at: '2026-07-30T00:00:00Z',
    author: 'claude',
    repository: 'baseline',
    milestones: [knownMilestone],
    kind: 'implementation',
    summary: 'Did the thing',
    impact: 'The thing is done',
    verification: ['ran the tests'],
    evidence: ['docs/roadmap/commit-notes.jsonl'],
    ...overrides,
  };
}

function expectFailure(notes, fragment, roadmap = baseRoadmap()) {
  assert.throws(
    () => validate(roadmap, notes),
    (error) => {
      assert.match(error.message, fragment);
      return true;
    },
    `expected a failure matching ${fragment}`,
  );
}

test('a well-formed journal record validates', () => {
  assert.doesNotThrow(() => validate(baseRoadmap(), [baseNote()]));
});

test('schema_version is pinned to 1', () => {
  expectFailure([baseNote({ schema_version: 2 })], /schema_version must be 1/);
});

test('duplicate note ids are rejected', () => {
  expectFailure([baseNote(), baseNote()], /duplicate note id note-1/);
});

test('the journal must stay chronological — this is what makes a historical backfill impossible', () => {
  const first = baseNote({ id: 'a', at: '2026-07-30T02:00:00Z' });
  const second = baseNote({ id: 'b', at: '2026-07-30T01:00:00Z' });
  expectFailure([first, second], /append-only chronological records/);
});

test('a non-ISO timestamp is rejected', () => {
  expectFailure([baseNote({ at: 'yesterday' })], /at must be an ISO timestamp/);
});

test('an unknown kind is rejected, naming the offending value', () => {
  expectFailure([baseNote({ kind: 'pending' })], /unsupported kind pending/);
});

test('every declared kind is accepted, including the zero-instance one', () => {
  for (const kind of noteKinds) {
    assert.doesNotThrow(
      () => validate(baseRoadmap(), [baseNote({ kind })]),
      `kind ${kind} should be legal`,
    );
  }
  // `rollback` is declared with no instances in the live journal: precedent that an
  // unconsumed kind is safe, because kind is branched on nowhere.
  assert.ok(noteKinds.has('rollback'));
});

test('a milestone id absent from the roadmap is rejected', () => {
  expectFailure([baseNote({ milestones: ['M99'] })], /unknown milestone M99/);
});

test('required string fields cannot be blank', () => {
  for (const field of ['id', 'author', 'repository', 'summary', 'impact']) {
    expectFailure([baseNote({ [field]: '' })], new RegExp(field));
  }
});

test('verification and evidence must be string arrays', () => {
  expectFailure([baseNote({ verification: 'ran it' })], /verification/);
  expectFailure([baseNote({ evidence: [123] })], /evidence/);
});

test('the declarative schema agrees with the enforced kind vocabulary', () => {
  // The schema is documentation unless it tracks roadmap.mjs. This test is the link:
  // adding a kind in one place and not the other fails here.
  const schema = JSON.parse(fs.readFileSync(schemaPath, 'utf8'));
  assert.deepEqual(
    [...schema.properties.kind.enum].sort(),
    [...noteKinds].sort(),
  );
  assert.deepEqual(
    [...schema.required].sort(),
    Object.keys(baseNote()).sort(),
  );
});

test('the live journal satisfies its own schema', () => {
  const schema = JSON.parse(fs.readFileSync(schemaPath, 'utf8'));
  const required = new Set(schema.required);
  const allowed = new Set(Object.keys(schema.properties));
  const lines = fs
    .readFileSync(path.join(repoRoot, 'docs/roadmap/commit-notes.jsonl'), 'utf8')
    .split('\n')
    .filter((line) => line.trim());

  assert.ok(lines.length > 0, 'journal must not be empty');
  lines.forEach((line, index) => {
    const note = JSON.parse(line);
    for (const field of required) {
      assert.ok(field in note, `line ${index + 1} missing required field ${field}`);
    }
    for (const field of Object.keys(note)) {
      assert.ok(allowed.has(field), `line ${index + 1} has undeclared field ${field}`);
    }
    assert.ok(schema.properties.kind.enum.includes(note.kind),
      `line ${index + 1} has undeclared kind ${note.kind}`);
  });
});
