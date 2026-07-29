// Guard tests for the live destination verifier (node --test).
//
// No network: every test drives runChecks() with a canned fetchImpl and proves the guard fails
// for its intended reason. The green-path fixture answers every URL correctly; each negative
// test overrides exactly one response and asserts the specific classed failure.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { baseWorkbench } from './workbench.testutil.mjs';
import { runChecks, sha256Hex } from './workbench-verify-live.mjs';

const BASE = 'https://example.test';
const GUILD = '1531911987074957442';
const FORUM_ID = '1531926985314668635';
const THREAD_ID = '1531977720790257866';
const NOW = Date.parse('2026-07-29T12:00:00Z');

const KIT_BYTES = Buffer.from('fixture-zip-bytes-not-a-real-archive');
const KIT_SHA = sha256Hex(KIT_BYTES);
const LOCAL_HTML = Buffer.from('<!DOCTYPE html><html>fixture workbench render</html>');

function verifyWorkbench() {
  const workbench = baseWorkbench();
  // A second, site-download tool so the downloads class has something to verify.
  workbench.tools.push({
    id: 'sample-kit',
    name: 'Sample Kit',
    status: 'live',
    access: { kind: 'site-download', href: '/workbench/downloads/sample-kit', sha256: KIT_SHA, size_bytes: KIT_BYTES.length, published_at: '2026-07-29T00:00:00Z' },
    source: { kind: 'public-repo', href: 'https://github.com/djcdevelopment/baseline', note: 'fixture' },
    discussion: { label: 'Sample kit thread', href: `https://discord.com/channels/${GUILD}/${THREAD_ID}` },
    first_tasks: [],
  });
  return workbench;
}

function provisionFixture() {
  return {
    guild_id: GUILD,
    channel_id: FORUM_ID,
    invite_href: 'https://discord.gg/TESTINVITE',
    invite_expires_at: '2099-01-01T00:00:00+00:00',
    posts: {},
  };
}

function response({ status = 200, url = '', jsonBody = null, bytes = null, headers = {} }) {
  const map = new Map(Object.entries(headers).map(([key, value]) => [key.toLowerCase(), value]));
  return {
    ok: status >= 200 && status < 300,
    status,
    url,
    headers: { get: (key) => map.get(key.toLowerCase()) ?? null },
    json: async () => jsonBody,
    arrayBuffer: async () => Uint8Array.from(bytes ?? []).buffer,
  };
}

/// The green-path world: every destination the fixture catalog names answers correctly.
/// Overrides are keyed by exact URL and win outright.
function makeFetch(overrides = {}) {
  return async (url) => {
    if (overrides[url]) return overrides[url](url);
    if (url.startsWith('https://discord.com/api/v10/invites/')) {
      return response({ url, jsonBody: { guild: { id: GUILD }, expires_at: '2099-01-01T00:00:00+00:00' } });
    }
    const channel = url.match(/^https:\/\/discord\.com\/api\/v10\/channels\/(\d+)$/);
    if (channel) {
      return channel[1] === FORUM_ID
        ? response({ url, jsonBody: { type: 15, guild_id: GUILD } })
        : response({ url, jsonBody: { type: 11, guild_id: GUILD, parent_id: FORUM_ID, thread_metadata: { archived: false } } });
    }
    if (url.startsWith('https://api.github.com/repos/')) {
      return response({ url, jsonBody: { private: false } });
    }
    if (url.startsWith('https://github.com/')) {
      return response({ url });
    }
    if (url === `${BASE}/workbench/downloads/sample-kit`) {
      return response({ url, bytes: KIT_BYTES, headers: { 'X-Download-Sha256': KIT_SHA } });
    }
    if (url === `${BASE}/workbench`) {
      return response({ url, headers: { 'X-Workbench-Sha256': sha256Hex(LOCAL_HTML) } });
    }
    if (url.startsWith(BASE)) {
      return response({ url });
    }
    throw new Error(`unexpected URL in fixture: ${url}`);
  };
}

function run(overridesOrOptions = {}, options = {}) {
  const overrides = overridesOrOptions;
  return runChecks({
    mode: 'post-publish',
    baseUrl: BASE,
    fetchImpl: makeFetch(overrides),
    botToken: 'fixture-token',
    now: NOW,
    workbench: verifyWorkbench(),
    provisionState: provisionFixture(),
    localHtml: LOCAL_HTML,
    ...options,
  });
}

test('the green-path world passes every class', async () => {
  const receipt = await run();
  assert.equal(receipt.verdict, 'pass', JSON.stringify(receipt.failed_checks, null, 2));
  assert.equal(receipt.warnings.length, 0);
  for (const cls of ['discord', 'github', 'routes', 'downloads', 'served-artifact']) {
    assert.ok(receipt.checks.some((c) => c.class === cls), `no ${cls} checks ran`);
  }
});

