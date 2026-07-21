#!/usr/bin/env node
/**
 * load-test-dual-channel.js — Heavy-load gut check for dual-channel transport.
 *
 * Spawns N bot players that:
 *   1. Connect via WebSocket (binary protocol)
 *   2. Bind a UDP channel using their session token
 *   3. Send player_input at 20Hz via UDP (binary, 19 bytes per packet)
 *   4. Receive entity_update via both WebSocket and UDP
 *   5. Print a live dashboard every 2 seconds
 *
 * Usage:
 *   node scripts/load-test-dual-channel.js [gateway_url] [player_count] [duration_sec]
 *
 * Examples:
 *   node scripts/load-test-dual-channel.js                          # 20 bots, localhost, 30s
 *   node scripts/load-test-dual-channel.js ws://localhost:4000 50   # 50 bots, 30s
 *   node scripts/load-test-dual-channel.js ws://localhost:4000 100 60
 *
 * What you'll see:
 *   - Live connection count (WS + UDP binds)
 *   - Input packets sent (UDP) vs entity updates received (WS binary + UDP)
 *   - Bytes/sec per channel
 *   - Observed tick rate
 *   - Per-bot latency estimate (input_seq round-trip)
 */

const WebSocket = require("ws");
const dgram = require("dgram");

// ── Config ──────────────────────────────────────────────────────────
const GATEWAY_WS = process.argv[2] || "ws://localhost:4000";
const BOT_COUNT  = parseInt(process.argv[3] || "20", 10);
const DURATION_S = parseInt(process.argv[4] || "30", 10);
const INPUT_HZ   = 20;    // match server tick rate
const REGION_ID  = "region-spawn";

// Derive UDP host/port from WS URL
const wsUrl = new URL(GATEWAY_WS);
const UDP_HOST = wsUrl.hostname;

// UDP port resolution: explicit env wins; otherwise each bot uses the udp_port field
// from its own session_started payload (falls back to 4005 until that arrives).
const UDP_PORT_ENV = process.env.UDP_PORT ? parseInt(process.env.UDP_PORT, 10) : null;
const DEFAULT_UDP_PORT = 4005;
// Display-only label for the dashboard — the real port is resolved per-bot (see Bot.udpPort).
const udpPortLabel = () => UDP_PORT_ENV ? String(UDP_PORT_ENV) : `${DEFAULT_UDP_PORT} (default, per-bot from session_started)`;

// RTT_WARMUP_S: seconds of RTT samples to exclude from the steady-state summary, measured
// from when the load test actually starts (all bots connected — see loadTestStartTime),
// not from process start. Default 15s covers connection ramp-up + a few seconds of settling.
const RTT_WARMUP_S = process.env.RTT_WARMUP_S !== undefined ? parseFloat(process.env.RTT_WARMUP_S) : 15;

// BOT_WANDER=1: bots head toward a random waypoint instead of pure random-walk.
// Unset (default) leaves the original 5%-chance-per-tick random-walk behavior unchanged.
const BOT_WANDER = process.env.BOT_WANDER === "1";
const WANDER_HALF_EXTENT = 450;      // waypoints uniform in [-450, 450] on X/Z (inside the 500u region bounds)
const WANDER_ARRIVAL_DIST = 20;      // re-pick once within this many units of the waypoint
const WANDER_MAX_AGE_MS = 10_000;    // ...or after 10s, whichever comes first
const WANDER_SPEED_PERCENT = 80;     // matches the speed already sent by _startInput
const WANDER_MAX_SPEED_PER_TICK = 10.0; // mirrors SimulationStep.MaxSpeedPerTick (10 u/tick @ 20Hz, 100%)

// ── Binary Protocol Helpers ─────────────────────────────────────────

// BitWriter — mirrors the C# BitWriter (big-endian bit order)
class BitWriter {
  constructor(size) {
    this.buf = Buffer.alloc(size);
    this.bitPos = 0;
  }
  writeBits(value, count) {
    for (let i = count - 1; i >= 0; i--) {
      const byteIdx = this.bitPos >> 3;
      const bitIdx = 7 - (this.bitPos & 7);
      if ((value >> i) & 1) {
        this.buf[byteIdx] |= (1 << bitIdx);
      }
      this.bitPos++;
    }
  }
  writeBool(v) { this.writeBits(v ? 1 : 0, 1); }
  writeByte(v) { this.writeBits(v & 0xFF, 8); }
  writeUInt16(v) { this.writeBits(v & 0xFFFF, 16); }
  writeUInt32(v) { this.writeBits(v >>> 0, 32); }
  get byteLength() { return Math.ceil(this.bitPos / 8); }
  get buffer() { return this.buf.subarray(0, this.byteLength); }
}

