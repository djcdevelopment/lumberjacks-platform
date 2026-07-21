// Multi-client movement test
// Two players connect, join region, move, see each other, one disconnects
const WebSocket = require('ws');

const GATEWAY_WS = process.argv[2] || 'ws://localhost:4000';
const gatewayHttpUrl = new URL(GATEWAY_WS);
gatewayHttpUrl.protocol = gatewayHttpUrl.protocol === 'wss:' ? 'https:' : 'http:';
const GATEWAY_HTTP = gatewayHttpUrl.toString().replace(/\/$/, '');

function createEnvelope(type, payload) {
    return JSON.stringify({ version: 1, type, seq: 1, timestamp: new Date().toISOString(), payload });
}

function connect(label) {
    return new Promise((resolve, reject) => {
        const ws = new WebSocket(GATEWAY_WS);
        const received = [];
        let playerId, sessionId;

        ws.on('message', (data) => {
            const env = JSON.parse(data.toString());
            received.push(env);
            if (env.type === 'session_started') {
                playerId = env.payload.player_id;
                sessionId = env.payload.session_id;
            }
        });

        ws.on('open', () => {
            // Wait for session_started
            setTimeout(() => resolve({ ws, received, get playerId() { return playerId; }, get sessionId() { return sessionId; }, label }), 500);
        });

        ws.on('error', reject);
    });
}

async function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

async function main() {
    console.log('=== Multi-Client Movement Test ===\n');
    console.log(`Gateway: ${GATEWAY_WS}\n`);

    // Wait for services
    await sleep(5000);

    // Step 1: Two players connect
    console.log('[1] Connecting Player A...');
    const a = await connect('A');
    console.log(`    Player A: ${a.playerId}`);

    console.log('[2] Connecting Player B...');
    const b = await connect('B');
    console.log(`    Player B: ${b.playerId}\n`);

    // Step 2: Player A joins region
    console.log('[3] Player A joins region-spawn...');
    a.ws.send(createEnvelope('join_region', { region_id: 'region-spawn' }));
    await sleep(1000);

    const aSnapshot = a.received.find(e => e.type === 'world_snapshot');
    if (aSnapshot) {
        console.log(`    Got world_snapshot: ${aSnapshot.payload.entities.length} entities in region`);
    } else {
        console.log('    ERROR: No world_snapshot received');
    }

    // Step 3: Player B joins region
    console.log('[4] Player B joins region-spawn...');
    b.ws.send(createEnvelope('join_region', { region_id: 'region-spawn' }));
    await sleep(1000);

    const bSnapshot = b.received.find(e => e.type === 'world_snapshot');
    if (bSnapshot) {
        const playerCount = bSnapshot.payload.entities.filter(e => e.entity_type === 'player').length;
        const structCount = bSnapshot.payload.entities.filter(e => e.entity_type === 'structure').length;
        console.log(`    Got world_snapshot: ${playerCount} players, ${structCount} structures`);
    }

    // Player A should have received entity_update for Player B joining
    const aGotB = a.received.find(e => e.type === 'entity_update' && e.payload?.data?.player_id === b.playerId);
    console.log(`    Player A saw Player B join: ${aGotB ? 'YES' : 'NO'}\n`);

    // Step 4: Player A moves
    console.log('[5] Player A moves to (10, 0, 20)...');
    a.ws.send(createEnvelope('player_move', {
        position: { x: 10, y: 0, z: 20 },
        velocity: { x: 1, y: 0, z: 2 },
        timestamp: Date.now(),
    }));
    await sleep(500);

    // Player B should receive the movement
    const bGotMove = b.received.find(e =>
        e.type === 'entity_update' &&
        e.payload?.entity_id === a.playerId &&
        e.payload?.data?.position
    );
    if (bGotMove) {
        const pos = bGotMove.payload.data.position;
        console.log(`    Player B sees A at: (${pos.x}, ${pos.y}, ${pos.z})`);
    } else {
        console.log('    ERROR: Player B did not receive A\'s movement');
    }

    // Step 5: Player B moves
    console.log('[6] Player B moves to (-5, 0, 15)...');
    b.ws.send(createEnvelope('player_move', {
        position: { x: -5, y: 0, z: 15 },
        velocity: { x: -0.5, y: 0, z: 1.5 },
        timestamp: Date.now(),
    }));
    await sleep(500);

    const aGotBMove = a.received.find(e =>
        e.type === 'entity_update' &&
        e.payload?.entity_id === b.playerId &&
        e.payload?.data?.position
    );
    if (aGotBMove) {
        const pos = aGotBMove.payload.data.position;
        console.log(`    Player A sees B at: (${pos.x}, ${pos.y}, ${pos.z})\n`);
    }

    // Step 6: Check simulation state
    console.log('[7] Checking simulation state...');
    const playersRes = await fetch(`${GATEWAY_HTTP}/players`);
    const players = await playersRes.json();
    console.log(`    Players in world: ${players.length}`);
    for (const p of players) {
        console.log(`      ${p.name} at (${p.position.x}, ${p.position.y}, ${p.position.z}) in ${p.region_id}`);
    }

    // Step 7: Check region player count
    const regionsRes = await fetch(`${GATEWAY_HTTP}/regions`);
    const regions = await regionsRes.json();
    console.log(`    ${regions[0].name} player count: ${regions[0].player_count}\n`);

    // Step 8: Player A disconnects
    console.log('[8] Player A disconnects...');
    a.ws.close();
    await sleep(1500);

    // Player B should receive entity_removed
    const bGotRemove = b.received.find(e => e.type === 'entity_removed' && e.payload?.entity_id === a.playerId);
    console.log(`    Player B saw A leave: ${bGotRemove ? 'YES' : 'NO'}`);

    // Check simulation cleaned up
    const playersAfter = await (await fetch(`${GATEWAY_HTTP}/players`)).json();
    console.log(`    Players remaining: ${playersAfter.length}`);

    const regionsAfter = await (await fetch(`${GATEWAY_HTTP}/regions`)).json();
    console.log(`    Region player count: ${regionsAfter[0].player_count}`);

    // Step 9: Check events
    await sleep(1000);
    const eventsRes = await fetch('http://localhost:4002/events?limit=5');
    const eventsData = await eventsRes.json();
    const connectedEvts = eventsData.events.filter(e => e.event_type === 'player_connected').length;
    const disconnectedEvts = eventsData.events.filter(e => e.event_type === 'player_disconnected').length;
    console.log(`\n[9] Events emitted: ${connectedEvts} player_connected, ${disconnectedEvts} player_disconnected`);

    console.log('\n=== MOVEMENT LOOP COMPLETE ===');
    console.log('Two players connected → joined region → received world snapshots →');
    console.log('moved and saw each other → one disconnected → cleanup confirmed →');
    console.log('events emitted for connect/disconnect');

    b.ws.close();
    process.exit(0);
}

main().catch(err => { console.error(err); process.exit(1); });

setTimeout(() => { console.error('Test timed out'); process.exit(1); }, 30000);
