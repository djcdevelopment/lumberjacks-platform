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
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { validate, noteKinds, notesRelative, outputRelative } from './roadmap.mjs';

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

// --- the staged gate, in the environments git actually runs it in --------------------
//
// checkStaged() bridges two path vocabularies: the *Relative constants are relative to
// this package, git reports and addresses paths from the top of the repository. The
// bridge is `git rev-parse --show-prefix`, and it is only as good as the environment git
// runs in -- so the one environment it MUST work in went untested, and it was the one
// that broke it. A pre-commit hook inherits GIT_DIR, and from a linked worktree that
// points at <repo>/.git/worktrees/<name>, outside the work tree; git stops discovering,
// treats the current directory as the top of the tree, and reports a prefix of ''. The
// gate then demanded a staged 'docs/roadmap/commit-notes.jsonl' that git had just
// reported as 'Lumberjacks/docs/roadmap/commit-notes.jsonl' and rejected a correct
// commit (observed 2026-07-31, worked around with --no-verify in db3248c). The tests
// below run the real check over a real index in each shape git can hand it.

const gitIdentity = [
  '-c', 'user.name=roadmap-test',
  '-c', 'user.email=roadmap-test@example.invalid',
  '-c', 'commit.gpgsign=false',
];

/// A child environment scrubbed of the pointers these tests exist to control. Scrubbing
/// is the DEFAULT below and not an option a caller can forget: `npm run roadmap:test` is
/// itself invoked from the pre-commit hook, so an unscrubbed `git init` in the fixture
/// would inherit GIT_DIR and land on the real repository.
function childEnv(overrides = {}) {
  const env = { ...process.env };
  delete env.GIT_DIR;
  delete env.GIT_WORK_TREE;
  delete env.GIT_INDEX_FILE;
  return { ...env, ...overrides };
}

function run(command, args, options = {}) {
  return execFileSync(command, args, {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: childEnv(),
    ...options,
  });
}

/// Stands up a throwaway repository holding a real copy of this package plus a real
/// staged journal append, then hands the package directory to `body`. `prefix` places the
/// package under a subdirectory (the baseline monorepo) or at the repo root (a standalone
/// checkout); `worktree` prepares the commit from a linked worktree rather than the main
/// checkout. Nothing is faked — the assertions are about how git behaves, so a hand-built
/// index or a stubbed rev-parse would test the fixture instead of the gate.
function withStagedFixture({ prefix, worktree }, body) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'roadmap-staged-'));
  try {
    const main = path.join(root, 'main');
    fs.mkdirSync(main, { recursive: true });
    run('git', ['init', '-q', '-b', 'main', '.'], { cwd: main });
    // checkStaged() compares the staged blob against the working file byte for byte, so
    // an autocrlf round-trip would fail it on Windows for reasons that are not the point.
    run('git', ['config', 'core.autocrlf', 'false'], { cwd: main });

    for (const relative of [
      'scripts/roadmap.mjs',
      'docs/roadmap/valheim-volunteer-roadmap.json',
      notesRelative,
      outputRelative,
    ]) {
      const target = path.join(main, prefix, relative);
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.copyFileSync(path.join(repoRoot, relative), target);
    }
    run('git', ['add', '-A'], { cwd: main });
    run('git', [...gitIdentity, 'commit', '-qm', 'seed'], { cwd: main });

    let checkout = main;
    if (worktree) {
      checkout = path.join(root, 'wt');
      run('git', ['worktree', 'add', '-q', checkout, '-b', 'wt'], { cwd: main });
    }
    const pkg = path.join(checkout, prefix);

    run(process.execPath, [
      'scripts/roadmap.mjs', 'note',
      '--milestone', knownMilestone,
      '--kind', 'implementation',
      '--summary', 'Exercise the staged gate',
      '--impact', 'The staged gate runs against a real index',
      '--verification', 'node --test scripts/roadmap.test.mjs',
      '--evidence', notesRelative,
    ], { cwd: pkg });
    run('git', ['add', '-A'], { cwd: pkg });

    body(pkg);
  } finally {
    // A leaked temp directory must never mask the assertion that just failed.
    try {
      fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    } catch { /* ignore */ }
  }
}

function stagedCheck(pkg, env) {
  try {
    run(process.execPath, ['scripts/roadmap.mjs', 'check', '--staged'], { cwd: pkg, env });
    return { ok: true, output: '' };
  } catch (error) {
    return { ok: false, output: `${error.stdout ?? ''}${error.stderr ?? ''}`.trim() };
  }
}

test('the staged gate passes from a linked worktree, where git hands the hook a GIT_DIR outside the work tree', () => {
  withStagedFixture({ prefix: 'Lumberjacks/', worktree: true }, (pkg) => {
    const gitDir = run('git', ['rev-parse', '--absolute-git-dir'], { cwd: pkg }).trim();
    assert.match(gitDir, /worktrees/, 'fixture must really be a linked worktree');
    // Exactly what `git commit` exports for a hook in a worktree: an absolute GIT_DIR
    // under .git/worktrees/, and an absolute GIT_INDEX_FILE beside it.
    const result = stagedCheck(pkg, childEnv({
      GIT_DIR: gitDir,
      GIT_INDEX_FILE: path.join(gitDir, 'index'),
    }));
    assert.ok(result.ok, result.output);
  });
});

test('the staged gate still passes from the main checkout, which git invokes with no GIT_DIR and a relative index', () => {
  withStagedFixture({ prefix: 'Lumberjacks/', worktree: false }, (pkg) => {
    // The shape that always worked, pinned so the worktree fix cannot cost it. The
    // relative GIT_INDEX_FILE is git's own doing and resolves against the top of the
    // work tree, not our cwd — which is why dropping GIT_DIR is safe but dropping this
    // would not be.
    const result = stagedCheck(pkg, childEnv({ GIT_INDEX_FILE: '.git/index' }));
    assert.ok(result.ok, result.output);
  });
});

test('the staged gate holds for a standalone checkout under a hook, where the package IS the repo root', () => {
  // The empty-prefix case the comment above gitPathPrefix() promises. Worth its own test
  // because the fix works by restoring discovery, and discovery must still land on ''.
  withStagedFixture({ prefix: '', worktree: true }, (pkg) => {
    const gitDir = run('git', ['rev-parse', '--absolute-git-dir'], { cwd: pkg }).trim();
    const result = stagedCheck(pkg, childEnv({
      GIT_DIR: gitDir,
      GIT_INDEX_FILE: path.join(gitDir, 'index'),
    }));
    assert.ok(result.ok, result.output);
  });
});