// BitReader — mirrors the C# BitReader
class BitReader {
  constructor(buf) {
    this.buf = buf;
    this.bitPos = 0;
  }
  readBits(count) {
    let value = 0;
    for (let i = 0; i < count; i++) {
      const byteIdx = this.bitPos >> 3;
      const bitIdx = 7 - (this.bitPos & 7);
      value = (value << 1) | ((this.buf[byteIdx] >> bitIdx) & 1);
      this.bitPos++;
    }
    return value;
  }
  readBool() { return this.readBits(1) === 1; }
  readByte() { return this.readBits(8); }
  readUInt16() { return this.readBits(16); }
  readUInt32() { return this.readBits(32); }

  readVarInt() {
    let result = 0;
    let shift = 0;
    let chunk;
    do {
      chunk = this.readByte();
      result |= (chunk & 0x7F) << shift;
      shift += 7;
    } while ((chunk & 0x80) !== 0);
    return result;
  }
}

// Message type IDs (from MessageTypeId.cs)
const MSG = {
  PlayerInput:  6,
  EntityUpdate: 12,
  EntityRemoved: 13,
};

// Build a binary envelope + PlayerInput payload
function buildPlayerInputPacket(direction, speed, inputSeq) {
  // Payload: direction(1) + speed(1) + actionFlags(1) + inputSeq(2) = 5 bytes
  const payloadSize = 5;

  // Envelope header: 43 bits = 6 bytes
  const w = new BitWriter(6 + payloadSize);
  // Header
  w.writeBits(1, 4);              // version = 1
  w.writeBits(MSG.PlayerInput, 6); // type = PlayerInput (6)
  w.writeBool(true);               // lane = Datagram (1)
  w.writeUInt16(0);                // seq = 0 (datagrams don't need ordering)
  w.writeUInt16(payloadSize);      // payloadLen = 5

  // Align to byte boundry (43 bits + 5 bits = 48 bits / 6 bytes)
  w.writeBits(0, 5);

  // Payload
  w.writeByte(direction);
  w.writeByte(speed);
  w.writeByte(0);                  // actionFlags = 0
  w.writeUInt16(inputSeq);

  return w.buffer;
}

// Parse a binary envelope header from a buffer
function parseEnvelopeHeader(buf) {
  if (buf.length < 6) return null;
  const r = new BitReader(buf);
  return {
    version:    r.readBits(4),
    type:       r.readBits(6),
    lane:       r.readBool() ? "datagram" : "reliable",
    seq:        r.readUInt16(),
    payloadLen: r.readUInt16(),
  };
}

// Convert a UInt64 decimal string to 8-byte LE buffer
function udpTokenToBytes(tokenStr) {
  const n = BigInt(tokenStr);
  const buf = Buffer.alloc(8);
  buf.writeBigUInt64LE(n);
  return buf;
}

// ── Metrics ─────────────────────────────────────────────────────────
const metrics = {
  wsConnected: 0,
  udpBound: 0,
  sessionStarted: 0,
  worldSnapshots: 0,

  udpInputsSent: 0,
  udpInputBytes: 0,

  wsMessagesRecv: 0,
  wsBinaryRecv: 0,
  wsJsonRecv: 0,
  wsBytesRecv: 0,

  udpRecv: 0,
  udpBytesRecv: 0,

  entityUpdatesWs: 0,
  entityUpdatesUdp: 0,
  entityRemoved: 0,

  errors: 0,
  disconnects: 0,
  lastTick: 0,
  ticksSeen: new Set(),

  // Latency tracking — latencySamples stays capped at the last 100 for the live dashboard
  // (unchanged). rttLog is the FULL, per-bot, timestamped history used for the steady-state
  // summary below; only populated once loadTestStartTime is set (i.e. after the connect/join
  // ramp-up), so it never needs its own warmup edge-case handling.
  latencySamples: [],
  inputSeqSentAt: new Map(), // inputSeq → timestamp
  rttLog: [], // { botId, ms, atMs } — atMs is relative to loadTestStartTime
};

