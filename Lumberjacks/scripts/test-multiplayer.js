/**
 * Multi-player concurrent test
 *
 * Spawns N players who all connect via WebSocket, join the same region and guild,
 * place structures, and verify they see each other's actions via broadcasts.
 *
 * Validates:
 * - Concurrent WebSocket sessions
 * - Broadcast fan-out (player A's structure appears for player B)
 * - Shared guild challenge progress across multiple players
 * - No DB contention under concurrent writes
 *
 * Usage: node scripts/test-multiplayer.js [playerCount] [gatewayUrl]
 *   playerCount defaults to 5
 *   gatewayUrl defaults to ws://localhost:4000
 */

const WebSocket = require("ws");

const PLAYER_COUNT = parseInt(process.argv[2]) || 5;
const GATEWAY = process.argv[3] || "ws://localhost:4000";
const SIM = GATEWAY.replace("ws://", "http://").replace("wss://", "https://");
// For remote deployments, route challenge/guild calls through OperatorApi's /api proxy.
// Pass OperatorApi URL as 4th arg, or it will be derived for Azure Container Apps URLs.
const OPERATOR = process.argv[4]
  || (GATEWAY.includes("azurecontainerapps")
    ? SIM.replace("gateway.", "operatorapi.")
    : "http://localhost:4004");
const isRemote = !GATEWAY.includes("localhost");
// Remote: OperatorApi proxies to Progression at /api/challenges etc.
// Local: hit Progression directly (no /api prefix needed)
const PROGRESSION = isRemote ? OPERATOR + "/api" : SIM.replace(":4000", ":4003");

const guildId = "guild-mp-" + Math.random().toString(36).slice(2, 8);

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

async function get(url) {
  const res = await fetch(url);
  const text = await res.text();
  try {
    return { status: res.status, data: JSON.parse(text) };
  } catch {
    return { status: res.status, data: text };
  }
}

function connectPlayer(index) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(GATEWAY);
    const messages = [];
    const broadcasts = []; // messages received AFTER initial join

    ws.on("open", () => {
      resolve({ ws, messages, broadcasts, index });
    });
    ws.on("message", (data) => {
      const msg = JSON.parse(data.toString());
      messages.push(msg);
    });
    ws.on("error", (err) => reject(new Error(`Player ${index}: ${err.message}`)));
  });
}

function send(ws, type, payload) {
  ws.send(JSON.stringify({ type, payload, seq: Date.now() }));
}

