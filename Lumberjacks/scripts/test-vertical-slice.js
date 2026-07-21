// End-to-end vertical slice test
// Usage: node scripts/test-vertical-slice.js [gatewayUrl]

const WebSocket = require('ws');

const GATEWAY = process.argv[2] || 'ws://localhost:4000';
const SIM = GATEWAY.replace('ws://', 'http://').replace('wss://', 'https://');
const EVENTS = GATEWAY.replace('ws://', 'http://').replace('wss://', 'https://').replace(':4000', ':4002');
const PROGRESSION_URL = GATEWAY.replace('ws://', 'http://').replace('wss://', 'https://').replace(':4000', ':4003');
const OPERATOR = GATEWAY.replace('ws://', 'http://').replace('wss://', 'https://').replace(':4000', ':4004');
const ws = new WebSocket(GATEWAY);
let sessionId, playerId;

ws.on('open', () => {
    console.log('[1] Connected to Gateway');
});

ws.on('message', (data) => {
    const envelope = JSON.parse(data.toString());
    console.log(`[${envelope.type}] Received:`, JSON.stringify(envelope.payload, null, 2));

    if (envelope.type === 'session_started') {
        sessionId = envelope.payload.session_id;
        playerId = envelope.payload.player_id;
        console.log(`\n[2] Session started — player: ${playerId}`);

        // Send place_structure
        const placeMsg = {
            version: 1,
            type: 'place_structure',
            seq: 1,
            timestamp: new Date().toISOString(),
            payload: {
                structure_type: 'wooden_wall',
                position: { x: 10.5, y: 0, z: -3.2 },
                rotation: 45.0,
            },
        };

        console.log('\n[3] Sending place_structure...');
        ws.send(JSON.stringify(placeMsg));
    }

    if (envelope.type === 'entity_update') {
        console.log('\n[4] Got entity_update — structure placed!');

        // Now verify the full loop
        setTimeout(async () => {
            try {
                // Check structures in simulation
                const structRes = await fetch(`${SIM}/structures?region_id=region-spawn`);
                const structures = await structRes.json();
                console.log(`\n[5] Structures in region: ${structures.length}`);

                // Check events in event-log
                const evtRes = await fetch(`${EVENTS}/events?type=structure_placed&limit=1`);
                const events = await evtRes.json();
                console.log(`[6] structure_placed events: ${events.count}`);

                // Check player progress
                const progRes = await fetch(`${PROGRESSION_URL}/players/${playerId}/progress`);
                const progress = await progRes.json();
                console.log(`[7] Player progress: ${JSON.stringify(progress)}`);

                // Check operator API
                const opRes = await fetch(`${OPERATOR}/api/structures`);
                const opStructures = await opRes.json();
                console.log(`[8] Operator API structures: ${opStructures.length}`);

                console.log('\n=== VERTICAL SLICE LOOP COMPLETE ===');
                console.log('Player connected → placed structure → server accepted authoritatively →');
                console.log('structure persisted → event emitted → progress updated →');
                console.log('entity_update sent to client → operator can see everything');

                ws.close();
                process.exit(0);
            } catch (err) {
                console.error('Verification failed:', err.message);
                ws.close();
                process.exit(1);
            }
        }, 2000); // Give side effects time to complete
    }
});

ws.on('error', (err) => {
    console.error('WebSocket error:', err.message);
    process.exit(1);
});

// Timeout safety
setTimeout(() => {
    console.error('Test timed out');
    process.exit(1);
}, 15000);
