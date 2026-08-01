// Guard tests for the workbench generator (node --test scripts/workbench.test.mjs).
//
// The negative tests here are the point: each one mutates a valid fixture, proves the guard
// fails for the intended reason (asserting the specific message, not just non-zero), then
// restores the fixture and proves the output is deterministic again. A guard that cannot be
// shown to fail is decoration.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

import { baseWorkbench, makeFixtureRepo } from './workbench.testutil.mjs';
import { validate, render, resolveTaskCompletion, computeTaskCounts } from './workbench.mjs';

/// A second task, explicitly blocked — the shape MC-1 would take if its interim forum
/// destination did not exist either.
function blockedTask() {
  return {
    id: 'ST-2',
    title: 'Wire the sample into the relay',
    size: 'small',
    done_when: 'The relay run is posted.',
    completion: {
      destination_kind: 'tool-thread',
      href: null,
      state: 'blocked',
      blocked_reason: 'The relay endpoint has not been stood up yet.',
    },
  };
}

// ---------------------------------------------------------------------------
// Provenance stamps (black-box, fixture repos)
// ---------------------------------------------------------------------------

test('clean inputs render a deterministic production stamp naming the input commit', (t) => {
  const fixture = makeFixtureRepo();
  t.after(fixture.dispose);

  const rendered = fixture.run('render');
  assert.equal(rendered.status, 0, rendered.stderr);
  const html = fixture.readHtml();
  assert.match(html, new RegExp(`Published from ${fixture.inputSha7()} · \\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2} UTC`));
  assert.doesNotMatch(html, /Preview rendered /);

  const checked = fixture.run('check');
  assert.equal(checked.status, 0, checked.stderr);

  // Deterministic: a second render of the same clean tree is byte-identical.
  const again = fixture.run('render');
  assert.equal(again.status, 0, again.stderr);
  assert.equal(fixture.readHtml(), html);
});

test('uncommitted inputs render a preview stamp, and check tolerates the moving clock', (t) => {
  const fixture = makeFixtureRepo();
  t.after(fixture.dispose);

  fs.appendFileSync(fixture.jsonPath, '\n');
  const rendered = fixture.run('render');
  assert.equal(rendered.status, 0, rendered.stderr);
  assert.match(fixture.readHtml(), /Preview rendered \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC with uncommitted changes/);
  assert.doesNotMatch(fixture.readHtml(), /Published from /);

  const checked = fixture.run('check');
  assert.equal(checked.status, 0, checked.stderr);
});

// Negative test 4 (trust review §8): production publish from a dirty tree must fail.
test('a production-stamped artifact over uncommitted inputs fails check for that stated reason', (t) => {
  const fixture = makeFixtureRepo();
  t.after(fixture.dispose);

  assert.equal(fixture.run('render').status, 0);
  fixture.commitAll('fixture: rendered artifact');
  const committedHtml = fixture.readHtml();

  // A render-neutral input change: the parsed catalog is identical, only the bytes moved —
  // isolating the stamp guard from the ordinary stale-content guard.
  fs.appendFileSync(fixture.jsonPath, '\n');
  const checked = fixture.run('check');
  assert.equal(checked.status, 1);
  assert.match(checked.stderr, /carries a published provenance stamp but the provenance inputs have uncommitted changes/);

  // Restore and prove determinism.
  fixture.writeWorkbench(baseWorkbench());
  assert.equal(fixture.run('check').status, 0);
  assert.equal(fixture.run('render').status, 0);
  assert.equal(fixture.readHtml(), committedHtml);
});

// Negative test 5 (trust review §8): published HTML lacking a source commit must fail.
// This is a byte-level reproduction of the defect this suite exists for: the live page once
// said "from an uncommitted working tree" while its source sat committed and clean.
test('a preview-stamped artifact in a clean tree fails check for that stated reason', (t) => {
  const fixture = makeFixtureRepo();
  t.after(fixture.dispose);

  fs.appendFileSync(fixture.jsonPath, '\n');
  assert.equal(fixture.run('render').status, 0);
  assert.match(fixture.readHtml(), /Preview rendered /);
  fixture.commitAll('fixture: committed a preview artifact');

  const checked = fixture.run('check');
  assert.equal(checked.status, 1);
  assert.match(checked.stderr, /carries a preview stamp but the provenance inputs are clean/);

  // The prescribed fix — re-render from the clean tree — heals it deterministically.
  assert.equal(fixture.run('render').status, 0);
  assert.match(fixture.readHtml(), new RegExp(`Published from ${fixture.inputSha7()}`));
  assert.equal(fixture.run('check').status, 0);
  const first = fixture.readHtml();
  assert.equal(fixture.run('render').status, 0);
  assert.equal(fixture.readHtml(), first);
});