// Set once all bots have connected and the timed run begins (see main()). RTT samples
// recorded before this is set (during staggered connect/join) are not added to rttLog.
let loadTestStartTime = null;

// ── Bot Class ───────────────────────────────────────────────────────
class Bot {
  constructor(id) {
    this.id = id;
    this.ws = null;
    this.udpSocket = null;
    this.udpToken = null;
    this.udpTokenBytes = null;
    this.playerId = null;
    this.inputSeq = 0;
    this.inputInterval = null;
    this.direction = Math.floor(Math.random() * 256); // random walk direction
    this.joined = false;
    this.udpBound = false;
    this.udpPort = UDP_PORT_ENV || DEFAULT_UDP_PORT; // may be replaced by session_started's udp_port

    // BOT_WANDER state: dead-reckoned position estimate (server is authoritative — this is
    // only used to steer toward waypoints, not for gameplay correctness) + current waypoint.
    this.pos = { x: 0, z: 0 };
    this.waypoint = null;
    this.waypointSetAt = 0;
  }

  connect() {
    return new Promise((resolve) => {
      const url = `${GATEWAY_WS}?protocol=binary`;
      this.ws = new WebSocket(url);
      this.ws.binaryType = "arraybuffer";

      this.ws.on("open", () => {
        metrics.wsConnected++;
        this._connected = true;
        resolve();
      });

      this.ws.on("message", (data, isBinary) => {
        metrics.wsMessagesRecv++;
        if (isBinary) {
          this._handleBinary(Buffer.isBuffer(data) ? data : Buffer.from(data));
        } else {
          this._handleJson(typeof data === "string" ? data : data.toString());
        }
      });

      this.ws.on("error", () => { metrics.errors++; });
      this.ws.on("close", () => {
        metrics.disconnects++;
        if (this._connected) metrics.wsConnected--;
        this._connected = false;
        this.stopInput();
      });

      // Timeout if connect takes too long
      setTimeout(() => resolve(), 5000);
    });
  }

  _handleJson(text) {
    metrics.wsJsonRecv++;
    metrics.wsBytesRecv += Buffer.byteLength(text);
    try {
      const msg = JSON.parse(text);
      if (msg.type === "session_started") {
        this.playerId = msg.payload.player_id;
        this.udpToken = msg.payload.udp_token;
        this.udpTokenBytes = udpTokenToBytes(this.udpToken);
        // Prefer the server-advertised udp_port unless UDP_PORT was explicitly set.
        if (!UDP_PORT_ENV && typeof msg.payload.udp_port === "number" && msg.payload.udp_port > 0) {
          this.udpPort = msg.payload.udp_port;
        }
        metrics.sessionStarted++;
        // Join region
        this._sendJson("join_region", { region_id: REGION_ID });
      } else if (msg.type === "world_snapshot") {
        metrics.worldSnapshots++;
        this.joined = true;
        // Bind UDP and start sending input
        this._bindUdp();
        this._startInput();
      } else if (msg.type === "entity_update") {
        metrics.entityUpdatesWs++;
        // Check for latency sample
        const seq = msg.payload?._data?.last_input_seq || msg.payload?.last_input_seq;
        this._recordLatency(seq);
      }
    } catch { /* ignore parse errors */ }
  }

  _handleBinary(buf) {
    metrics.wsBinaryRecv++;
    metrics.wsBytesRecv += buf.length;
    const header = parseEnvelopeHeader(buf);
    if (!header) return;

    if (header.type === MSG.EntityUpdate) {
      metrics.entityUpdatesWs++;
      this._extractBinaryLatency(buf.subarray(6));
    } else if (header.type === MSG.EntityRemoved) {
      metrics.entityRemoved++;
    }
  }

  _extractBinaryLatency(payloadBuf) {
    try {
      const r = new BitReader(payloadBuf);
      const entityIdLen = r.readVarInt();
      for (let i = 0; i < entityIdLen; i++) r.readByte(); // skip string
      for (let i = 0; i < 12; i++) r.readByte(); // skip pos + vel (12 bytes)
      r.readUInt16(); // skip heading
      const seq = r.readUInt16(); // read lastInputSeq
      this._recordLatency(seq);
    } catch { /* ignore bad parse */ }
  }

