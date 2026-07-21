# Progression DSL v0

The DSL should let communities define ranks, quests, challenges, and rewards without writing engine code.

## Design Rules

- human-readable
- versioned
- validated before activation
- based on canonical events
- auditable after execution

## Example Shape

```yaml
kind: guild_challenge
id: road-builders-weekend
version: 1
name: Road Builders Weekend
window:
  start: 2026-04-10T18:00:00Z
  end: 2026-04-13T06:00:00Z
scope:
  guild: any
trigger:
  event: structure_placed
  filters:
    tags:
      category: road
progress:
  mode: sum
  value: 1
completion:
  target: 200
rewards:
  - type: guild_points
    amount: 100
  - type: discord_role
    role_id: road-builder
```

## Core Concepts

`kind`
- `rank_rule`
- `guild_challenge`
- `seasonal_objective`
- `achievement`

`trigger`
- canonical event name
- optional filters by region, guild, actor, tags, or payload fields

`progress`
- how an event increments or evaluates state

`completion`
- target threshold or boolean expression

`rewards`
- items
- points
- titles
- permissions
- Discord role changes

## Validation Rules

- referenced events must exist in `docs/events.md`
- filters must reference known schema fields
- rewards must target registered reward handlers
- time windows must be valid
- definitions must be explainable in plain language

## Explainability Requirement

For every completed rule, the system should be able to answer:
- which event instances counted
- which filters matched
- which threshold was crossed
- which rewards were emitted