// The environment the generator MUST work in went untested, and it was the one that broke it.
// A pre-commit hook inherits GIT_DIR, and from a linked worktree that points at
// <repo>/.git/worktrees/<name>, outside the work tree; git stops discovering and treats the
// current directory as the top of the tree, so `status --porcelain` reported the committed,
// clean provenance inputs as untracked. Mode flipped to preview and check() rejected a
// correctly-published page — "carries a published provenance stamp but the provenance inputs
// have uncommitted changes" — for a commit that only touched docs/workbench/ (observed
// 2026-08-01, worked around with --no-verify in cab0486).
test('provenance stays production from a linked worktree, where git hands the hook a GIT_DIR outside the work tree', (t) => {
  // The package must sit at a prefix, as it does in the baseline monorepo: that is what makes
  // "treat the current directory as the top of the work tree" wrong rather than merely
  // redundant, and it is the difference between reproducing this bug and missing it.
  const fixture = makeFixtureRepo({ prefix: 'Lumberjacks/' });
  t.after(fixture.dispose);

  assert.equal(fixture.run('render').status, 0);
  fixture.commitAll('fixture: rendered artifact');
  const sha7 = fixture.inputSha7();

  const linked = fixture.linkedWorktree();
  assert.match(linked.gitDir, /worktrees/, 'fixture must really be a linked worktree');

  // Rendering under the hook environment must still name the input commit, not fall back to
  // a preview stamp — that stamp is the visible half of the same failure.
  const rendered = linked.run('render', linked.hookEnv());
  assert.equal(rendered.status, 0, rendered.stderr);
  assert.match(linked.readHtml(), new RegExp(`Published from ${sha7} · `));
  assert.doesNotMatch(linked.readHtml(), /Preview rendered /);

  const checked = linked.run('check', linked.hookEnv());
  assert.equal(checked.status, 0, checked.stderr);
});

test('check refuses to vouch for provenance without a git checkout', (t) => {
  const fixture = makeFixtureRepo({ git: false });
  t.after(fixture.dispose);

  const rendered = fixture.run('render');
  assert.equal(rendered.status, 0, rendered.stderr);
  assert.match(fixture.readHtml(), /Content freshness unknown — not rendered from a git checkout/);

  const checked = fixture.run('check');
  assert.equal(checked.status, 1);
  assert.match(checked.stderr, /check requires a git checkout/);
});

// ---------------------------------------------------------------------------
// Task completion and actionability (trust review §1–§2)
// ---------------------------------------------------------------------------

