# Edge Node Responsibilities v0

## Purpose

Define the first safe contract for community-provided or third-party regional infrastructure in the platform.

This document exists to make one thing explicit:

Edge nodes may improve delivery, fanout, caching, and regional responsiveness, but they do not own authoritative game truth.

That boundary preserves fairness while allowing communities to contribute hardware that improves play quality in specific locations or during dense events. This aligns with the platform principles that server authority owns truth, the client is thin, interest management is core infrastructure, and community edge nodes may assist but not own combat, inventory, or progression truth.
## Why this matters

The source game model assumes a smaller, more centralized world host. Our platform direction is different:

- persistent shared regions
- event-driven progression
- operator-visible explanations
- community-scale survival play
- optional regional assistance from volunteer or purchased infrastructure

The point of edge infrastructure is not to grant power. The point is to improve stability, responsiveness, and observability around a region without changing outcomes. That fits the product goal of scaling through relevance and partitioning rather than brute force, and supports the roadmap’s early “community edge node alpha” concept.
## Non-negotiable rule

Edge contribution affects delivery quality, not gameplay authority.

An edge node may help a region feel better to play in.
It may not make a player stronger, richer, faster to progress, or harder to fight.

## Authority split

### Authoritative core services

The following remain on trusted server infrastructure only:

- combat resolution
- inventory truth
- building ownership truth
- progression truth
- guild state truth
- reward granting
- canonical event acceptance
- persistence writes for authoritative world state

This follows the architecture rule that all meaningful state must exist without the client running and must remain inspectable from backend tools.
### Edge-assisted responsibilities

The following may be performed by edge nodes under explicit contracts:

- connection relay
- regional message fanout
- short-lived snapshot cache
- interest subscription assistance
- read-only regional query acceleration
- event stream mirroring for analytics or indexing
- health reporting
- regional load hints back to operators

## v0 edge node types

v0 should support only two node roles.

### 1. Relay Node

Purpose:
Reduce network friction between nearby players and the trusted gateway/simulation path.

Allowed responsibilities:
- terminate or proxy player network sessions for a region
- multiplex websocket or transport traffic
- reduce duplicate outbound fanout
- maintain transient connection state
- report latency, drop rate, and subscriber counts

Not allowed:
- mutate authoritative state
- generate progression events as truth
- resolve combat or placement outcomes
- accept inventory mutations as final

### 2. Cache Node

Purpose:
Improve read performance and regional visibility without becoming the source of truth.

Allowed responsibilities:
- hold recent region snapshots
- hold settlement proxy data
- hold landmark signatures
- serve read-only replicated state to nearby clients
- cache recent event views for operator dashboards
- precompute region-local read models

Not allowed:
- create or finalize new world truth
- issue rewards
- decide progression outcomes
- accept write operations as committed

## Explicitly excluded from v0

The following are intentionally out of scope for the first edge design:

- combat delegation
- inventory delegation
- progression worker execution on community nodes
- community-owned persistence writes
- market/economy settlement on edge nodes
- arbitrary plugin execution on volunteer hardware
- peer-to-peer authority election
- “best hardware wins” simulation priority

This is consistent with the current focus on planning, first contracts, and first vertical slice rather than advanced community infrastructure.
## Trust model

Edge nodes are semi-trusted infrastructure participants.

They may be:
- volunteer-operated
- community-purchased
- hosted by a third party chosen by the community
- platform-managed in later phases

They are trusted to improve transport and caching.
They are not trusted to define truth.

### Trust assumptions

Assume an edge node can:
- go offline unexpectedly
- serve stale cached data
- misreport health
- lag behind the authoritative region
- be misconfigured
- act maliciously

Therefore:
- all authoritative writes must be validated and committed upstream
- all cacheable data must carry version and freshness metadata
- all node-issued metrics must be treated as advisory until corroborated
- edge loss must degrade quality, not correctness

## Core invariants

1. Losing an edge node cannot roll back authoritative world state.
2. Losing an edge node cannot grant or remove rewards.
3. A player connected through an edge node must receive the same authoritative outcome as a player connected directly.
4. Edge-assisted play may reduce latency or improve stability, but must not improve progression rate or combat result.
5. Operators must be able to tell whether a region was edge-assisted and whether the node was healthy.
6. A stale cache may delay visibility, but cannot become accepted truth.

