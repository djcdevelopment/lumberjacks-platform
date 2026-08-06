#!/usr/bin/env node

// Live destination verification for the Community Workbench — the release-path counterpart to
// scripts/workbench.mjs, which stays deliberately offline. Everything the published page asks a
// stranger to click is verified against the live world it points at:
//
//   discord          the invite resolves, targets the expected guild, and has not expired;
//                    every member-only channel/thread URL exists in that guild (bot token)
//   github           every public URL the page carries answers 200, and every repo the catalog
//                    declares public actually is
//   routes           the site's promoted internal routes answer 200 on the public origin
//                    without bouncing to an auth gate
//   downloads        (post-publish) each download streams with the exact digest, size, and
//                    header the page claims
//   served-artifact  (post-publish) the served page hash equals the local committed render
//
// Two phases, because pre-upload the live site still serves the previous page:
//   --pre-publish    discord + github + routes (the publish gate in Publish-WorkbenchAssets.ps1)
//   --post-publish   everything, run after the upload lands
//
// Failures are per-check and classed; the receipt lands at captures/workbench-verify-live.json.
// Transient network trouble therefore never blocks local editing — render/check know nothing of
// this file. It belongs to the release path alone.

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workbenchPath = path.join(repoRoot, 'docs/workbench/workbench.json');
const provisionStatePath = path.join(repoRoot, '../tools/workbench/discord/provision-state.json');
const localHtmlPath = path.join(repoRoot, 'src/Game.Gateway/Community/workbench.html');
const receiptPath = path.join(repoRoot, '../captures/workbench-verify-live.json');

// Token resolution mirrors tools/workbench/discord/workbench_discord.py exactly: the env var,
// then an env-named file, then the two default files under ~/.baseline (a raw token, or a
// KEY=VALUE env file). One resolution rule for both tools, so "the bot works but the verifier
// cannot see the token" is not a reachable state.
const tokenCandidatePaths = [
  path.join(os.homedir(), '.baseline', 'workbench-discord.token'),
  path.join(os.homedir(), '.baseline', 'discord.env'),
];
const TOKEN_KEY_NAMES = ['WORKBENCH_DISCORD_TOKEN', 'DISCORD_BOT_TOKEN', 'DISCORD_TOKEN', 'BOT_TOKEN', 'TOKEN', 'KEY'];

function loadBotToken() {
  if (process.env.WORKBENCH_DISCORD_TOKEN?.trim()) return process.env.WORKBENCH_DISCORD_TOKEN.trim();
  const envFile = process.env.WORKBENCH_DISCORD_TOKEN_FILE;
  for (const candidate of envFile ? [envFile, ...tokenCandidatePaths] : tokenCandidatePaths) {
    if (!fs.existsSync(candidate)) continue;
    const text = fs.readFileSync(candidate, 'utf8').trim();
    if (!text) continue;
    if (!text.includes('=')) return text;
    for (const line of text.split(/\r?\n/)) {
      const match = line.match(/^([A-Z_]+)\s*=\s*(.+)$/);
      if (match && TOKEN_KEY_NAMES.includes(match[1])) return match[2].trim();
    }
  }
  return null;
}

const DEFAULT_BASE_URL = 'https://am4.tail8e749c.ts.net';
const DISCORD_API = 'https://discord.com/api/v10';
const GITHUB_API = 'https://api.github.com';
const REQUEST_TIMEOUT_MS = 15_000;
const INVITE_WARN_DAYS = 14;

// These two lists mirror literal template text in scripts/workbench.mjs (the nav bar and the
// provenance footer). If the generator's template changes, change these with it — check() cannot
// see this file, so the mirror is maintained by hand and verified by the routes/github classes.
const NAV_ROUTES = ['/workbench', '/community', '/roadmap', '/networksense', '/events', '/testing', '/join', '/health'];
const FOOTER_GITHUB_LINKS = [
  'https://github.com/djcdevelopment/baseline',
  'https://github.com/djcdevelopment/Lumberjacks',
  'https://github.com/djcdevelopment/comfy',
  'https://github.com/djcdevelopment/baseline/blob/main/docs/legal/LICENSING.md',
];

