# Vertical Slice Intent

## Purpose

This project is not starting by building a mod.

It is starting by building the smallest playable and observable vertical slice that can prove the underlying platform architecture.

The point of the first slice is not content breadth.
The point is not feature parity with an existing survival game.
The point is not to recreate a polished gameplay experience.

The point is to prove that the foundational infrastructure can support a future community-scale game built for persistence, extensibility, explainability, and regional scalability from the start.

## What this means

The first vertical slice must exist to validate the platform, not to imitate the source game.

It should answer:

- Can authoritative state be owned cleanly by trusted services?
- Can a player action produce durable world change?
- Can that action emit canonical events?
- Can progression consume those events deterministically?
- Can the result be explained in operator tooling?
- Can the same slice later benefit from region partitioning, relevance, and edge-assisted delivery without changing its core rules?

If the slice proves those things, it is successful even if the visuals are rough, the content is minimal, and the world is small.

## Non-goal

The first slice is not intended to be:

- a Valheim mod
- a feature clone of an existing survival game
- a full content demo
- a polished art showcase
- a combat-complete game prototype
- a general sandbox with unbounded mechanics

Any work that does not help prove the platform’s authority model, event model, progression path, persistence, or operator visibility should be treated as secondary.

## Core framing

Build the simplest vertical slice that demonstrates:

- server authority
- persistent shared state
- event-driven progression
- explainable operator tooling
- future compatibility with relevance partitioning and edge assistance

Everything else is optional until that loop is proven.

## Why this matters

Many projects accidentally optimize the first playable slice for appearance or familiarity.

That would be the wrong optimization here.

This platform exists because existing community-hosted survival experiences hit architectural ceilings:

- fragile server authority
- limited scalability
- manual progression tracking
- unclear event contracts
- weak operator visibility
- brittle mod extension paths

The first slice must therefore be chosen to expose and validate these hard problems early, not to hide them behind content or polish.

## Definition of success

The first vertical slice is successful when it proves the infrastructure underneath it is real.

That means:

1. a player can connect to a shared region
2. the player can perform a meaningful world action
3. the action changes authoritative state
4. that change persists across restart
5. canonical events are emitted from the action
6. progression rules can consume the event stream
7. the result is visible to players and operators
8. operators can answer why the state changed
9. the slice can later be stress-tested under density, relevance, and edge-assisted delivery without being redesigned

## Practical rule

When evaluating any early feature, ask:

Does this help prove the platform?

If yes, it belongs in the first slice.
If no, it should be deferred, simplified, or cut.

## Slice selection rule

Choose the first gameplay domain not because it is flashy, but because it touches the platform end to end.

A good first slice is one that naturally exercises:

- player input
- authoritative simulation
- persistence
- event emission
- progression updates
- shared world consequence
- operator explanation

This is why an early domain like logging, building, roads, or shared settlement contribution may be a better first slice than combat breadth or biome complexity.

## Final statement

The first vertical slice is a proof of platform architecture disguised as a small playable experience.

It is not a mod.
It is not a clone.
It is not the full game.

It is the smallest end-to-end system that can prove the scalable foundation on which the real game will be built.