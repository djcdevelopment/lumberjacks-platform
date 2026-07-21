/**
 * Input→Tick→Broadcast pipeline smoke test
 *
 * Validates the core real-time loop:
 * 1. Three clients connect and join region-spawn
 * 2. Client 1 sends player_input, all clients verify entity_update within ~200ms
 * 3. Client 2 disconnects, others verify entity_removed
 * 4. Client 2 reconnects with resume token, verifies world_snapshot
 *
 * Only requires the Gateway service (which hosts simulation in-process).
 *
 * Usage: node scripts/test-input-broadcast.js [gatewayUrl]
 *   gatewayUrl defaults to ws://localhost:4000
 */

const WebSocket = require("ws");

const GATEWAY = process.argv[2] || "ws://localhost:4000";
const TICK_MS = 50; // 20Hz tick

let passed = 0;
let failed = 0;

function assert(condition, msg) {
  if (condition) {
    console.log(`  ✓ ${msg}`);
    passed++;
  } else {
    console.error(`  ✗ ${msg}`);
    failed++;
  }
}

async function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function connectPlayer(url = GATEWAY) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    const messages = [];

    ws.on("open", () => resolve({ ws, messages }));
    ws.on("message", (data) => {
      messages.push(JSON.parse(data.toString()));
    });
    ws.on("error", reject);
  });
}

function send(ws, type, payload) {
  ws.send(JSON.stringify({ type, payload, seq: Date.now() }));
}

function waitForMessage(player, type, timeoutMs = 2000) {
  return new Promise((resolve) => {
    // Check existing messages first
    const existing = player.messages.find((m) => m.type === type);
    if (existing) return resolve(existing);

    const start = Date.now();
    const interval = setInterval(() => {
      const msg = player.messages.find(
        (m) => m.type === type && m._checked !== true
      );
      if (msg) {
        clearInterval(interval);
        resolve(msg);
      } else if (Date.now() - start > timeoutMs) {
        clearInterval(interval);
        resolve(null);
      }
    }, 20);
  });
}

function clearMessages(player) {
  player.messages.length = 0;
}

async function main() {
  console.log(`\n=== Input→Tick→Broadcast Smoke Test ===`);
  console.log(`Gateway: ${GATEWAY}\n`);

  // --- Step 1: Connect 3 clients ---
  console.log("1. Connecting 3 clients...");
  const [c1, c2, c3] = await Promise.all([
    connectPlayer(),
    connectPlayer(),
    connectPlayer(),
  ]);
  await sleep(500);

  // Get player IDs from session_started
  for (const c of [c1, c2, c3]) {
    const session = c.messages.find((m) => m.type === "session_started");
    c.playerId = session?.payload?.player_id;
    c.resumeToken = session?.payload?.resume_token;
  }
  assert(c1.playerId, `Client 1 connected: ${c1.playerId?.slice(0, 8)}...`);
  assert(c2.playerId, `Client 2 connected: ${c2.playerId?.slice(0, 8)}...`);
  assert(c3.playerId, `Client 3 connected: ${c3.playerId?.slice(0, 8)}...`);

  // --- Step 2: All join region-spawn ---
  console.log("\n2. All clients joining region-spawn...");
  for (const c of [c1, c2, c3]) {
    send(c.ws, "join_region", { region_id: "region-spawn" });
  }
  await sleep(1000);

  for (const c of [c1, c2, c3]) {
    const snapshot = c.messages.find((m) => m.type === "world_snapshot");
    assert(snapshot, `Client got world_snapshot`);
  }

  // --- Step 3: Client 1 sends player_input, verify entity_update broadcasts ---
  console.log("\n3. Client 1 sends player_input...");
  clearMessages(c1);
  clearMessages(c2);
  clearMessages(c3);

  send(c1.ws, "player_input", {
    direction: 0, // north
    speed_percent: 100,
    action_flags: 0,
    input_seq: 1,
  });

  // Wait for 2 tick intervals to ensure the input is processed and broadcast
  await sleep(TICK_MS * 4);

  // All clients in the region should get entity_update for player 1
  const c1Updates = c1.messages.filter((m) => m.type === "entity_update");
  const c2Updates = c2.messages.filter((m) => m.type === "entity_update");
  const c3Updates = c3.messages.filter((m) => m.type === "entity_update");

  assert(c1Updates.length > 0, `Client 1 received entity_update (self)`);
  assert(c2Updates.length > 0, `Client 2 received entity_update (broadcast)`);
  assert(c3Updates.length > 0, `Client 3 received entity_update (broadcast)`);

  // Verify the update contains player 1's data
  const c2Update = c2Updates.find(
    (m) =>
      m.payload?.entity_id === c1.playerId ||
      m.payload?.data?.player_id === c1.playerId
  );
  assert(c2Update, `Broadcast contains Client 1's player data`);

  // --- Step 4: Client 2 disconnects, others should see entity_removed ---
  console.log("\n4. Client 2 disconnects...");
  clearMessages(c1);
  clearMessages(c3);

  const c2PlayerId = c2.playerId;
  c2.ws.close();
  await sleep(500);

  const c1Removed = c1.messages.find(
    (m) =>
      m.type === "entity_removed" && m.payload?.entity_id === c2PlayerId
  );
  const c3Removed = c3.messages.find(
    (m) =>
      m.type === "entity_removed" && m.payload?.entity_id === c2PlayerId
  );
  assert(c1Removed, `Client 1 received entity_removed for Client 2`);
  assert(c3Removed, `Client 3 received entity_removed for Client 2`);

  // --- Step 5: Client 2 reconnects with resume token ---
  console.log("\n5. Client 2 reconnects with resume token...");
  const resumeUrl = `${GATEWAY}?resume=${c2.resumeToken}`;
  const c2r = await connectPlayer(resumeUrl);
  await sleep(1000);

  const resumeSession = c2r.messages.find(
    (m) => m.type === "session_started" && m.payload?.resumed === true
  );
  assert(resumeSession, `Client 2 got session_started with resumed=true`);

  const resumeSnapshot = c2r.messages.find((m) => m.type === "world_snapshot");
  assert(resumeSnapshot, `Client 2 got world_snapshot after resume`);

  if (resumeSnapshot) {
    const entities = resumeSnapshot.payload?.entities || [];
    assert(entities.length > 0, `Resume snapshot contains entities (${entities.length})`);
  }

  // --- Cleanup ---
  console.log("\n6. Cleaning up...");
  c1.ws.close();
  c3.ws.close();
  c2r.ws.close();
  await sleep(300);

  // --- Summary ---
  console.log(`\n=== Results ===`);
  console.log(`  Passed: ${passed}`);
  console.log(`  Failed: ${failed}`);
  console.log(``);

  if (failed === 0) {
    console.log("  ALL CHECKS PASSED\n");
  } else {
    console.log("  SOME CHECKS FAILED\n");
    process.exit(1);
  }
}

main().catch((err) => {
  console.error("Test failed:", err);
  process.exit(1);
});
