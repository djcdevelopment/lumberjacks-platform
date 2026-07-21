# LinkedIn Post Ideas

Short-form posts for LinkedIn. Each is standalone and targets a different angle/audience.

---

## Post 1: The Solo Founder Sprint

**Angle:** What's possible as a solo technical founder with AI tooling
**Audience:** Founders, indie devs, startup people

Two days ago I had a design doc and empty TypeScript files.

Today I have:
- 8 .NET microservices running a multiplayer game backend
- Binary protocol with 96% bandwidth reduction
- UDP + WebSocket dual-channel transport
- Spatial interest management with area-of-interest filtering
- 157 passing tests
- Deployed to Azure Container Apps
- 10-player multiplayer test passing against the live deployment
- A comprehensive deployment runbook

Total cost: ~$25/month on Azure. Total time: 32 hours.

I used Claude as a pair programmer — not for boilerplate, but for the full architectural build. It maintained consistency across 180 files, caught cross-cutting issues I would have missed, and wrote docs inline with the code instead of "later."

The most surprising thing wasn't the speed. It was what I was willing to attempt. Binary serialization, UDP transport, AND Azure deployment in the same sprint? Alone, I'd have scoped that as three separate milestones.

AI pair programming doesn't just make you faster. It changes what you think is worth trying.

---

## Post 2: Server-Authoritative Multiplayer

**Angle:** Technical deep dive on the architecture choice
**Audience:** Game developers, backend engineers

Hot take: your game client shouldn't decide where the player is.

I'm building a survival game where the server owns ALL state. The client is a thin rendering shell — it captures input, sends it to the server, and renders whatever the server says.

Why?

1. **Anti-cheat by design.** If the server computes all positions, the client can't teleport or speed hack. There's nothing to exploit.

2. **Community operators can trust the world.** This is built for community-run servers. Operators need to know the world state is authoritative and auditable.

3. **Engine portability.** The client is replaceable. We chose Godot, but the backend doesn't care. Swap the renderer, keep the game.

The trade-off is latency. When you press W, your character doesn't move until the server says so (~50ms round trip locally, ~100ms over the internet). For a building/survival game, that's fine. For an FPS, you'd need client-side prediction.

The implementation:
- Client sends `player_input` (5 bytes: direction, speed, action flags, sequence number)
- Server applies physics at 20Hz tick rate
- Server broadcasts `entity_update` (33 bytes per player, binary-packed)
- Area-of-interest filtering: near entities every tick, mid-range every 4th tick, far entities dropped

Core simulation bandwidth: <3.6 KB/s per client. That's dialup speeds, by design.

If you're building multiplayer and thinking "the client needs to be smart" — maybe reconsider. A dumb client with a smart server is easier to secure, easier to scale, and easier to replace.

---

## Post 3: The Documentation Shift

**Angle:** AI making docs happen in real-time instead of "later"
**Audience:** Engineering managers, tech leads, anyone who's said "we'll document that later"

Something weird happened during my last sprint: documentation kept pace with the code.

Not "I'll write the README after." Not "the code is the documentation." Actual architectural decision records, deployment runbooks, retrospectives, and a client implementation plan — all written within hours of the code they describe.

In 32 hours of building a multiplayer game backend, we produced:
- 2 new ADRs (binary serialization, UDP transport)
- A 500-line deployment runbook with PowerShell commands and gotcha tables
- A simulation audit scoring the implementation against design principles
- A 6-phase client implementation plan with acceptance tests per phase
- Updated README with prerequisites, build commands, and smoke tests

How? AI wrote the first drafts from the code it just helped write. I edited for accuracy and tone. The feedback loop was minutes, not days.

This matters more than it sounds. I can hand someone the deployment runbook and they can deploy to Azure without me. I can revisit the project in 3 months and know exactly what was built and why. The Godot client plan has acceptance tests so I know when each phase is done.

Documentation debt is a choice. With AI pair programming, the cost of writing docs dropped from "significant effort I'll do later" to "a few minutes of editing right now."

---

## Post 4: Bandwidth on a Budget

**Angle:** Specific technical achievement (binary protocol)
**Audience:** Multiplayer/networking engineers, performance-minded devs

We got our multiplayer game's per-client bandwidth down to 3.6 KB/s.

For context, that's dial-up speeds. 28.8 kbps. The constraint was intentional — we want this to work for players on bad connections.

How:

**JSON entity update: ~200 bytes**
```json
{"entity_id":"abc","entity_type":"player","data":{"position":{"x":1.5,"y":0,"z":3.2},"velocity":{"x":0.5,"y":0,"z":0.1},"heading":45.0},"tick":12345}
```

**Binary entity update: 33 bytes**
- 6-byte envelope header (version, type, lane, seq, length)
- 6 bytes for position (CompactVec3: 16-bit fixed-point X/Z, 16-bit Y)
- 6 bytes for velocity
- 1 byte for heading (0-255 mapped to 0-360 degrees)
- Entity ID, tick, state hash packed in remaining bytes

**84% reduction.**

Player input is even more dramatic: 120 bytes JSON → 5 bytes binary. 96% reduction.

The key insight: game state updates don't need JSON's flexibility. Positions are always 3 floats. Headings are always one angle. Input is always direction + speed + flags. When the schema is fixed, bit-packing wins.

Tools used:
- Custom BitWriter/BitReader with stackalloc (zero heap allocation)
- CompactVec3: 48-bit fixed-point covering 65 km squared at 1-unit precision
- CRC32 state hashing for desync detection

At 20Hz tick rate with 50 players in range, that's 50 * 33 bytes * 20 ticks = 33 KB/s. Well within any modern connection, and viable on 3G mobile.

---

## Post 5: The Azure Deploy (Short)

**Angle:** Quick win / practical deployment story
**Audience:** Broad — anyone deploying containers

Deployed 4 microservices to Azure Container Apps today. Cost: ~$25/month.

The stack:
- Gateway (WebSocket game server) — external ingress
- OperatorApi (admin endpoints) — external ingress
- EventLog + Progression — internal ingress only
- PostgreSQL Flexible Server (Burstable B1ms)

Things that bit me:
1. Docker `latest` tag + Azure = stale images. Use unique tags per deploy.
2. `--no-cache` on Docker build. Layer caching will silently serve old code.
3. Azure eastus was restricted for PostgreSQL. Switched to eastus2.
4. Internal-only services can't be tested from outside. Added proxy endpoints.

Things that worked great:
1. Scale-to-zero on idle services (EventLog, Progression cost ~$0 when nobody's playing)
2. WebSocket upgrade works through Container Apps HTTPS ingress with zero config
3. Internal DNS between containers Just Works

10-player multiplayer test passes against the live deployment. The backend is live and ready for the Godot client.