## v0 message responsibilities

This section does not define exact schema yet; it defines ownership.

### Edge node may receive

- region subscription updates
- recent read-only snapshots
- settlement proxy bundles
- landmark signature updates
- transport session instructions
- health/configuration updates

### Edge node may emit

- health heartbeat
- latency metrics
- subscriber counts
- cache freshness reports
- relay capacity status
- read-only diagnostics
- forwarding acknowledgements

### Edge node may not emit as source of truth

- reward_granted
- guild_objective_completed
- player_rank_changed
- inventory committed
- combat resolved
- structure_placed committed
- discord_role_synced as authoritative result

Those outcomes belong to the authoritative event and progression path. The platform already centers on canonical events and explainable progression flows from trusted services.
## Operational model

### Registration

An edge node must register with:
- node id
- node role
- region affinity or preferred coverage
- software version
- declared capacity
- public network endpoint
- health check endpoint
- operator ownership metadata

### Health

Minimum health signals:
- last heartbeat time
- relay latency percentile
- packet drop rate
- active subscribers
- cache freshness lag
- upstream connectivity
- version compatibility
- current role status

### Detach behavior

If an edge node becomes unhealthy:
- new assignments stop
- clients may reconnect via gateway or alternate route
- cached reads fall back to authoritative sources
- operator UI marks the node degraded
- no authoritative correction is needed because no truth was owned there

This lines up with the event catalog’s edge node operational events.
## Operator requirements

The operator surface must answer:

- which regions are edge-assisted right now
- which node is serving each region
- whether that node is relay, cache, or both
- node health over time
- whether a degraded player experience correlates with node health
- whether fallback occurred
- whether cached state freshness exceeded threshold

This follows the broader product promise that operators should be able to explain why progress changed and inspect the environment clearly.
## Fairness requirements

To protect trust:

- edge assignment must not prioritize only wealthy or privileged players
- node presence must not increase reward rates
- node presence must not reduce danger calculations or combat difficulty
- node presence must not alter authoritative tick outcome
- node sponsorship may improve stability in a location, but never confer mechanical advantage

Permitted effect:
- lower disconnect rate
- lower fanout pressure
- smoother regional updates
- faster read-side visibility

Forbidden effect:
- stronger character
- safer combat by rule
- faster progression by rule
- exclusive authoritative privileges

## v0 success criteria

Edge Node Responsibilities v0 is proven when:

1. a relay node can assist one region without owning state
2. a cache node can serve recent read-only regional views
3. player-visible outcomes remain identical with or without the edge node
4. operator UI can show node health and fallback behavior
5. a node failure causes degraded quality only, not incorrect world state
6. moderate local load shows improved transport or cache behavior under test

This matches the roadmap’s early edge-node proof while keeping authority, events, and admin explainability central.
## MVP implementation guidance

Do not build “distributed simulation” first.
Build these in order:

### Step 1
Relay-only node for one test region.

### Step 2
Add read-only snapshot cache for that region.

### Step 3
Expose node health and fallback in operator UI.

### Step 4
Run a comparison:
- direct path only
- relay-assisted path
- relay + cache path

Measure:
- connection stability
- fanout pressure
- snapshot delivery lag
- cache freshness
- player disconnect rate
- operator visibility completeness

## Open questions

These are intentionally deferred, not ignored:

- Should edge nodes be assigned manually, automatically, or by region policy?
- How are volunteer nodes authenticated and approved?
- What freshness budget is acceptable for cached regional reads?
- Should one node be allowed both relay and cache roles in v0?
- What regional handoff behavior is required when players cross boundaries rapidly?
- How should settlement proxies be versioned for cache distribution?
- What anti-abuse controls are required before public volunteer onboarding?

## ADR trigger

A formal ADR is required before:
- any edge role is allowed to influence authoritative write timing
- any plugin code can run on community hardware
- any community node participates in combat, inventory, or progression execution
- any edge election or region ownership transfer is introduced

## Summary

v0 edge nodes are not mini-servers.
They are regional accelerators.

They improve:
- transport
- caching
- fanout
- observability

They do not own:
- truth
- rewards
- combat
- progression
- persistence

That boundary is the whole point.