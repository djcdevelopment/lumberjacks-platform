# 90-Day Roadmap

This roadmap assumes a small founding team with role-based ownership:

- Product/Technical Lead: scope, architecture decisions, final acceptance
- Backend Lead: simulation, state, APIs
- Netcode/Realtime Lead: transport, relevance, replication
- Client Lead: thin client, prediction, rendering integration
- Tools Lead: admin UI, content authoring, Discord bridge
- Infra Lead: local dev stack, CI, observability, deployment
- Content Systems Lead: events, quests, ranks, sample content

One person may hold multiple roles early. Ownership still needs to be explicit.

## Phase 1: Weeks 1-4

Objective:
Lock scope, define contracts, and stand up the development skeleton.

Success criteria:
- The team agrees on the product brief and architecture principles.
- The monorepo exists with working local bootstrapping.
- The first event contracts and service boundaries are written.

Milestones:

### Week 1: Founding decisions

Owners:
- Product/Technical Lead
- Backend Lead
- Tools Lead

Deliverables:
- Product brief
- Architecture principles
- MVP scope
- ADR for engine strategy and thin-client rule

Visible evidence:
- Docs merged to `main`
- Kickoff board with prioritized backlog

### Week 2: Monorepo skeleton

Owners:
- Infra Lead
- Backend Lead
- Client Lead

Deliverables:
- Monorepo layout created
- Local bootstrap scripts
- Shared schema package
- Stub services for simulation, event log, progression, admin API, Discord bridge

Visible evidence:
- `./start-all` boots placeholder services
- CI runs lint and unit tests

### Week 3: Event and content contracts

Owners:
- Content Systems Lead
- Backend Lead
- Tools Lead

Deliverables:
- Initial event catalog
- Progression DSL v0
- Sample rank ladder
- Sample guild challenge definitions

Visible evidence:
- Example content validates against schemas
- Docs explain how one live community workflow maps to events

### Week 4: First vertical backbone

Owners:
- Backend Lead
- Netcode/Realtime Lead
- Client Lead

Deliverables:
- Player session lifecycle
- Region join/leave flow
- Snapshot and delta envelope format
- Basic client connection showing remote presence

Visible evidence:
- Two clients can connect to a local environment and move in one test region

## Phase 2: Weeks 5-8

Objective:
Prove the core platform loop: authoritative state, relevance management, event-driven progression, and admin visibility.

Success criteria:
- The server owns truth for movement, placement, and inventory basics.
- The event log produces progression updates.
- The admin panel can explain progress changes.

Milestones:

### Week 5: Region and relevance system

Owners:
- Netcode/Realtime Lead
- Backend Lead

Deliverables:
- Region partition model
- Interest subscription graph
- Activation tiers for entities and regions
- Debug view for subscriptions and load

Visible evidence:
- Bot clients crossing boundaries trigger deterministic subscription changes

### Week 6: Build and persistence loop

Owners:
- Backend Lead
- Client Lead

Deliverables:
- Structure placement flow
- Persistent world object storage
- Settlement proxy generation seed data
- Inventory pickup and item state basics

Visible evidence:
- A placed structure survives restart and appears to a reconnecting client

### Week 7: Event log to progression path

Owners:
- Content Systems Lead
- Backend Lead
- Tools Lead

Deliverables:
- Durable event log
- Progression worker consuming events
- Rank and guild challenge updates
- Audit trail for why a player or guild changed state

Visible evidence:
- Admin UI shows a completed action, emitted event, and resulting reward path

### Week 8: Discord and operator loop

Owners:
- Tools Lead
- Infra Lead

Deliverables:
- Discord identity linking
- Role sync prototype
- Community event announcement flow
- Operator dashboard with environment health

Visible evidence:
- A test event updates Discord and the admin panel from the same source of truth

## Phase 3: Weeks 9-12

Objective:
Prove the platform feels like a real community game instead of a backend demo.

Success criteria:
- Travel is readable and safer by design.
- Distant settlements have visible presence.
- Combat-adjacent regions perform better under density because of relevance and partitioning.
- A trusted-alpha group can build, travel, earn progress, and create admin-defined events.

Milestones:

### Week 9: Roads and safety corridor prototype

Owners:
- Backend Lead
- Client Lead
- Content Systems Lead

Deliverables:
- Road corridor data model
- Safe travel warning logic
- Spawn bias rules near maintained paths
- Map presentation for roads and landmarks

Visible evidence:
- Test players can follow a readable route with reduced surprise danger

### Week 10: Distant art and settlement LOD

Owners:
- Client Lead
- Netcode/Realtime Lead
- Backend Lead

Deliverables:
- Settlement bounding volumes
- Landmark signatures
- Distant outline or silhouette rendering
- Progressive detail loading based on distance and relevance

Visible evidence:
- A remote settlement is visible as presence before full detail loads

### Week 11: Community edge node alpha

Owners:
- Infra Lead
- Netcode/Realtime Lead

Deliverables:
- Relay or cache node role
- Registration and health checks
- Basic failover
- Metrics for assisted regions

Visible evidence:
- A non-authoritative community node improves fanout or cache hit rate without owning combat truth

### Week 12: Trusted alpha weekend

Owners:
- Product/Technical Lead
- All leads

Deliverables:
- Alpha checklist
- Test scenarios
- Metrics report
- Postmortem and next-quarter plan

Visible evidence:
- A real group can connect, travel, build, complete a challenge, and generate usable operator feedback

## First Deliverables To Create Immediately

These should exist before code volume increases:

- `docs/product-brief.md`
- `docs/architecture-principles.md`
- `docs/mvp-scope.md`
- `docs/events.md`
- `docs/progression-dsl.md`
- `docs/adrs/0001-thin-client-platform.md`
- Monorepo bootstrap scripts
- A backlog labeled by phase and owner

## Suggested Metrics For The First 90 Days

Technical:
- region tick time p50 and p95
- snapshot size p50 and p95
- reconciliation corrections per minute
- event processing lag
- world save and restore correctness
- relay/cache node availability

Community:
- admin minutes required to launch an event
- time to add a new rank or challenge
- number of custom definitions shipped without code changes
- alpha player retention across two sessions
- bugs found by trusted modders versus internal team
