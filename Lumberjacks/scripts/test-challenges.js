/**
 * End-to-end test: Inventory + Guild Challenge system
 *
 * Prerequisites: Gateway (4000), EventLog (4002), Progression (4003), OperatorApi (4004), Postgres on 5433
 * Usage: node scripts/test-challenges.js [gatewayUrl]
 *
 * Flow:
 * 1. Create a guild challenge: "Place 3 structures" → awards 50 guild points
 * 2. Connect a player via WebSocket (with guild_id), join region
 * 3. Place 3 structures — challenge should auto-complete after the 3rd
 * 4. Verify challenge progress and guild points
 * 5. Also test inventory: spawn item, pick it up, store it in a container
 */

const WebSocket = require("ws");

const GATEWAY = process.argv[2] || "ws://localhost:4000";
const SIM = GATEWAY.replace("ws://", "http://").replace("wss://", "https://");
const PROGRESSION = GATEWAY.replace("ws://", "http://").replace("wss://", "https://").replace(":4000", ":4003");

const guildId = "guild-" + Math.random().toString(36).slice(2, 10);

async function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function post(url, body) {
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  try {
    return { status: res.status, data: JSON.parse(text) };
  } catch {
    return { status: res.status, data: text };
  }
}

async function get(url) {
  const res = await fetch(url);
  const text = await res.text();
  try {
    return { status: res.status, data: JSON.parse(text) };
  } catch {
    return { status: res.status, data: text };
  }
}

async function patch(url, body) {
  const res = await fetch(url, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  try {
    return { status: res.status, data: JSON.parse(text) };
  } catch {
    return { status: res.status, data: text };
  }
}

function connectWs() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(GATEWAY);
    const messages = [];
    ws.on("open", () => resolve({ ws, messages }));
    ws.on("message", (data) => {
      const msg = JSON.parse(data.toString());
      messages.push(msg);
    });
    ws.on("error", reject);
  });
}

function sendWs(ws, type, payload) {
  ws.send(JSON.stringify({ type, payload, seq: Date.now() }));
}