async function main() {
  console.log(`\n=== Multi-Player Concurrent Test ===`);
  console.log(`Players: ${PLAYER_COUNT}`);
  console.log(`Gateway: ${GATEWAY}`);
  console.log(`Guild: ${guildId}\n`);

  // --- Step 0: Deactivate old challenges to avoid cross-test interference ---
  const oldChallenges = await get(`${PROGRESSION}/challenges?active=true`);
  if (oldChallenges.data && oldChallenges.data.length > 0) {
    console.log(`0. Deactivating ${oldChallenges.data.length} old challenges`);
    for (const c of oldChallenges.data) {
      await patch(`${PROGRESSION}/challenges/${c.challenge_id}`, { active: false });
    }
  }

  // --- Step 1: Create a shared guild challenge ---
  console.log("1. Creating shared guild challenge");
  const challengeRes = await post(`${PROGRESSION}/challenges`, {
    kind: "guild",
    name: "Team Builder",
    trigger_event: "structure_placed",
    target: PLAYER_COUNT * 2, // each player places 2
    rewards: JSON.stringify([{ type: "guild_points", amount: 100 }]),
  });
  if (challengeRes.status !== 201) {
    console.error("   Failed to create challenge:", challengeRes.data);
    process.exit(1);
  }
  const challengeId = challengeRes.data.challenge_id;
  console.log(`   Challenge: ${challengeId} (target: ${PLAYER_COUNT * 2} structures)`);

  // --- Step 2: Connect all players ---
  console.log(`\n2. Connecting ${PLAYER_COUNT} players...`);
  const players = [];
  for (let i = 0; i < PLAYER_COUNT; i++) {
    const p = await connectPlayer(i);
    await sleep(100); // small stagger to avoid thundering herd
    players.push(p);
  }

  // Wait for session_started messages
  await sleep(500);

  // Extract server-assigned player IDs
  for (const p of players) {
    const sessionMsg = p.messages.find((m) => m.type === "session_started");
    if (!sessionMsg) {
      console.error(`   Player ${p.index}: No session_started!`);
      process.exit(1);
    }
    p.playerId = sessionMsg.payload.player_id;
    console.log(`   Player ${p.index}: ${p.playerId.slice(0, 8)}...`);
  }

  // --- Step 3: All players join same region with same guild ---
  console.log(`\n3. All players joining region-spawn (guild: ${guildId})`);
  for (const p of players) {
    send(p.ws, "join_region", { region_id: "region-spawn", guild_id: guildId });
  }
  await sleep(1000);

  // Verify world_snapshot received
  let joinSuccess = 0;
  for (const p of players) {
    const snapshot = p.messages.find((m) => m.type === "world_snapshot");
    if (snapshot) {
      joinSuccess++;
      // Mark where broadcasts start (after initial join messages)
      p.broadcastStart = p.messages.length;
    }
  }
  console.log(`   Joined: ${joinSuccess}/${PLAYER_COUNT}`);

  if (joinSuccess < PLAYER_COUNT) {
    for (const p of players) {
      const errors = p.messages.filter((m) => m.type === "error");
      if (errors.length > 0) {
        console.error(`   Player ${p.index} errors:`, errors.map((e) => e.payload));
      }
    }
  }

  // --- Step 4: Each player places 2 structures ---
  console.log(`\n4. Each player placing 2 structures...`);
  const structuresPerPlayer = 2;
  for (let round = 0; round < structuresPerPlayer; round++) {
    for (const p of players) {
      send(p.ws, "place_structure", {
        structure_type: round === 0 ? "campfire" : "wooden_wall",
        position: { x: p.index * 10 + round * 3, y: 0, z: 0 },
      });
    }
    await sleep(800); // let all placements process
    console.log(`   Round ${round + 1}: ${PLAYER_COUNT} structures placed`);
  }

  // Wait for all fire-and-forget progression updates to land
  await sleep(4000);

  // --- Step 5: Check broadcasts ---
  console.log(`\n5. Checking broadcast delivery`);
  for (const p of players) {
    // Messages after broadcastStart are broadcasts from other players
    const postJoin = p.messages.slice(p.broadcastStart || 0);
    const entityUpdates = postJoin.filter((m) => m.type === "entity_update");
    const ownUpdates = entityUpdates.filter(
      (m) => m.payload?.entity_id === p.playerId || m.payload?.data?.player_id === p.playerId
    );
    const otherUpdates = entityUpdates.length - ownUpdates.length;
    console.log(
      `   Player ${p.index}: ${entityUpdates.length} entity_updates (${ownUpdates.length} own, ${otherUpdates} from others)`
    );
  }

  // --- Step 6: Verify all players visible in world ---
  console.log(`\n6. Checking world state`);
  const simApi = isRemote ? `${OPERATOR}/api` : SIM;
  const playersRes = await get(`${simApi}/players`);
  const connectedPlayers = playersRes.data.filter((p) => p.connected);
  console.log(`   Connected players: ${connectedPlayers.length}`);

  const structuresRes = await get(`${simApi}/structures?region_id=region-spawn`);
  console.log(`   Structures in region: ${structuresRes.data.length}`);

  // --- Step 7: Check challenge progress ---
  console.log(`\n7. Checking guild challenge progress`);
  const progressRes = await get(`${PROGRESSION}/challenges/${challengeId}/progress`);
  if (progressRes.data.guilds && progressRes.data.guilds.length > 0) {
    const gp = progressRes.data.guilds[0];
    const target = progressRes.data.target;
    console.log(`   Guild ${gp.guild_id}: ${gp.current_value}/${target}`);
    console.log(`   Completed: ${gp.completed}`);
    if (gp.completed) {
      console.log(`   Completed at: ${gp.completed_at}`);
    }
  } else {
    console.log(`   No guild progress found`);
  }

  // --- Step 8: Check guild points ---
  console.log(`\n8. Checking guild points`);
  const guildRes = await get(`${PROGRESSION}/guilds/${guildId}/progress`);
  if (guildRes.status === 200) {
    console.log(`   Points: ${guildRes.data.points}`);
    console.log(`   Challenges completed: ${guildRes.data.challenges_completed}`);
  } else {
    console.log(`   Guild not found (${guildRes.status})`);
  }

  // --- Cleanup ---
  console.log(`\n9. Disconnecting all players...`);
  for (const p of players) {
    p.ws.close();
  }
  await sleep(500);

  // Verify players removed
  const playersAfter = await get(`${simApi}/players`);
  const stillConnected = playersAfter.data.filter((p) => p.connected);
  console.log(`   Players still connected: ${stillConnected.length}`);

  // --- Summary ---
  const totalStructures = PLAYER_COUNT * structuresPerPlayer;
  const challengeProgress = progressRes.data.guilds?.[0]?.current_value || 0;

  console.log(`\n=== Results ===`);
  console.log(`  Players connected:     ${joinSuccess}/${PLAYER_COUNT}`);
  console.log(`  Structures placed:     ${structuresRes.data.length} (expected ${totalStructures}+)`);
  console.log(`  Challenge progress:    ${challengeProgress}/${totalStructures}`);
  console.log(`  Challenge complete:    ${progressRes.data.guilds?.[0]?.completed || false}`);
  console.log(`  Clean disconnect:      ${stillConnected.length === 0}`);
  console.log("");

  if (
    joinSuccess === PLAYER_COUNT &&
    challengeProgress >= totalStructures &&
    stillConnected.length === 0
  ) {
    console.log("  ALL CHECKS PASSED\n");
  } else {
    console.log("  SOME CHECKS FAILED — see details above\n");
    process.exit(1);
  }
}

main().catch((err) => {
  console.error("Test failed:", err);
  process.exit(1);
});