function sha256Hex(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

/// Every unique github.com/djcdevelopment URL anywhere in the catalog, plus the generator's
/// footer links. Walking every string means a new href field is covered the day it is added.
function collectGithubHrefs(workbench) {
  const found = new Set(FOOTER_GITHUB_LINKS);
  const walk = (value) => {
    if (typeof value === 'string') {
      if (value.startsWith('https://github.com/djcdevelopment/')) found.add(value);
    } else if (Array.isArray(value)) {
      value.forEach(walk);
    } else if (value && typeof value === 'object') {
      Object.values(value).forEach(walk);
    }
  };
  walk(workbench);
  return [...found].sort();
}

/// Member-only Discord destinations the page renders: the forum, the start-here pin, and each
/// tool's discussion thread.
function collectDiscordChannelUrls(workbench) {
  const urls = [];
  const push = (label, href) => {
    if (typeof href === 'string' && href.startsWith('https://discord.com/channels/')) urls.push({ label, href });
  };
  push('feedback.forum', workbench.feedback.forum_href);
  push('feedback.start_here', workbench.feedback.start_here_href);
  for (const tool of workbench.tools) push(`${tool.id}.discussion`, tool.discussion.href);
  return urls;
}

function repoFromGithubHref(href) {
  const match = href.match(/^https:\/\/github\.com\/djcdevelopment\/([^/]+)/);
  return match ? match[1] : null;
}

function parseChannelUrl(href) {
  const match = href.match(/^https:\/\/discord\.com\/channels\/(\d+)\/(\d+)/);
  return match ? { guildId: match[1], channelId: match[2] } : null;
}

async function fetchWithTimeout(fetchImpl, url, init = {}) {
  return fetchImpl(url, {
    redirect: 'follow',
    ...init,
    headers: { 'User-Agent': 'baseline-workbench-verify', ...(init.headers ?? {}) },
    signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
  });
}

/// Build and run every check for the requested mode. Everything with a side of the world in it
/// is injectable — fetchImpl, botToken, now, and the two data files — so the negative tests can
/// prove each guard fails for its intended reason without touching the network.
async function runChecks(options) {
  const {
    mode,
    baseUrl = DEFAULT_BASE_URL,
    fetchImpl,
    botToken = null,
    allowUnverifiedThreads = false,
    now = Date.now(),
    workbench = JSON.parse(fs.readFileSync(workbenchPath, 'utf8')),
    provisionState = fs.existsSync(provisionStatePath) ? JSON.parse(fs.readFileSync(provisionStatePath, 'utf8')) : null,
    localHtml = mode === 'post-publish' && fs.existsSync(localHtmlPath) ? fs.readFileSync(localHtmlPath) : null,
  } = options;
  if (mode !== 'pre-publish' && mode !== 'post-publish') throw new Error(`unknown mode ${mode}`);
  if (typeof fetchImpl !== 'function') throw new Error('fetchImpl is required');
  if (!provisionState?.guild_id) throw new Error(`provision state with guild_id is required (${provisionStatePath})`);

  const expectedGuild = String(provisionState.guild_id);
  const forumChannelId = provisionState.channel_id ? String(provisionState.channel_id) : null;
  const origin = new URL(baseUrl).origin;
  const results = [];
  const record = (cls, name, target, outcome) => results.push({ class: cls, name, target, ...outcome });

  // ---------------------------------------------------------------- discord: the invite
  const inviteHref = workbench.feedback.invite_href;
  const inviteCode = inviteHref?.match(/^https:\/\/discord\.gg\/([A-Za-z0-9-]+)$/)?.[1];
  if (!inviteCode) {
    record('discord', 'invite resolves', inviteHref ?? '(none)', { ok: false, detail: 'no discord.gg invite in workbench.feedback' });
  } else {
    try {
      const res = await fetchWithTimeout(fetchImpl, `${DISCORD_API}/invites/${inviteCode}?with_expiration=true`);
      if (!res.ok) {
        record('discord', 'invite resolves', inviteHref, { ok: false, detail: `HTTP ${res.status} — the page's only way in for a non-member does not resolve` });
      } else {
        const invite = await res.json();
        record('discord', 'invite resolves', inviteHref, { ok: true });
        const inviteGuild = String(invite.guild?.id ?? invite.guild_id ?? '');
        record('discord', 'invite targets the expected guild', inviteHref,
          inviteGuild === expectedGuild
            ? { ok: true }
            : { ok: false, detail: `invite resolves to guild ${inviteGuild || '(unknown)'}, expected ${expectedGuild}` });
        const expiresAt = invite.expires_at ? Date.parse(invite.expires_at) : null;
        if (expiresAt !== null && expiresAt <= now) {
          record('discord', 'invite has not expired', inviteHref, { ok: false, detail: `invite expired ${invite.expires_at} — regenerate it as never-expiring, then update workbench.json and provision-state.json` });
        } else if (expiresAt !== null && expiresAt - now <= INVITE_WARN_DAYS * 86_400_000) {
          record('discord', 'invite has not expired', inviteHref, { ok: true, warn: `invite expires ${invite.expires_at}` });
        } else {
          record('discord', 'invite has not expired', inviteHref, { ok: true });
        }
        const recorded = provisionState.invite_expires_at ? Date.parse(provisionState.invite_expires_at) : null;
        record('discord', 'invite expiry matches provision-state', inviteHref,
          recorded === expiresAt
            ? { ok: true }
            : { ok: false, detail: `live expiry ${invite.expires_at ?? 'null'} != provision-state ${provisionState.invite_expires_at ?? 'null'} — update provision-state.json` });
      }
    } catch (error) {
      record('discord', 'invite resolves', inviteHref, { ok: false, detail: String(error) });
    }
  }

  // ------------------------------------------------- discord: member-only channels/threads
  const channelUrls = collectDiscordChannelUrls(workbench);
  for (const { label, href } of channelUrls) {
    const parsed = parseChannelUrl(href);
    if (!parsed) {
      record('discord', `${label} URL is well-formed`, href, { ok: false, detail: 'not a /channels/<guild>/<id> URL' });
      continue;
    }
    if (parsed.guildId !== expectedGuild) {
      record('discord', `${label} URL names the expected guild`, href, { ok: false, detail: `URL names guild ${parsed.guildId}, expected ${expectedGuild}` });
      continue;
    }
    if (!botToken) {
      record('discord', `${label} destination is live`, href, allowUnverifiedThreads
        ? { ok: true, warn: 'unverified — no bot token available' }
        : { ok: false, detail: 'no bot token found (env WORKBENCH_DISCORD_TOKEN, or ~/.baseline/workbench-discord.token / discord.env) — the thread cannot be verified (pass --allow-unverified-threads to downgrade to a warning)' });
      continue;
    }
    try {
      const res = await fetchWithTimeout(fetchImpl, `${DISCORD_API}/channels/${parsed.channelId}`, {
        headers: { Authorization: `Bot ${botToken}` },
      });
      if (!res.ok) {
        record('discord', `${label} destination is live`, href, { ok: false, detail: `HTTP ${res.status} — the destination does not resolve for the bot` });
        continue;
      }
      const channel = await res.json();
      const liveGuild = String(channel.guild_id ?? '');
      if (liveGuild !== expectedGuild) {
        record('discord', `${label} destination is live`, href, { ok: false, detail: `channel ${parsed.channelId} lives in guild ${liveGuild || '(unknown)'}, expected ${expectedGuild}` });
        continue;
      }
      // 11/12 are threads; a thread under a different parent is a repurposed URL.
      if ((channel.type === 11 || channel.type === 12) && forumChannelId && String(channel.parent_id ?? '') !== forumChannelId) {
        record('discord', `${label} destination is live`, href, { ok: false, detail: `thread parent is ${channel.parent_id}, expected the #workbench forum ${forumChannelId}` });
        continue;
      }
      record('discord', `${label} destination is live`, href,
        channel.thread_metadata?.archived
          ? { ok: true, warn: 'thread is archived (it reopens on the next reply)' }
          : { ok: true });
    } catch (error) {
      record('discord', `${label} destination is live`, href, { ok: false, detail: String(error) });
    }
  }

  // Task destinations: every actionable task's resolved destination must be one of the URLs
  // verified above. Destinations derive from discussion.href / forum_href, so this is a mapping
  // assertion for the receipt rather than a new network check.
  const verifiedLive = new Set(results.filter((r) => r.ok && r.name.endsWith('destination is live')).map((r) => r.target));
  for (const tool of workbench.tools) {
    for (const task of tool.first_tasks) {
      const completion = task.completion;
      const state = completion?.state ?? 'actionable';
      if (state === 'blocked') continue;
      const destination = completion?.destination_kind === 'main-forum' ? workbench.feedback.forum_href : tool.discussion.href;
      record('discord', `task ${task.id} destination is live`, destination ?? '(none)',
        destination && verifiedLive.has(destination)
          ? { ok: true }
          : { ok: false, detail: `the destination for ${tool.id}/${task.id} was not verified live` });
    }
  }

  // ---------------------------------------------------------------------------- github
  const githubHrefs = collectGithubHrefs(workbench);
  for (const href of githubHrefs) {
    try {
      const res = await fetchWithTimeout(fetchImpl, href);
      record('github', 'public URL resolves', href, res.ok ? { ok: true } : { ok: false, detail: `HTTP ${res.status}` });
    } catch (error) {
      record('github', 'public URL resolves', href, { ok: false, detail: String(error) });
    }
  }
  const declaredPublicRepos = new Set(githubHrefs.map(repoFromGithubHref).filter(Boolean));
  for (const tool of workbench.tools) {
    if (tool.source.kind === 'public-repo' && tool.source.href) {
      const repo = repoFromGithubHref(tool.source.href);
      if (repo) declaredPublicRepos.add(repo);
    }
  }
  for (const repo of [...declaredPublicRepos].sort()) {
    const target = `${GITHUB_API}/repos/djcdevelopment/${repo}`;
    try {
      const res = await fetchWithTimeout(fetchImpl, target);
      if (!res.ok) {
        record('github', 'repository is public', repo, { ok: false, detail: `HTTP ${res.status} — a private or missing repo answers 404 here` });
        continue;
      }
      const data = await res.json();
      record('github', 'repository is public', repo,
        data.private === false ? { ok: true } : { ok: false, detail: 'the API reports this repository as private while the catalog declares it public' });
    } catch (error) {
      record('github', 'repository is public', repo, { ok: false, detail: String(error) });
    }
  }

  // ---------------------------------------------------------------------------- routes
  for (const route of NAV_ROUTES) {
    const target = `${origin}${route}`;
    try {
      const res = await fetchWithTimeout(fetchImpl, target);
      const finalOrigin = res.url ? new URL(res.url).origin : origin;
      if (!res.ok) {
        record('routes', 'internal route answers 200', target, { ok: false, detail: `HTTP ${res.status}` });
      } else if (finalOrigin !== origin) {
        record('routes', 'internal route answers 200', target, { ok: false, detail: `redirected off-origin to ${res.url} — that is an auth gate or a misroute, not the page` });
      } else {
        record('routes', 'internal route answers 200', target, { ok: true });
      }
    } catch (error) {
      record('routes', 'internal route answers 200', target, { ok: false, detail: String(error) });
    }
  }

  // ---------------------------------------------------------- post-publish only from here
  if (mode === 'post-publish') {
    for (const tool of workbench.tools) {
      if (tool.access.kind !== 'site-download') continue;
      const target = `${origin}${tool.access.href}`;
      try {
        const res = await fetchWithTimeout(fetchImpl, target);
        if (!res.ok) {
          record('downloads', `${tool.id} download streams`, target, { ok: false, detail: `HTTP ${res.status}` });
          continue;
        }
        const bytes = Buffer.from(await res.arrayBuffer());
        record('downloads', `${tool.id} download streams`, target, { ok: true });
        record('downloads', `${tool.id} size matches size_bytes`, target,
          bytes.length === tool.access.size_bytes
            ? { ok: true }
            : { ok: false, detail: `served ${bytes.length} bytes, the page claims ${tool.access.size_bytes}` });
        const digest = sha256Hex(bytes);
        record('downloads', `${tool.id} digest matches the page`, target,
          digest === tool.access.sha256
            ? { ok: true }
            : { ok: false, detail: `served sha256 ${digest}, the page claims ${tool.access.sha256}` });
        const header = res.headers.get('x-download-sha256');
        record('downloads', `${tool.id} X-Download-Sha256 header matches`, target,
          header && header.toLowerCase() === tool.access.sha256
            ? { ok: true }
            : { ok: false, detail: `header says ${header ?? '(missing)'}` });
      } catch (error) {
        record('downloads', `${tool.id} download streams`, target, { ok: false, detail: String(error) });
      }
    }

    if (!localHtml) {
      record('served-artifact', 'served page hash matches the local render', `${origin}/workbench`, { ok: false, detail: `local artifact missing at ${localHtmlPath}` });
    } else {
      try {
        const res = await fetchWithTimeout(fetchImpl, `${origin}/workbench`);
        const header = res.headers.get('x-workbench-sha256');
        const localHash = sha256Hex(localHtml);
        record('served-artifact', 'served page hash matches the local render', `${origin}/workbench`,
          res.ok && header && header.toLowerCase() === localHash.toLowerCase()
            ? { ok: true }
            : { ok: false, detail: `X-Workbench-Sha256 is ${header ?? '(missing)'} (HTTP ${res.status}), local render is ${localHash}` });
      } catch (error) {
        record('served-artifact', 'served page hash matches the local render', `${origin}/workbench`, { ok: false, detail: String(error) });
      }
    }
  }

  const failures = results.filter((r) => !r.ok);
  const warnings = results.filter((r) => r.ok && r.warn);
  return {
    verdict: failures.length === 0 ? 'pass' : 'fail',
    mode,
    base_url: origin,
    checked_at: new Date(now).toISOString(),
    checks: results,
    failed_checks: failures.map((r) => `${r.class}: ${r.name} (${r.target})`),
    warnings: warnings.map((r) => `${r.class}: ${r.name} — ${r.warn}`),
  };
}

function usage() {
  console.log(`Usage:
  node scripts/workbench-verify-live.mjs --pre-publish  [options]   # discord + github + routes
  node scripts/workbench-verify-live.mjs --post-publish [options]   # + downloads + served hash

Options:
  --base-url <url>            public origin to verify against (default ${DEFAULT_BASE_URL})
  --allow-unverified-threads  downgrade missing-bot-token thread checks to warnings

The bot token resolves like the provisioning bot's: WORKBENCH_DISCORD_TOKEN, then
WORKBENCH_DISCORD_TOKEN_FILE, then ~/.baseline/workbench-discord.token or discord.env. The
receipt lands at captures/workbench-verify-live.json. This never runs from render/check — live
state belongs to the release path only.`);
}

export {
  runChecks,
  collectGithubHrefs,
  collectDiscordChannelUrls,
  parseChannelUrl,
  repoFromGithubHref,
  sha256Hex,
  DEFAULT_BASE_URL,
  NAV_ROUTES,
  FOOTER_GITHUB_LINKS,
};

const cliPath = process.argv[1] ? path.resolve(process.argv[1]) : '';
const selfPath = fileURLToPath(import.meta.url);
const invokedAsCli = process.platform === 'win32'
  ? cliPath.toLowerCase() === selfPath.toLowerCase()
  : cliPath === selfPath;

if (invokedAsCli) {
  const args = process.argv.slice(2);
  const mode = args.includes('--pre-publish') ? 'pre-publish' : args.includes('--post-publish') ? 'post-publish' : null;
  const baseUrlIndex = args.indexOf('--base-url');
  const baseUrl = baseUrlIndex >= 0 ? args[baseUrlIndex + 1] : DEFAULT_BASE_URL;
  if (!mode || (baseUrlIndex >= 0 && !baseUrl)) {
    usage();
    process.exitCode = 2;
  } else {
    const botToken = loadBotToken();
    try {
      const receipt = await runChecks({
        mode,
        baseUrl,
        fetchImpl: fetch,
        botToken,
        allowUnverifiedThreads: args.includes('--allow-unverified-threads'),
      });
      fs.mkdirSync(path.dirname(receiptPath), { recursive: true });
      fs.writeFileSync(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, 'utf8');
      for (const check of receipt.checks) {
        const tag = check.ok ? (check.warn ? 'WARN' : 'OK') : 'FAIL';
        const note = check.ok ? (check.warn ?? '') : check.detail;
        console.log(`[${tag}] ${check.class}: ${check.name} — ${check.target}${note ? ` — ${note}` : ''}`);
      }
      console.log(`\nverify-live ${receipt.verdict.toUpperCase()} (${receipt.mode}, ${receipt.base_url}) — ${receipt.checks.length} checks, ${receipt.failed_checks.length} failed, ${receipt.warnings.length} warnings`);
      console.log(`receipt: ${path.relative(process.cwd(), receiptPath)}`);
      process.exitCode = receipt.verdict === 'pass' ? 0 : 1;
    } catch (error) {
      console.error(`workbench-verify-live: ${error.message}`);
      process.exitCode = 2;
    }
  }
}