  _bindUdp() {
    if (!this.udpTokenBytes) return;
    this.udpSocket = dgram.createSocket("udp4");

    this.udpSocket.on("message", (msg) => {
      metrics.udpRecv++;
      metrics.udpBytesRecv += msg.length;
      // UDP inbound: [token:8] [envelope:6+] [payload]
      if (msg.length > 14) {
        const envBuf = msg.subarray(8);
        const header = parseEnvelopeHeader(envBuf);
        if (header && header.type === MSG.EntityUpdate) {
          metrics.entityUpdatesUdp++;
          this._extractBinaryLatency(envBuf.subarray(6));
        }
      }
    });

    this.udpSocket.on("error", () => { /* ignore */ });

    // Send a bind packet (first UDP packet establishes the session mapping)
    const bindPacket = this._buildUdpInput(0, 0, 0);
    this.udpSocket.send(bindPacket, this.udpPort, UDP_HOST, (err) => {
      if (!err) {
        metrics.udpBound++;
        this.udpBound = true;
      }
    });
  }

  _buildUdpInput(direction, speed, seq) {
    const envelope = buildPlayerInputPacket(direction, speed, seq);
    // Prepend 8-byte token
    return Buffer.concat([this.udpTokenBytes, envelope]);
  }

  // Pick a new waypoint uniformly within ±WANDER_HALF_EXTENT on X/Z.
  _pickWaypoint() {
    this.waypoint = {
      x: (Math.random() * 2 - 1) * WANDER_HALF_EXTENT,
      z: (Math.random() * 2 - 1) * WANDER_HALF_EXTENT,
    };
    this.waypointSetAt = Date.now();
  }

  // Steer this.direction toward the current waypoint, re-picking on arrival or timeout.
  // Also dead-reckons this.pos using the same physics SimulationStep applies server-side,
  // so repeated calls keep converging on the waypoint. This is a client-side estimate only
  // (it doesn't know about World:SpawnSpread or server-side corrections) — good enough for
  // steering a load-test bot, not meant to track true position.
  _wanderStep() {
    const now = Date.now();
    if (!this.waypoint || now - this.waypointSetAt > WANDER_MAX_AGE_MS) {
      this._pickWaypoint();
    }

    let dx = this.waypoint.x - this.pos.x;
    let dz = this.waypoint.z - this.pos.z;
    if (Math.sqrt(dx * dx + dz * dz) < WANDER_ARRIVAL_DIST) {
      this._pickWaypoint();
      dx = this.waypoint.x - this.pos.x;
      dz = this.waypoint.z - this.pos.z;
    }

    let headingDeg = Math.atan2(dx, dz) * 180 / Math.PI;
    if (headingDeg < 0) headingDeg += 360;
    this.direction = Math.round((headingDeg / 360) * 255) & 0xFF;

    const headingRad = headingDeg * Math.PI / 180;
    const speedPerTick = (WANDER_SPEED_PERCENT / 100) * WANDER_MAX_SPEED_PER_TICK;
    this.pos.x += Math.sin(headingRad) * speedPerTick;
    this.pos.z += Math.cos(headingRad) * speedPerTick;
  }

  _startInput() {
    // Send player_input at INPUT_HZ via UDP
    this.inputInterval = setInterval(() => {
      if (!this.udpBound || !this.udpSocket) return;

      this.inputSeq = (this.inputSeq + 1) & 0xFFFF;

      if (BOT_WANDER) {
        this._wanderStep();
      } else if (Math.random() < 0.05) {
        // Original behavior: slowly wander by changing direction occasionally
        this.direction = Math.floor(Math.random() * 256);
      }

      const packet = this._buildUdpInput(this.direction, WANDER_SPEED_PERCENT, this.inputSeq);
      this.udpSocket.send(packet, this.udpPort, UDP_HOST);

      metrics.udpInputsSent++;
      metrics.udpInputBytes += packet.length;

      // Track latency for every 20th input
      if (this.inputSeq % 20 === 0) {
        metrics.inputSeqSentAt.set(`${this.playerId}:${this.inputSeq}`, Date.now());
      }
    }, 1000 / INPUT_HZ);
  }

