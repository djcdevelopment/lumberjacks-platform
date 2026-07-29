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
import { validate, render } from './workbench.mjs';

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