async function main() {
  console.log(`\n=== Guild Challenge + Inventory E2E Test ===`);
  console.log(`Guild: ${guildId}\n`);

  // --- Step 1: Create a challenge ---
  console.log("1. Creating challenge: 'Place 3 structures' → 50 guild points");
  const challengeRes = await post(`${PROGRESSION}/challenges`, {
    kind: "guild",
    name: "Builder's Trial",
    trigger_event: "structure_placed",
    target: 3,
    rewards: JSON.stringify([{ type: "guild_points", amount: 50 }]),
  });
  console.log(`   Status: ${challengeRes.status}`);
  console.log(`   Challenge ID: ${challengeRes.data.challenge_id}`);
  const challengeId = challengeRes.data.challenge_id;

  // --- Step 2: Verify challenge shows in list ---
  console.log("\n2. Listing active challenges");
  const listRes = await get(`${PROGRESSION}/challenges?active=true`);
  console.log(`   Active challenges: ${listRes.data.length}`);

  // --- Step 3: Connect player and join region (with guild_id) ---
  console.log("\n3. Connecting player via WebSocket");
  const { ws, messages } = await connectWs();
  await sleep(300);

  // Capture server-assigned player_id from session_started message
  const sessionMsg = messages.find((m) => m.type === "session_started");
  if (!sessionMsg) {
    console.error("   ERROR: No session_started message received");
    ws.close();
    process.exit(1);
  }
  const playerId = sessionMsg.payload.player_id;
  console.log(`   Session started — server-assigned player: ${playerId}`);

  // Join with guild_id so challenges trigger naturally
  sendWs(ws, "join_region", { region_id: "region-spawn", guild_id: guildId });
  await sleep(500);
  console.log(
    `   Messages after join: ${messages.map((m) => m.type).join(", ")}`
  );

  // --- Step 4: Place 3 structures (triggers challenge progress via guild_id) ---
  console.log("\n4. Placing 3 structures to trigger challenge...");
  for (let i = 0; i < 3; i++) {
    sendWs(ws, "place_structure", {
      structure_type: "campfire",
      position: { x: i * 5, y: 0, z: 0 },
    });
    await sleep(600);
    console.log(`   Structure ${i + 1} placed`);
  }

  // Give progression time to process fire-and-forget calls
  await sleep(1500);

  // --- Step 5: Check challenge progress ---
  console.log("\n5. Checking challenge progress");
  const progressRes = await get(
    `${PROGRESSION}/challenges/${challengeId}/progress`
  );
  console.log(`   Challenge: ${progressRes.data.challenge_name}`);
  console.log(`   Target: ${progressRes.data.target}`);
  if (progressRes.data.guilds && progressRes.data.guilds.length > 0) {
    const gp = progressRes.data.guilds[0];
    console.log(
      `   Guild ${gp.guild_id}: ${gp.current_value}/${progressRes.data.target}`
    );
    console.log(`   Completed: ${gp.completed}`);
    if (gp.completed_at) console.log(`   Completed at: ${gp.completed_at}`);
  } else {
    console.log("   No guild progress found — guild_id may not be flowing");
  }

  // --- Step 6: Check guild points ---
  console.log("\n6. Checking guild progress");
  const guildRes = await get(`${PROGRESSION}/guilds/${guildId}/progress`);
  if (guildRes.status === 200) {
    console.log(`   Guild: ${guildRes.data.guild_id}`);
    console.log(`   Points: ${guildRes.data.points}`);
    console.log(
      `   Challenges completed: ${guildRes.data.challenges_completed}`
    );
  } else {
    console.log(`   Guild not found (status ${guildRes.status})`);
  }

  // --- Step 7: Inventory test (using server-assigned playerId) ---
  console.log("\n7. Testing inventory system");

  // Spawn an item
  const spawnRes = await post(`${SIM}/items/spawn`, {
    item_type: "wood",
    position: { x: 10, y: 0, z: 10 },
    region_id: "region-spawn",
    quantity: 5,
  });
  console.log(
    `   Spawned item: ${spawnRes.data.item_id} (${spawnRes.data.item_type} x${spawnRes.data.quantity})`
  );
  const itemId = spawnRes.data.item_id;

  // Pick it up via interact message
  sendWs(ws, "interact", { action: "pickup", item_id: itemId });
  await sleep(500);

  // Check inventory using the server-assigned playerId
  const invRes = await get(`${SIM}/players/${playerId}/inventory`);
  console.log(`   Inventory after pickup: ${JSON.stringify(invRes.data)}`);

  // Create a container on a structure
  const containerRes = await post(`${SIM}/containers`, {
    structure_id: "test-structure",
    region_id: "region-spawn",
  });
  console.log(`   Created container: ${containerRes.data.container_id}`);
  const containerId = containerRes.data.container_id;

  // Store item
  sendWs(ws, "interact", {
    action: "store",
    container_id: containerId,
    item_type: "wood",
    quantity: 3,
  });
  await sleep(500);

  // Check container contents
  const contentsRes = await get(`${SIM}/containers/${containerId}`);
  console.log(
    `   Container contents: ${JSON.stringify(contentsRes.data.items)}`
  );

  // Check remaining inventory
  const invRes2 = await get(`${SIM}/players/${playerId}/inventory`);
  console.log(`   Remaining inventory: ${JSON.stringify(invRes2.data)}`);

  // --- Cleanup ---
  ws.close();
  await sleep(300);

  console.log("\n=== Test Complete ===\n");

  // Summary
  console.log("Summary:");
  console.log("  - Challenge CRUD (create, list, get, progress)");
  console.log(
    "  - Challenge evaluation via natural gameplay (guild_id flows through)"
  );
  console.log("  - Guild points awarded on challenge completion");
  console.log("  - Item spawn, pickup, store");
  console.log("  - Container system");
  console.log("  - Player inventory tracking (server-assigned player_id)");
}

main().catch((err) => {
  console.error("Test failed:", err);
  process.exit(1);
});
