/**
 * Test: Session resume after disconnect
 *
 * Flow:
 * 1. Connect, join region, place a structure
 * 2. Disconnect
 * 3. Reconnect with resume_token
 * 4. Verify same player_id, receive world_snapshot with the structure
 */

const WebSocket = require("ws");

const GATEWAY = process.argv[2] || "ws://localhost:4000";

async function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function connect(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    const messages = [];
    ws.on("open", () => resolve({ ws, messages }));
    ws.on("message", (data) => messages.push(JSON.parse(data.toString())));
    ws.on("error", reject);
  });
}

function send(ws, type, payload) {
  ws.send(JSON.stringify({ type, payload, seq: Date.now() }));
}

async function main() {
  console.log(`\n=== Session Resume Test ===`);
  console.log(`Gateway: ${GATEWAY}\n`);

  // --- Step 1: Connect and join ---
  console.log("1. First connection");
  const conn1 = await connect(GATEWAY);
  await sleep(300);

  const session1 = conn1.messages.find((m) => m.type === "session_started");
  const playerId = session1.payload.player_id;
  const resumeToken = session1.payload.resume_token;
  console.log(`   Player: ${playerId.slice(0, 8)}...`);
  console.log(`   Resume token: ${resumeToken.slice(0, 8)}...`);
  console.log(`   Resumed: ${session1.payload.resumed}`);

  // Join region and place a structure
  send(conn1.ws, "join_region", { region_id: "region-spawn" });
  await sleep(500);

  send(conn1.ws, "place_structure", {
    structure_type: "test_beacon",
    position: { x: 99, y: 0, z: 99 },
  });
  await sleep(500);

  const structureMsg = conn1.messages.find(
    (m) => m.type === "entity_update" && m.payload?.entity_type === "structure"
  );
  const structureId = structureMsg?.payload?.entity_id;
  console.log(`   Placed structure: ${structureId?.slice(0, 8)}...`);

  // --- Step 2: Disconnect ---
  console.log("\n2. Disconnecting...");
  conn1.ws.close();
  await sleep(500);
  console.log("   Disconnected");

  // --- Step 3: Reconnect with resume token ---
  console.log("\n3. Reconnecting with resume token");
  const resumeUrl = `${GATEWAY}?resume=${resumeToken}`;
  const conn2 = await connect(resumeUrl);
  await sleep(500);

  const session2 = conn2.messages.find((m) => m.type === "session_started");
  const playerId2 = session2.payload.player_id;
  const resumeToken2 = session2.payload.resume_token;
  console.log(`   Player: ${playerId2.slice(0, 8)}...`);
  console.log(`   Resumed: ${session2.payload.resumed}`);
  console.log(`   Same player: ${playerId === playerId2}`);
  console.log(`   New resume token: ${resumeToken2.slice(0, 8)}...`);

  // Should receive world_snapshot with the structure
  const snapshot = conn2.messages.find((m) => m.type === "world_snapshot");
  if (snapshot) {
    const entities = snapshot.payload.entities || [];
    const structures = entities.filter((e) => e.entity_type === "structure");
    console.log(`   World snapshot: ${entities.length} entities, ${structures.length} structures`);
    const beacon = structures.find(
      (s) => s.type === "test_beacon" || s.entity_id === structureId
    );
    console.log(`   Our beacon found: ${beacon ? "yes" : "no"}`);
  } else {
    console.log("   No world_snapshot received");
  }

  // --- Step 4: Try resume with OLD token (should fail gracefully) ---
  console.log("\n4. Trying old resume token (should get new session)");
  conn2.ws.close();
  await sleep(300);

  const conn3 = await connect(`${GATEWAY}?resume=${resumeToken}`);
  await sleep(300);

  const session3 = conn3.messages.find((m) => m.type === "session_started");
  console.log(`   Player: ${session3.payload.player_id.slice(0, 8)}...`);
  console.log(`   Resumed: ${session3.payload.resumed}`);
  console.log(`   Different player (as expected): ${session3.payload.player_id !== playerId}`);

  conn3.ws.close();
  await sleep(200);

  // --- Results ---
  const passed =
    playerId === playerId2 &&
    session2.payload.resumed === true &&
    session3.payload.resumed === false &&
    session3.payload.player_id !== playerId;

  console.log(`\n=== ${passed ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED"} ===\n`);
  if (!passed) process.exit(1);
}

main().catch((err) => {
  console.error("Test failed:", err);
  process.exit(1);
});