// Negative test 1 (trust review §8): a task that completes "in the thread" while
// discussion.href is null and no completion is declared — MC-1's original shape.
test('a thread-completing task with no thread and no completion fails render, naming the three options', (t) => {
  const workbench = baseWorkbench();
  workbench.tools[0].discussion.href = null;
  const fixture = makeFixtureRepo({ workbench });
  t.after(fixture.dispose);

  const rendered = fixture.run('render');
  assert.equal(rendered.status, 1);
  assert.match(rendered.stderr, /the task completes in the tool's thread but discussion\.href is null/);
  assert.match(rendered.stderr, /wire discussion\.href/);
  assert.match(rendered.stderr, /main-forum/);
  assert.match(rendered.stderr, /"state":"blocked"/);

  // Restore and prove determinism.
  fixture.writeWorkbench(baseWorkbench());
  assert.equal(fixture.run('render').status, 0);
  const first = fixture.readHtml();
  assert.equal(fixture.run('render').status, 0);
  assert.equal(fixture.readHtml(), first);
});

// Negative test 2 (trust review §8): a blocked task must never inflate the actionable count,
// and the rendered count must be load-bearing.
test('a blocked task is excluded from the hero count, rendered visibly, and the count cannot be hand-edited', (t) => {
  const workbench = baseWorkbench();
  workbench.tools[0].first_tasks.push(blockedTask());
  assert.deepEqual(computeTaskCounts(workbench), { present: 2, actionable: 1, blocked: 1 });

  const fixture = makeFixtureRepo({ workbench });
  t.after(fixture.dispose);
  assert.equal(fixture.run('render').status, 0);
  const html = fixture.readHtml();
  assert.match(html, /<a href="#tools">1 first tasks open · 1 blocked<\/a>/);
  assert.match(html, /Pick one to start · 1 open · 1 blocked/);
  assert.match(html, /<span class="task-blocked">blocked<\/span>/);
  assert.match(html, /<em>Blocked:<\/em> The relay endpoint has not been stood up yet\./);
  assert.match(html, /1 open · 1 blocked<\/span>/); // index row tally

  // Tampering with the rendered hero count fails check — the computed count is load-bearing.
  fs.writeFileSync(fixture.htmlPath, html.replace('1 first tasks open · 1 blocked', '2 first tasks open'), 'utf8');
  const checked = fixture.run('check');
  assert.equal(checked.status, 1);
  assert.match(checked.stderr, /is stale/);

  assert.equal(fixture.run('render').status, 0);
  assert.equal(fixture.readHtml(), html);
});

// Negative test 3 (trust review §8): hardcoded counts disagreeing with the data.
test('a headline tool count that disagrees with the catalog fails render', (t) => {
  const workbench = baseWorkbench();
  workbench.headline = 'Eight tools. A fixture catalog exercised by the generator’s own tests.';
  const fixture = makeFixtureRepo({ workbench });
  t.after(fixture.dispose);

  const rendered = fixture.run('render');
  assert.equal(rendered.status, 1);
  assert.match(rendered.stderr, /headline says Eight tools but the catalog holds 1/);
});

test('hardcoded task-count prose anywhere in the catalog fails render', (t) => {
  const workbench = baseWorkbench();
  workbench.invitation_line = 'There are 11 first tasks open right now.';
  const fixture = makeFixtureRepo({ workbench });
  t.after(fixture.dispose);

  const rendered = fixture.run('render');
  assert.equal(rendered.status, 1);
  assert.match(rendered.stderr, /hardcoded task-count prose is forbidden/);
});

test('completion resolution derivation matrix', () => {
  const workbench = baseWorkbench();
  const tool = workbench.tools[0];
  const task = tool.first_tasks[0];

  // Default: derived tool-thread, actionable, href from the discussion.
  assert.deepEqual(resolveTaskCompletion(workbench, tool, task, 't'), {
    destination_kind: 'tool-thread',
    state: 'actionable',
    href: tool.discussion.href,
    blocked_reason: null,
  });

  // Explicit main-forum interim (MC-1's shape) resolves to the forum href.
  task.completion = { destination_kind: 'main-forum', href: null, state: 'actionable' };
  assert.equal(resolveTaskCompletion(workbench, tool, task, 't').href, workbench.feedback.forum_href);

  // An authored href is a second copy of a derived fact.
  task.completion = { destination_kind: 'main-forum', href: 'https://discord.com/channels/1/2', state: 'actionable' };
  assert.throws(() => resolveTaskCompletion(workbench, tool, task, 't'), /must be null/);

  // Actionable requires the destination to exist.
  task.completion = { destination_kind: 'main-forum', href: null, state: 'actionable' };
  workbench.feedback.forum_href = null;
  assert.throws(() => resolveTaskCompletion(workbench, tool, task, 't'), /cannot be actionable while its destination is absent/);
  workbench.feedback.forum_href = baseWorkbench().feedback.forum_href;

  // Blocked requires a reason, and a blocked task cannot be the suggested first pick.
  task.completion = { destination_kind: 'tool-thread', href: null, state: 'blocked' };
  assert.throws(() => resolveTaskCompletion(workbench, tool, task, 't'), /blocked_reason/);
  task.completion.blocked_reason = 'Waiting on the relay.';
  assert.equal(resolveTaskCompletion(workbench, tool, task, 't').href, null);
  task.suggested = true;
  assert.throws(() => resolveTaskCompletion(workbench, tool, task, 't'), /may not be the suggested first pick/);
  delete task.suggested;

  // A reason on an actionable task is stale data waiting to mislead.
  task.completion = { destination_kind: 'tool-thread', href: null, state: 'actionable', blocked_reason: 'stale' };
  assert.throws(() => resolveTaskCompletion(workbench, tool, task, 't'), /unless the state is blocked/);
});

test('zero blocked tasks keep the original hero wording', (t) => {
  const fixture = makeFixtureRepo();
  t.after(fixture.dispose);
  assert.equal(fixture.run('render').status, 0);
  const html = fixture.readHtml();
  assert.match(html, /<a href="#tools">1 first tasks open<\/a>/);
  // Visible text only — the stylesheet always carries the .task-blocked rules.
  const text = html.replace(/<style>[\s\S]*?<\/style>/, ' ').replace(/<[^>]*>/g, ' ');
  assert.doesNotMatch(text, /\bblocked\b/i);
});

// ---------------------------------------------------------------------------
// Copy derived from structured policy (trust review §4–§5)
// ---------------------------------------------------------------------------

// Negative test 6 (trust review §8): source prose promising commit access while the
// structured field says false.
test('a source note promising commit access while code_contributions is false fails validation', () => {
  const workbench = baseWorkbench();
  workbench.tools[0].contribution.code_contributions = false;
  workbench.tools[0].contribution.stage_3_reward = 'Triage rights on the fixture thread.';
  workbench.tools[0].source.note = 'Stage 3 gets you commit access here.';
  assert.throws(
    () => validate(workbench, JSON.stringify(workbench)),
    /source\.note promises commit access while code_contributions is false/,
  );
});

test('policy vocabulary in a source note fails validation even without a contradiction', () => {
  const workbench = baseWorkbench();
  workbench.tools[0].source.note = 'Nothing here is gated — read it all.';
  assert.throws(
    () => validate(workbench, JSON.stringify(workbench)),
    /restates access policy/,
  );
});

test('naming a person in the catalog fails validation', () => {
  const workbench = baseWorkbench();
  workbench.ladder[4].what_you_did = 'Sustained the contribution over time, and Derek agrees you are the person holding it.';
  assert.throws(
    () => validate(workbench, JSON.stringify(workbench)),
    /must not name a person/,
  );
});

test('the access-policy line is derived from code_contributions', () => {
  const granting = baseWorkbench();
  assert.ok(render(granting).includes('Ladder stage 3 opens commit access for this tool.'));

  const withheld = baseWorkbench();
  withheld.tools[0].contribution.code_contributions = false;
  withheld.tools[0].contribution.stage_3_reward = 'Triage rights on the fixture thread.';
  assert.ok(render(withheld).includes('Ladder stage 3 does not open commit access here'));
});

test('LICENSING.md renders as a link where named, and an unlinked mention fails check', (t) => {
  const workbench = baseWorkbench();
  workbench.tools[0].license = 'Public source (BSL 1.1) — see LICENSING.md.';
  const fixture = makeFixtureRepo({ workbench });
  t.after(fixture.dispose);

  assert.equal(fixture.run('render').status, 0);
  assert.match(fixture.readHtml(), /<a [^>]*>LICENSING\.md<\/a>/);
  assert.equal(fixture.run('check').status, 0);

  // A mention outside the linkified fields is a named document a reader cannot open.
  workbench.tools[0].what_it_does = 'Exists so LICENSING.md coverage can be proven.';
  fixture.writeWorkbench(workbench);
  assert.equal(fixture.run('render').status, 0);
  const checked = fixture.run('check');
  assert.equal(checked.status, 1);
  assert.match(checked.stderr, /LICENSING\.md is named \d+ time\(s\) but linked only/);
});

// ---------------------------------------------------------------------------
// Pins on pre-existing guards (unit, in-process) — so refactoring cannot drop them.
// ---------------------------------------------------------------------------

test('the base fixture validates and renders its required sections', () => {
  const workbench = baseWorkbench();
  validate(workbench, JSON.stringify(workbench));
  const html = render(workbench);
  for (const marker of ['id="tools"', 'id="ladder"', '01 · THE TOOLS', 'Sample Tool']) {
    assert.ok(html.includes(marker), `rendered page is missing ${marker}`);
  }
});

test('pin: stage_3_reward may not promise commit access while code_contributions is false', () => {
  const workbench = baseWorkbench();
  workbench.tools[0].contribution.code_contributions = false;
  assert.throws(
    () => validate(workbench, JSON.stringify(workbench)),
    /promises commit access while code_contributions is false/,
  );
});

test('pin: a member-only Discord link may not render without an invite', () => {
  const workbench = baseWorkbench();
  workbench.feedback.invite_href = null;
  assert.throws(
    () => validate(workbench, JSON.stringify(workbench)),
    /member-only Discord link/,
  );
});
