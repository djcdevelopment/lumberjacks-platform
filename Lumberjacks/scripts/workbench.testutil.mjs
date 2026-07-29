// Shared helpers for the workbench generator's test suite (node --test).
//
// Two mechanisms, matching how the guards are exercised:
//   - baseWorkbench(): a minimal catalog that passes every validator, for in-process unit
//     tests against the exported functions.
//   - makeFixtureRepo(): a throwaway git repo holding a byte-copy of the real generator plus
//     the base catalog, for black-box CLI runs. workbench.mjs derives its repo root from its
//     own location, so a copied script inside a fixture tree exercises the real provenance
//     plumbing against the fixture's git history — no test-only code paths in the generator.
//
// Not matched by the npm test glob on purpose: this file defines no tests.

import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const realScriptPath = path.join(path.dirname(fileURLToPath(import.meta.url)), 'workbench.mjs');

/// A fresh minimal catalog per call (mutating a shared object across tests would couple them).
/// Every field exists to satisfy a specific validator; trim nothing without checking validate().
export function baseWorkbench() {
  return {
    schema_version: 1,
    title: 'Comfy × Valheim — Fixture Workbench',
    headline: 'One tool. A fixture catalog exercised by the generator’s own tests.',
    owners_href: 'https://github.com/djcdevelopment/baseline/blob/main/Lumberjacks/docs/workbench/OWNERS.md',
    honesty_statement: 'Every status here is a tested claim.',
    not_a_verdict_summary: 'This fixture is not a verdict on anything.',
    not_a_verdict: 'A fixture catalog exercised by tests; nothing described here ships anywhere.',
    invitation_line: 'Reading this fixture closely is itself a contribution.',
    feedback: {
      rhythm: 'Replies land when the operator checks in.',
      forum_label: '#workbench forum',
      forum_href: 'https://discord.com/channels/1531911987074957442/1531926985314668635',
      start_here_label: 'How this works — read me first',
      start_here_href: null,
      invite_label: 'Join the Discord',
      invite_href: 'https://discord.gg/TESTINVITE',
    },
    ladder: [
      {
        stage: 0,
        name: 'Curious',
        what_you_did: 'Read the page.',
        what_you_get: 'A true picture of what runs.',
        recorded_in: 'Nowhere — looking is free.',
      },
      {
        stage: 1,
        name: 'Voice',
        what_you_did: 'Posted one observation.',
        what_you_get: 'A reply from a person.',
        recorded_in: 'The thread you posted in.',
      },
      {
        stage: 2,
        name: 'Hands',
        what_you_did: 'Completed a first task.',
        what_you_get: 'Your name on the result.',
        recorded_in: 'OWNERS.md candidate notes.',
      },
      {
        stage: 3,
        name: 'Contributor',
        what_you_did: 'Kept a tool honest over several changes.',
        what_you_get: 'The right named on the tool’s own card.',
        recorded_in: 'OWNERS.md.',
      },
      {
        stage: 4,
        name: 'Owner',
        what_you_did: 'Sustained the contribution over time, and the project operator agrees you are the person holding it.',
        what_you_get: 'You set the tool’s direction and you can say no.',
        recorded_in: 'OWNERS.md, plus the tool’s ownership record.',
      },
    ],
    tools: [
      {
        id: 'sample-tool',
        name: 'Sample Tool',
        one_liner: 'Does exactly one thing, inside this test fixture.',
        status: 'live',
        status_detail: 'Runs in the test fixture and nowhere else; that is the whole claim.',
        what_it_does: 'Exists so the generator’s honesty guards can be exercised in isolation.',
        who_its_for: 'The test suite.',
        time_to_first_result: 'instant',
        requires: ['node 20+'],
        access: {
          kind: 'public-repo',
          href: 'https://github.com/djcdevelopment/baseline',
          sha256: null,
          size_bytes: null,
          published_at: null,
        },
        source: {
          kind: 'public-repo',
          href: 'https://github.com/djcdevelopment/baseline',
          note: 'Lives entirely inside the test fixture tree.',
        },
        docs: [
          { label: 'How to run it', href: null },
        ],
        discussion: {
          label: 'Sample tool thread',
          href: 'https://discord.com/channels/1531911987074957442/1531977720790257866',
        },
        first_tasks: [
          {
            id: 'ST-1',
            title: 'Run it once and say what happened',
            size: 'small',
            done_when: 'Your result is in the thread, along with anything that surprised you.',
          },
        ],
        ownership: {
          state: 'unclaimed',
          claimed_by: null,
          claimed_at: null,
          record: null,
        },
        roadmap_milestones: ['A7'],
        privacy_note: 'Collects nothing.',
        license: 'Public source (BSL 1.1) — see the repository.',
        contribution: {
          stage_3_reward: 'Commit access to the fixture tool’s own directory.',
          code_contributions: true,
        },
      },
    ],
  };
}

function git(root, args) {
  return execFileSync('git', args, {
    cwd: root,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
}

/// Build a throwaway repo: real generator bytes, fixture catalog, one commit (unless
/// { git: false }). Callers own cleanup via the returned dispose(); tests register it with
/// t.after so a failing assertion still removes the tree.
export function makeFixtureRepo({ workbench = baseWorkbench(), git: withGit = true } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'workbench-fixture-'));
  fs.mkdirSync(path.join(root, 'scripts'), { recursive: true });
  fs.mkdirSync(path.join(root, 'docs', 'workbench'), { recursive: true });
  fs.copyFileSync(realScriptPath, path.join(root, 'scripts', 'workbench.mjs'));

  const fixture = {
    root,
    scriptPath: path.join(root, 'scripts', 'workbench.mjs'),
    jsonPath: path.join(root, 'docs', 'workbench', 'workbench.json'),
    htmlPath: path.join(root, 'src', 'Game.Gateway', 'Community', 'workbench.html'),

    writeWorkbench(value) {
      fs.writeFileSync(fixture.jsonPath, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
    },
    readHtml() {
      return fs.readFileSync(fixture.htmlPath, 'utf8');
    },
    run(command) {
      const result = spawnSync(process.execPath, [fixture.scriptPath, command], {
        cwd: root,
        encoding: 'utf8',
      });
      return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
    },
    commitAll(message) {
      git(root, ['add', '-A']);
      git(root, ['commit', '--quiet', '-m', message]);
    },
    /// The sha7 the production stamp must name: last commit touching the provenance inputs.
    inputSha7() {
      return git(root, ['log', '-1', '--format=%H', '--', 'docs/workbench/workbench.json', 'scripts/workbench.mjs']).slice(0, 7);
    },
    dispose() {
      fs.rmSync(root, { recursive: true, force: true });
    },
  };

  fixture.writeWorkbench(workbench);

  if (withGit) {
    git(root, ['init', '--quiet']);
    git(root, ['config', 'core.autocrlf', 'false']);
    git(root, ['config', 'user.name', 'fixture']);
    git(root, ['config', 'user.email', 'fixture@test.invalid']);
    git(root, ['config', 'commit.gpgsign', 'false']);
    fixture.commitAll('fixture: initial state');
  }

  return fixture;
}
