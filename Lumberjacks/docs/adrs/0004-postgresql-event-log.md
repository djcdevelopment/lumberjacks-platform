# ADR 0004: PostgreSQL as Event Log and State Store

## Status

Accepted

## Context

The platform's architecture is event-first (ADR 0001, architecture principles). Every meaningful action emits a canonical event. The progression service consumes events to update ranks, guild goals, and challenges. Operators must be able to query the event trail to explain why any state change happened.

The event log needs to support:

- append-only durable writes from multiple services
- indexed queries by event type, actor, region, guild, and time range
- structured payloads with flexible fields per event type
- operator-facing audit queries (why did this player's rank change?)
- correlation with research telemetry bundles

World state (players, guilds, structures, regions) also needs durable persistence that survives restarts — this is a hard requirement from the architecture principles ("all meaningful state must exist without the client running").

## Decision

Use PostgreSQL as the primary data store for both the event log and authoritative world state.

- Events are stored in an append-only `events` table with JSONB payload columns
- World state (player progress, guild progress, structures) uses standard relational tables
- Each service manages its own tables but shares the same PostgreSQL instance in local dev
- Production can split into per-service databases when isolation is needed

## Consequences

Positive:
- single technology to learn, operate, and back up in early development
- JSONB gives schema flexibility for event payloads without losing query power
- rich indexing (B-tree, GIN on JSONB) supports the operator audit queries
- transactional guarantees for state mutations
- mature ecosystem for migrations, monitoring, and tooling
- easy local development (runs on Docker or native install)

Negative:
- not optimized for high-throughput append-only workloads the way Kafka or EventStoreDB would be
- single-node bottleneck under very high event rates (addressable with partitioning or write-ahead strategies later)
- mixing event log and state store in one system could create contention under load

## Upgrade Path

If event throughput exceeds what PostgreSQL can handle (likely well beyond MVP scale):

1. **Partitioned events table** — partition by time range (monthly) for write throughput and query efficiency
2. **Dedicated event store** — migrate the append-only event stream to Kafka, NATS JetStream, or EventStoreDB while keeping PostgreSQL for queryable state
3. **Read replicas** — separate operator query load from write path

The event schema in `packages/schemas` is store-agnostic. The `services/event-log` API is the only code that talks to PostgreSQL directly for events, so migration is isolated.

## Alternatives Considered

- **SQLite (local) + PostgreSQL (prod)**: Simpler local setup but introduces compatibility constraints and makes integration testing less representative.
- **MongoDB**: Flexible document model but weaker transactional guarantees for the state mutations that progression and guild systems need.
- **Kafka + PostgreSQL**: Better event throughput but adds operational complexity that is unjustified before the platform has real traffic. Can be adopted later without changing the event schema.
- **EventStoreDB**: Purpose-built for event sourcing but small ecosystem and adds a new operational dependency for marginal benefit at MVP scale.

## Follow-Up Work

- Define partition strategy for the events table before it exceeds ~10M rows
- Benchmark event write throughput under simulated 40-player density
- Evaluate whether progression should consume events via polling, pg_notify, or an intermediate message bus