  _sendJson(type, payload) {
    if (this.ws?.readyState === WebSocket.OPEN) {
      const msg = JSON.stringify({ type, payload });
      this.ws.send(msg);
    }
  }

  _recordLatency(seq) {
    if (!seq || !this.playerId) return;
    const key = `${this.playerId}:${seq}`;
    const sentAt = metrics.inputSeqSentAt.get(key);
    if (sentAt) {
      const rttMs = Date.now() - sentAt;
      metrics.latencySamples.push(rttMs);
      metrics.inputSeqSentAt.delete(key);
      // Keep only last 100 samples
      if (metrics.latencySamples.length > 100) {
        metrics.latencySamples = metrics.latencySamples.slice(-100);
      }
      // Full history for the steady-state summary — only once the timed run has actually
      // started, so connect/join ramp-up noise never enters it.
      if (loadTestStartTime !== null) {
        metrics.rttLog.push({ botId: this.id, ms: rttMs, atMs: Date.now() - loadTestStartTime });
      }
    }
  }

  stopInput() {
    if (this.inputInterval) clearInterval(this.inputInterval);
    this.inputInterval = null;
  }

  disconnect() {
    this.stopInput();
    if (this.udpSocket) {
      try { this.udpSocket.close(); } catch {}
    }
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.close();
    }
  }
}

// ── Dashboard ───────────────────────────────────────────────────────
let prevMetrics = {};
let startTime = Date.now();