test('pre-publish runs no download or served-artifact checks', async () => {
  const receipt = await run({}, { mode: 'pre-publish', localHtml: null });
  assert.equal(receipt.verdict, 'pass', JSON.stringify(receipt.failed_checks, null, 2));
  assert.ok(!receipt.checks.some((c) => c.class === 'downloads' || c.class === 'served-artifact'));
});

// Negative test 7 (trust review §8): a public-repo deep link that 404s.
test('a 404 on a public deep link fails the github class and names the URL', async () => {
  const target = 'https://github.com/djcdevelopment/baseline/blob/main/LICENSING.md';
  const receipt = await run({ [target]: (url) => response({ url, status: 404 }) });
  assert.equal(receipt.verdict, 'fail');
  const failure = receipt.checks.find((c) => !c.ok && c.class === 'github' && c.target === target);
  assert.ok(failure, 'no github failure recorded for the 404 link');
  assert.match(failure.detail, /HTTP 404/);
});

// Negative test 8 (trust review §8): a validly shaped invite that has expired.
test('an expired invite fails the discord class for that stated reason', async () => {
  const receipt = await run({
    'https://discord.com/api/v10/invites/TESTINVITE?with_expiration=true': (url) => response({
      url,
      jsonBody: { guild: { id: GUILD }, expires_at: '2026-07-01T00:00:00+00:00' },
    }),
  });
  assert.equal(receipt.verdict, 'fail');
  const failure = receipt.checks.find((c) => !c.ok && c.class === 'discord' && c.name === 'invite has not expired');
  assert.ok(failure, 'no expiry failure recorded');
  assert.match(failure.detail, /invite expired 2026-07-01/);
});

// Negative test 9 (trust review §8): a discussion URL pointing at the wrong guild.
test('a thread living in the wrong guild fails the discord class naming both guilds', async () => {
  const receipt = await run({
    [`https://discord.com/api/v10/channels/${THREAD_ID}`]: (url) => response({
      url,
      jsonBody: { type: 11, guild_id: '999900001111222233', parent_id: FORUM_ID },
    }),
  });
  assert.equal(receipt.verdict, 'fail');
  const failure = receipt.checks.find((c) => !c.ok && c.class === 'discord' && c.detail?.includes('999900001111222233'));
  assert.ok(failure, 'no wrong-guild failure recorded');
  assert.match(failure.detail, new RegExp(GUILD));
});

// Negative test 10 (trust review §8): a download serving the wrong digest, and the wrong size.
test('a download with the right length but wrong bytes fails the digest check', async () => {
  const wrong = Buffer.from(KIT_BYTES);
  wrong[0] ^= 0xff;
  const receipt = await run({
    [`${BASE}/workbench/downloads/sample-kit`]: (url) => response({
      url, bytes: wrong, headers: { 'X-Download-Sha256': sha256Hex(wrong) },
    }),
  });
  assert.equal(receipt.verdict, 'fail');
  const failure = receipt.checks.find((c) => !c.ok && c.class === 'downloads' && c.name.includes('digest'));
  assert.ok(failure, 'no digest failure recorded');
  assert.match(failure.detail, new RegExp(`the page claims ${KIT_SHA}`));
});

test('a truncated download fails the size check', async () => {
  const short = KIT_BYTES.subarray(0, 10);
  const receipt = await run({
    [`${BASE}/workbench/downloads/sample-kit`]: (url) => response({
      url, bytes: short, headers: { 'X-Download-Sha256': sha256Hex(short) },
    }),
  });
  assert.equal(receipt.verdict, 'fail');
  const failure = receipt.checks.find((c) => !c.ok && c.class === 'downloads' && c.name.includes('size'));
  assert.ok(failure, 'no size failure recorded');
  assert.match(failure.detail, /served 10 bytes, the page claims 36/);
});

test('a missing bot token fails closed, and --allow-unverified-threads downgrades to warnings', async () => {
  const strict = await run({}, { botToken: null });
  assert.equal(strict.verdict, 'fail');
  assert.ok(strict.checks.some((c) => !c.ok && /no bot token/.test(c.detail ?? '')));

  const lenient = await run({}, { botToken: null, allowUnverifiedThreads: true });
  assert.equal(lenient.verdict, 'pass', JSON.stringify(lenient.failed_checks, null, 2));
  assert.ok(lenient.warnings.length > 0);
});

test('an off-origin redirect on an internal route is an auth-gate failure', async () => {
  const receipt = await run({
    [`${BASE}/community`]: () => response({ url: 'https://login.example.test/signin', status: 200 }),
  });
  assert.equal(receipt.verdict, 'fail');
  const failure = receipt.checks.find((c) => !c.ok && c.class === 'routes' && c.target === `${BASE}/community`);
  assert.ok(failure, 'no route failure recorded');
  assert.match(failure.detail, /redirected off-origin/);
});
