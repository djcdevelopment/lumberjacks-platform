# ADR 0007: Canonical Event Schema as System of Record

**Status:** Accepted
**Date:** 2026-03-26
**Supersedes:** None

## Context

The platform is event-first by design (Architecture Principle #3). Every meaningful player action, world mutation, and operational change must be captured as a canonical event. These events serve multiple consumers: progression rules, operator audit trails, analytics, Discord integrations, and future replay/debugging systems.

We needed to decide: should events be loosely typed blobs, strongly typed per-event classes, or something in between?

## Decision

Events follow a **uniform envelope with typed string constants and flexible JSONB payloads**.

The canonical schema is:

```
event_id       UUID        (globally unique, idempotency key)
event_type     string      (from a closed set of 30 constants)
occurred_at    timestamp   (when it happened in the game world)
world_id       string      (which world instance)
region_id      string?     (spatial context)
actor_id       string?     (who caused it)
guild_id       string?     (guild context)
source_service string      (which service emitted it)
schema_version int         (for payload evolution)
payload        JSONB       (event-specific data)
```

Event types are **string constants, not language-level enums**. The 30 canonical types are defined in `Game.Contracts.Events.EventType` (C#) and documented in `docs/events.md`.

## Rationale

**String constants over enums.** The wire format uses strings like `"player_connected"`. String constants serialize naturally to JSON without custom converters. Adding a new event type is additive — no enum renumbering, no binary compatibility concerns, no migration.

**Uniform envelope.** Every event carries the same top-level fields. This means a single `events` table in PostgreSQL, a single serialization path, and a single query interface. Consumers filter by `event_type` rather than joining across specialized tables.

**Flexible payload with schema_version.** Event-specific data lives in the JSONB `payload` field. The `schema_version` field allows payload shape to evolve without breaking existing consumers. Version 1 payloads are documented in `docs/events.md`.

**Closed type set.** The 30 event types cover the MVP scope exhaustively. New types are added deliberately through documentation and code review, not ad-hoc. The `EventType.All` collection enables compile-time completeness checks.

## Consequences

- All services that emit events must use the canonical envelope format
- Progression rules consume events by `event_type` — the progression DSL (docs/progression-dsl.md) pattern-matches on these strings
- The PostgreSQL `events` table has indexes on `event_type`, `actor_id`, and `occurred_at` for efficient operator queries
- Adding a new event type requires: adding the constant to `EventType`, documenting the payload in `docs/events.md`, and updating `EventType.All`
- Event payloads are not validated at the schema level in the database — validation happens at the service boundary before insertion