function snapshotMetrics() {
  return {
    t: Date.now(),
    udpInputsSent: metrics.udpInputsSent,
    udpInputBytes: metrics.udpInputBytes,
    wsBytesRecv: metrics.wsBytesRecv,
    udpBytesRecv: metrics.udpBytesRecv,
    entityUpdatesWs: metrics.entityUpdatesWs,
    entityUpdatesUdp: metrics.entityUpdatesUdp,
    wsMessagesRecv: metrics.wsMessagesRecv,
  };
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function printDashboard() {
  const now = snapshotMetrics();
  const dt = prevMetrics.t ? (now.t - prevMetrics.t) / 1000 : 2;

  const udpInputRate = Math.round((now.udpInputsSent - (prevMetrics.udpInputsSent || 0)) / dt);
  const wsUpdateRate = Math.round((now.entityUpdatesWs - (prevMetrics.entityUpdatesWs || 0)) / dt);
  const udpUpdateRate = Math.round((now.entityUpdatesUdp - (prevMetrics.entityUpdatesUdp || 0)) / dt);
  const wsBytesPerSec = (now.wsBytesRecv - (prevMetrics.wsBytesRecv || 0)) / dt;
  const udpBytesPerSec = (now.udpBytesRecv - (prevMetrics.udpBytesRecv || 0)) / dt;
  const udpOutBytesPerSec = (now.udpInputBytes - (prevMetrics.udpInputBytes || 0)) / dt;

  const elapsed = ((Date.now() - startTime) / 1000).toFixed(0);
  const remaining = DURATION_S - parseInt(elapsed, 10);

  const avgLatency = metrics.latencySamples.length > 0
    ? (metrics.latencySamples.reduce((a, b) => a + b, 0) / metrics.latencySamples.length).toFixed(0)
    : "n/a";

  console.clear();
  console.log("=".repeat(68));
  console.log("  DUAL-CHANNEL LOAD TEST");
  console.log("=".repeat(68));
  console.log(`  Target:  ${GATEWAY_WS}  (UDP → ${UDP_HOST}:${udpPortLabel()})`);
  console.log(`  Bots:    ${BOT_COUNT}   Duration: ${DURATION_S}s   Elapsed: ${elapsed}s   Left: ${remaining}s`);
  console.log("-".repeat(68));

  console.log("\n  CONNECTIONS");
  console.log(`    WebSocket connected:  ${metrics.wsConnected}`);
  console.log(`    UDP bound:            ${metrics.udpBound}`);
  console.log(`    Sessions started:     ${metrics.sessionStarted}`);
  console.log(`    World snapshots:      ${metrics.worldSnapshots}`);
  console.log(`    Errors:               ${metrics.errors}`);
  console.log(`    Disconnects:          ${metrics.disconnects}`);

  console.log("\n  OUTBOUND (client → server)");
  console.log(`    UDP player_input:     ${udpInputRate}/s   (total: ${metrics.udpInputsSent})`);
  console.log(`    UDP bandwidth out:    ${formatBytes(udpOutBytesPerSec)}/s`);

  console.log("\n  INBOUND (server → client)");
  console.log(`    WS entity_update:     ${wsUpdateRate}/s   (total: ${metrics.entityUpdatesWs})`);
  console.log(`    UDP entity_update:    ${udpUpdateRate}/s   (total: ${metrics.entityUpdatesUdp})`);
  console.log(`    WS bandwidth in:      ${formatBytes(wsBytesPerSec)}/s`);
  console.log(`    UDP bandwidth in:     ${formatBytes(udpBytesPerSec)}/s`);
  console.log(`    WS messages (total):  ${metrics.wsMessagesRecv}  (binary: ${metrics.wsBinaryRecv}  json: ${metrics.wsJsonRecv})`);

  console.log("\n  LATENCY");
  console.log(`    Avg round-trip:       ${avgLatency} ms   (samples: ${metrics.latencySamples.length})`);

  const totalBwIn = wsBytesPerSec + udpBytesPerSec;
  const udpPct = totalBwIn > 0 ? ((udpBytesPerSec / totalBwIn) * 100).toFixed(1) : "0.0";
  console.log("\n  CHANNEL SPLIT");
  console.log(`    Total inbound:        ${formatBytes(totalBwIn)}/s`);
  console.log(`    UDP share:            ${udpPct}%`);
  console.log(`    WS share:             ${(100 - parseFloat(udpPct)).toFixed(1)}%`);

  console.log("\n" + "=".repeat(68));

  prevMetrics = now;
}

// ── Steady-state RTT summary ───────────────────────────────────────
// Appended after the existing FINAL SUMMARY block (does not replace or reshape it — the
// whole-run "Avg latency" line above stays as-is for continuity). Excludes RTT samples from
// the first RTT_WARMUP_S seconds of the timed run, then reports avg/p50/p95/max both overall
// and split by bot-index quartile (first 25% of bots connected vs last 25%) — the fairness
// signal: a send loop that serves early-connected sessions first every tick should show the
// last-quartile bots with a visibly worse RTT distribution than the first.

function percentile(sortedAscending, p) {
  if (sortedAscending.length === 0) return null;
  const rank = Math.min(sortedAscending.length, Math.max(1, Math.ceil(p * sortedAscending.length)));
  return sortedAscending[rank - 1];
}

function rttStats(samples) {
  if (samples.length === 0) return null;
  const ms = samples.map(s => s.ms).sort((a, b) => a - b);
  const avg = ms.reduce((a, b) => a + b, 0) / ms.length;
  return {
    count: ms.length,
    avg,
    p50: percentile(ms, 0.50),
    p95: percentile(ms, 0.95),
    max: ms[ms.length - 1],
  };
}

function formatRttStats(label, stats) {
  if (!stats) {
    console.log(`  ${label}: n/a (no samples)`);
    return;
  }
  console.log(`  ${label}: avg=${stats.avg.toFixed(1)}ms  p50=${stats.p50.toFixed(0)}ms  p95=${stats.p95.toFixed(0)}ms  max=${stats.max.toFixed(0)}ms  (n=${stats.count})`);
}

function printSteadyStateRttSummary() {
  const warmupMs = RTT_WARMUP_S * 1000;
  const steadySamples = metrics.rttLog.filter(s => s.atMs >= warmupMs);

  console.log("\n" + "=".repeat(68));
  console.log(`  STEADY-STATE RTT SUMMARY  (excludes first ${RTT_WARMUP_S}s of the run)`);
  console.log("=".repeat(68));
  console.log(`  Samples: ${steadySamples.length} of ${metrics.rttLog.length} total (RTT_WARMUP_S=${RTT_WARMUP_S})`);
  console.log("");

  formatRttStats("Overall".padEnd(24), rttStats(steadySamples));

  // Fairness split: first 25% of bots (by connection order / id) vs last 25%.
  const quartileSize = Math.max(1, Math.floor(BOT_COUNT * 0.25));
  const firstQuartileMax = quartileSize - 1;
  const lastQuartileMin = BOT_COUNT - quartileSize;

  const firstQuartileSamples = steadySamples.filter(s => s.botId <= firstQuartileMax);
  const lastQuartileSamples = steadySamples.filter(s => s.botId >= lastQuartileMin);

  console.log("");
  formatRttStats(`First 25% (bots 0-${firstQuartileMax})`.padEnd(24), rttStats(firstQuartileSamples));
  formatRttStats(`Last 25% (bots ${lastQuartileMin}-${BOT_COUNT - 1})`.padEnd(24), rttStats(lastQuartileSamples));

  console.log("=".repeat(68));
}

// ── Main ────────────────────────────────────────────────────────────
async function main() {
  console.log(`\nSpawning ${BOT_COUNT} bots against ${GATEWAY_WS}...`);
  console.log(`UDP target: ${UDP_HOST}:${udpPortLabel()}`);
  if (BOT_WANDER) console.log(`Bot movement: BOT_WANDER=1 (waypoint-seeking, ±${WANDER_HALF_EXTENT}u)`);
  console.log(`Duration: ${DURATION_S} seconds\n`);

  const bots = [];

  // Stagger connections: 5 per 100ms to avoid overwhelming the server
  const BATCH_SIZE = 5;
  const BATCH_DELAY = 100;

  for (let i = 0; i < BOT_COUNT; i += BATCH_SIZE) {
    const batch = [];
    for (let j = i; j < Math.min(i + BATCH_SIZE, BOT_COUNT); j++) {
      const bot = new Bot(j);
      bots.push(bot);
      batch.push(bot.connect());
    }
    await Promise.all(batch);
    if (i + BATCH_SIZE < BOT_COUNT) {
      await new Promise(r => setTimeout(r, BATCH_DELAY));
    }
  }

  console.log(`All ${bots.length} bots connected. Starting load test...\n`);
  startTime = Date.now();
  loadTestStartTime = startTime; // marks t=0 for the steady-state RTT summary below
  prevMetrics = snapshotMetrics();

  // Dashboard update interval
  const dashboardInterval = setInterval(printDashboard, 2000);

  // Run for DURATION_S seconds
  await new Promise(r => setTimeout(r, DURATION_S * 1000));

  // Final dashboard
  clearInterval(dashboardInterval);
  printDashboard();

  // Disconnect all bots
  console.log("\nDisconnecting bots...");
  for (const bot of bots) {
    bot.disconnect();
  }

  // Wait a moment for clean disconnect
  await new Promise(r => setTimeout(r, 1000));

  // Final summary
  console.log("\n" + "=".repeat(68));
  console.log("  FINAL SUMMARY");
  console.log("=".repeat(68));
  console.log(`  Total UDP inputs sent:      ${metrics.udpInputsSent}`);
  console.log(`  Total UDP bytes out:        ${formatBytes(metrics.udpInputBytes)}`);
  console.log(`  Total WS entity_updates:    ${metrics.entityUpdatesWs}`);
  console.log(`  Total UDP entity_updates:   ${metrics.entityUpdatesUdp}`);
  console.log(`  Total WS bytes in:          ${formatBytes(metrics.wsBytesRecv)}`);
  console.log(`  Total UDP bytes in:         ${formatBytes(metrics.udpBytesRecv)}`);
  console.log(`  Errors:                     ${metrics.errors}`);
  console.log(`  Disconnects:                ${metrics.disconnects}`);

  const avgLat = metrics.latencySamples.length > 0
    ? (metrics.latencySamples.reduce((a, b) => a + b, 0) / metrics.latencySamples.length).toFixed(0)
    : "n/a";
  console.log(`  Avg latency:                ${avgLat} ms`);

  if (metrics.entityUpdatesUdp > 0) {
    console.log(`\n  ✓ UDP channel is ACTIVE — entity updates flowing via datagrams`);
  } else if (metrics.entityUpdatesWs > 0) {
    console.log(`\n  ⚠ UDP channel NOT receiving — updates coming via WebSocket only`);
    console.log(`    (This is expected if Gateway's UdpTransport isn't bound or sending)`);
  } else {
    console.log(`\n  ✗ No entity updates received on either channel`);
  }

  console.log("=".repeat(68));

  printSteadyStateRttSummary();

  process.exit(0);
}

main().catch(err => {
  console.error("Fatal error:", err);
  process.exit(1);
});
