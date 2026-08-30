/**
 * End-to-end smoke for the first native Lumberjacks loop.
 *
 * It connects through the real /game admission boundary, joins region-spawn,
 * walks to the authored Storm Pine, and performs eight cooldown-spaced swings.
 * Credentials are accepted only through environment variables and never printed.
 * A direct local connection uses the Gateway's existing private-plane policy.
 *
 * Usage:
 *   node scripts/test-native-field-loop.js [ws://localhost:4000/game]
 *
 * Optional public-run environment:
 *   LUMBERJACKS_TEST_ENROLLMENT_ID
 *   LUMBERJACKS_TEST_CLIENT_KEY
 *   LUMBERJACKS_TEST_NATIVE_RELEASE (defaults to 0.1.0-alpha.1)
 */

const WebSocket = require("ws");

const gateway = process.argv[2] || "ws://localhost:4000/game";
const expectPersisted = process.argv.includes("--expect-felled");
const release = process.env.LUMBERJACKS_TEST_NATIVE_RELEASE || "0.1.0-alpha.1";
const enrollment = process.env.LUMBERJACKS_TEST_ENROLLMENT_ID || "local-field-smoke";
const clientKey = process.env.LUMBERJACKS_TEST_CLIENT_KEY || "local-field-smoke";
const messages = [];
let sequence = 0;

const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

function send(socket, type, payload) {
  socket.send(JSON.stringify({
    version: 1,
    type,
    seq: ++sequence,
    timestamp: new Date().toISOString(),
    payload,
  }));
}

function waitFor(predicate, label, timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const timer = setInterval(() => {
      const match = messages.find(predicate);
      if (match) {
        clearInterval(timer);
        resolve(match);
      } else if (Date.now() - started >= timeoutMs) {
        clearInterval(timer);
        reject(new Error(`Timed out waiting for ${label}`));
      }
    }, 20);
  });
}

function connect() {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(gateway, {
      headers: {
        "X-Lumberjacks-Enrollment-Id": enrollment,
        "X-Lumberjacks-Client-Key": clientKey,
        "X-Lumberjacks-Native-Release": release,
      },
    });
    socket.on("message", (data, isBinary) => {
      if (!isBinary) messages.push(JSON.parse(data.toString()));
    });
    socket.once("open", () => resolve(socket));
    socket.once("error", reject);
    socket.once("unexpected-response", (_, response) =>
      reject(new Error(`Native admission returned HTTP ${response.statusCode}`)));
  });
}

async function main() {
  const socket = await connect();
  try {
    const started = await waitFor(
      (message) => message.type === "session_started",
      "session_started");
    if (started.payload.native_client_release !== release)
      throw new Error("Gateway did not bind the exact native release");

    send(socket, "join_region", { region_id: "region-spawn" });
    const snapshot = await waitFor(
      (message) => message.type === "world_snapshot",
      "world_snapshot");
    const featured = snapshot.payload.entities.find(
      (entity) => entity.entity_id === "northwoods-old-pine");
    if (!featured) throw new Error("Authored Storm Pine is absent from the snapshot");
    if (featured.growth_history?.name !== "The Storm Pine")
      throw new Error("Authored field-note metadata is absent from the snapshot");

    if (expectPersisted) {
      if (featured.health !== 0 || featured.growth_history?.strike_count !== "8" ||
          typeof featured.growth_history?.fall_heading !== "string")
        throw new Error("Restarted snapshot did not retain the terminal tree state");
      process.stdout.write(JSON.stringify({
        verdict: "native_field_persistence_passed",
        gateway: new URL(gateway).host,
        release,
        tree_id: featured.entity_id,
        strikes: Number(featured.growth_history.strike_count),
        terminal_health: featured.health,
        fall_heading: featured.growth_history.fall_heading,
      }) + "\n");
      return;
    }

    // Spawn is (0, 0, 0); seven northward inputs place the player inside the
    // server's 2.5-unit XZ axe range around the tree at (0, 0, 5).
    for (let index = 0; index < 7; index++) {
      send(socket, "player_input", {
        direction: 0,
        speed_percent: 100,
        action_flags: 0,
        input_seq: sequence + 1,
      });
      await sleep(55);
    }
    send(socket, "player_input", {
      direction: 0,
      speed_percent: 0,
      action_flags: 0,
      input_seq: sequence + 1,
    });
    await sleep(150);

    for (let strike = 0; strike < 8; strike++) {
      send(socket, "player_input", {
        direction: 0,
        speed_percent: 0,
        action_flags: 4,
        input_seq: sequence + 1,
      });
      await sleep(80);
      send(socket, "player_input", {
        direction: 0,
        speed_percent: 0,
        action_flags: 0,
        input_seq: sequence + 1,
      });
      await sleep(520);
    }

    const felled = await waitFor(
      (message) => message.type === "entity_update" &&
        message.payload?.entity_id === "northwoods-old-pine" &&
        message.payload?.data?.health === 0,
      "terminal Storm Pine update");
    const history = felled.payload.data.growth_history;
    if (history?.strike_count !== "8")
      throw new Error(`Expected 8 persisted axe marks, got ${history?.strike_count}`);
    if (typeof history?.fall_heading !== "string")
      throw new Error("Terminal update has no server-authored fall heading");

    process.stdout.write(JSON.stringify({
      verdict: "native_field_loop_passed",
      gateway: new URL(gateway).host,
      release,
      player_id_prefix: started.payload.player_id.slice(0, 4),
      tree_id: featured.entity_id,
      strikes: Number(history.strike_count),
      terminal_health: felled.payload.data.health,
      fall_heading: history.fall_heading,
    }) + "\n");
  } finally {
    socket.close();
  }
}

main().catch((error) => {
  console.error(`native field loop failed: ${error.message}`);
  process.exitCode = 1;
});
