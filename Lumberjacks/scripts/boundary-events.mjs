#!/usr/bin/env node
import { createReadStream } from 'node:fs';
import { readdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import readline from 'node:readline';

const command = process.argv[2] ?? 'check';
const root = resolve(process.argv[3] ?? process.env.BOUNDARY_EVENTS_PATH ?? './telemetry/boundary-events');
const required = ['schema_version', 'event_version', 'event_id', 'timestamp_utc', 'event_type', 'trace_id', 'span_id', 'source', 'data'];
const knownTypes = new Set(['identity.resolved', 'authorization.decided', 'zdo.batch.queued', 'request.completed']);

async function filesUnder(directory) {
  const result = [];
  async function walk(current) {
    let entries;
    try { entries = await readdir(current, { withFileTypes: true }); } catch (error) {
      if (error.code === 'ENOENT') return;
      throw error;
    }
    for (const entry of entries) {
      const path = join(current, entry.name);
      if (entry.isDirectory()) await walk(path);
      else if (entry.name.endsWith('.jsonl') || entry.name.endsWith('.open.jsonl')) result.push(path);
    }
  }
  await walk(directory);
  return result.sort();
}

async function readRows(path, stats, collect) {
  const input = createReadStream(path, { encoding: 'utf8' });
  const lines = readline.createInterface({ input, crlfDelay: Infinity });
  let lineNumber = 0;
  for await (const line of lines) {
    lineNumber++;
    if (!line.trim()) continue;
    let row;
    try { row = JSON.parse(line); } catch {
      if (path.endsWith('.open.jsonl')) stats.truncated++;
      else stats.malformed++;
      stats.errors.push(`${path}:${lineNumber}: ${path.endsWith('.open.jsonl') ? 'truncated' : 'malformed JSON'}`);
      continue;
    }
    const missing = required.filter((key) => !Object.hasOwn(row, key));
    if (missing.length) {
      stats.malformed++;
      stats.errors.push(`${path}:${lineNumber}: missing ${missing.join(', ')}`);
      continue;
    }
    if (!row.source || typeof row.source !== 'object' || !row.data || typeof row.data !== 'object') {
      stats.malformed++;
      stats.errors.push(`${path}:${lineNumber}: source and data must be objects`);
      continue;
    }
    if (!knownTypes.has(row.event_type)) stats.unknownTypes.add(row.event_type);
    stats.rows++;
    if (row.trace_id == null) stats.nulls.trace_id++;
    if (row.span_id == null) stats.nulls.span_id++;
    collect(row);
  }
}

const paths = await filesUnder(root);
const stats = { rows: 0, malformed: 0, truncated: 0, errors: [], unknownTypes: new Set(), nulls: { trace_id: 0, span_id: 0 } };
const rows = [];
for (const path of paths) await readRows(path, stats, (row) => rows.push(row));

if (command === 'check') {
  console.log(JSON.stringify({ root, files: paths.length, rows: stats.rows, malformed: stats.malformed, truncated: stats.truncated, unknown_event_types: [...stats.unknownTypes], errors: stats.errors }, null, 2));
  process.exitCode = stats.malformed ? 1 : 0;
} else if (command === 'summarize') {
  const count = (items) => Object.fromEntries([...items.entries()].sort(([a], [b]) => a.localeCompare(b)));
  const events = new Map(), versions = new Map(), auth = new Map(), combinations = new Map(), durations = new Map(), missing = new Map();
  for (const row of rows) {
    events.set(row.event_type, (events.get(row.event_type) ?? 0) + 1);
    const version = `${row.schema_version}/${row.event_version}`;
    versions.set(version, (versions.get(version) ?? 0) + 1);
    const data = row.data;
    if (row.event_type === 'authorization.decided') {
      const key = `${data.result ?? '<missing>'}/${data.reason ?? '<missing>'}`;
      auth.set(key, (auth.get(key) ?? 0) + 1);
      const combo = `${data.required_capabilities ?? '<missing>'} -> ${data.granted_capabilities ?? '<missing>'}`;
      combinations.set(combo, (combinations.get(combo) ?? 0) + 1);
    }
    if (typeof data.duration_ms === 'number') {
      const values = durations.get(row.event_type) ?? [];
      values.push(data.duration_ms); durations.set(row.event_type, values);
    }
    for (const key of Object.keys(data)) if (data[key] == null || data[key] === '') missing.set(`${row.event_type}.${key}`, (missing.get(`${row.event_type}.${key}`) ?? 0) + 1);
  }
  console.log(JSON.stringify({ root, files: paths.length, rows: stats.rows, malformed: stats.malformed, by_event_type: count(events), by_version: count(versions), authorization_result_reason: count(auth), required_granted_capabilities: count(combinations), missing_or_null_data_fields: count(missing), durations_ms: Object.fromEntries([...durations].map(([key, values]) => [key, { count: values.length, min: Math.min(...values), max: Math.max(...values), average: values.reduce((a, b) => a + b, 0) / values.length }])) }, null, 2));
} else {
  console.error(`Usage: node scripts/boundary-events.mjs <check|summarize> [path]`);
  process.exitCode = 2;
}
