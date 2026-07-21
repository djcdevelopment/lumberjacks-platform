# ADR 0001: Thin Client, Platform-Owned Authority

## Status

Accepted

## Context

The project aims to support long-lived community survival worlds with:

- 100+ player ambitions
- Discord-native community operations
- event-driven progression
- heavy extension and plugin needs
- possible future engine portability

An engine-led architecture would push too much game truth into client or scene logic and would make scale, explainability, and mod governance harder.

## Decision

The platform will treat the game engine as a thin client shell.

Trusted backend services will own:
- world state
- progression state
- guild state
- persistent inventories
- authoritative placement and combat-sensitive logic
- event log truth

The client will own:
- rendering
- input
- local prediction
- interpolation
- UX and moment-to-moment feedback

## Consequences

Positive:
- clearer trust boundaries
- better operator visibility
- easier Discord and admin integration
- better chance of future engine replacement
- progression and content rules can change without client rewrites

Negative:
- more backend work up front
- stricter protocol design needed early
- client engineers must respect authority boundaries

## Follow-Up Decisions Needed

- transport and replication strategy
- event log implementation
- plugin sandbox model
- persistence model for world regions
