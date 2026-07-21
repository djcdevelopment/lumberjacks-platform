# Research Telemetry v0

## Purpose

Define a pull-based, opt-in telemetry system that allows clients and edge nodes to capture detailed diagnostic data locally, which can later be retrieved by trusted research or operator nodes for analysis.

This system exists to answer one core question:

> What actually happened in the system from the player’s perspective, and how does that correlate with authoritative behavior?

It complements (but does not replace) the authoritative event model.

- Authoritative events = truth
- Research telemetry = evidence

## Core Principles

1. Opt-in, not always-on
2. Pull-based, not push-heavy
3. Bounded capture, never unbounded logging
4. Correlatable with authoritative events
5. Never a source of truth
6. Safe for volunteer and community-operated nodes
7. Useful for real incident reconstruction, not vanity metrics

This aligns with platform principles around server authority, explainability, and edge assistance without ownership of truth.

## System Overview

Research telemetry consists of four primary components:

1. Capture Profile
2. Capture Window
3. Capture Bundle
4. Research Pull

### High-level flow

1. User or node enables `/detailedlogging`
2. A capture profile is activated locally
3. Data is recorded into a bounded local bundle
4. Capture window closes (time, size, or manual)
5. Bundle is stored locally or regionally
6. A trusted research node requests specific bundles
7. Bundles are pulled and correlated with authoritative events

No continuous global ingestion is required.

## Terminology

capture_profile
- Defines what data is collected and at what rate.

capture_window
- A bounded period during which telemetry is recorded.

capture_bundle
- A compressed, structured artifact containing captured telemetry and metadata.

research_pull_request
- A request from a trusted system to retrieve specific bundles.

session_id
- Unique identifier for a player or node session.

region_id
- Logical region identifier used for correlation.

correlation_id
- Shared identifier used to align telemetry with authoritative events and system activity.

## Activation Model

Telemetry can be activated via:

- client command: `/detailedlogging on`
- edge node command: `/detailedlogging on`
- operator-issued token or test trigger
- region-scoped activation (future)

### Required constraints

Every activation must define:

- start time
- max duration OR max size
- capture profile
- optional correlation_id

### Default limits (v0)

- duration: max 15 minutes
- bundle size: max 50–100 MB (configurable)
- sampling: reduced unless explicitly elevated

No unbounded capture is allowed.



## Capture Profiles

Profiles define what is recorded.

### v0 Profiles

#### 1. Minimal

- session metadata
- region transitions
- latency (avg, p95)
- disconnect/reconnect events

Low overhead, safe for broad use.

#### 2. Standard

- all minimal data
- packet timing / jitter
- reconciliation corrections
- snapshot sizes and frequency
- visible entity counts

Default for most diagnostics.

#### 3. Deep

- all standard data
- high-frequency timing samples
- detailed transport events
- cache hits/misses (edge)
- relay path diagnostics
- client frame timing (coarse)

Short-duration only.

---

Profiles must be versioned and validated.

## Capture Window

Defines the lifecycle of a telemetry session.

### Required fields

- capture_id
- session_id
- region_id (if applicable)
- start_timestamp
- end_timestamp
- capture_profile
- software_version
- node_type (client, relay, cache, gateway)

### Termination conditions

A capture window ends when:

- duration limit reached
- size limit reached
- user disables logging
- operator ends test
- node shuts down

Incomplete windows must still produce a valid partial bundle.
## Capture Bundle

The unit of storage and transfer.

### Structure

capture_bundle
- bundle_id
- capture_id
- node_id
- node_type
- session_id
- region_id
- start_timestamp
- end_timestamp
- profile
- software_version
- correlation_id (optional)
- data_segments
- integrity_hash

### Data segments (v0)

- transport_metrics
- latency_samples
- reconciliation_events
- region_activity
- edge_metrics (if node)
- client_performance (if client)

### Requirements

- compressed (e.g. zstd or gzip)
- append-only within window
- immutable after closure
- versioned schema

Bundles must be self-describing.
## Correlation Model

Telemetry is only useful if it can be aligned with authoritative systems.

### Required correlation keys

- session_id
- region_id
- timestamps (bounded window)
- correlation_id (if part of a test or event)

### Alignment targets

Bundles should be correlatable with:

- canonical events (event log)
- region tick summaries
- gateway metrics
- edge node metrics
- progression outcomes

Example:

- event: `guild_objective_completed`
- bundle: shows latency spike + reconciliation burst before completion
- insight: performance degradation did not affect outcome correctness

Telemetry explains experience, not truth.

## Research Pull Model

Data is retrieved on demand.

### research_pull_request

Fields:

- request_id
- requester_id
- authorization_scope
- target:
  - session_ids OR
  - region_id OR
  - correlation_id OR
  - time window
- bundle_filters (profile, node_type, version)
- purpose (optional, human-readable)

### Authorization

Only trusted systems may pull:

- operator API
- internal research nodes
- approved analysis jobs

Future:
- community-approved research roles (read-only, anonymized)

### Pull behavior

- bundles are transferred on request
- no continuous streaming
- rate-limited
- auditable

All pulls must be logged.

## Storage Model

v0 assumes local-first storage.

### Locations

- client: local disk
- edge node: local or attached storage
- optional: regional cache node

### Retention

- default: 24–72 hours
- configurable by node operator
- auto-deletion after expiration

### Optional future

- opt-in upload to shared research pool
- anonymized datasets for system-wide analysis

## Privacy and Trust

Telemetry must be safe to opt into.

### Requirements

- explicit opt-in
- visible active state (UI indicator)
- bounded capture
- no sensitive personal data
- no raw input capture (keystrokes, chat content)
- no inventory or private state beyond identifiers

### Transparency

Users and node operators must know:

- what is collected
- when it is collected
- how long it is stored
- who can request it

### Trust rule

Telemetry improves the system.
It must not expose or exploit the player.

## Fairness Constraints

Telemetry must not influence gameplay outcomes.

Specifically:

- no telemetry data may alter:
  - combat resolution
  - progression evaluation
  - reward granting
  - inventory state

- enabling `/detailedlogging` must not:
  - improve player performance
  - change matchmaking or region priority
  - grant advantages

Allowed effects:

- improved diagnostics
- better system tuning over time
- improved stability in future sessions
## Operator Requirements

Operators must be able to:

- view active capture sessions
- view capture density by region
- trigger bounded diagnostic windows
- request bundles for a specific incident
- correlate telemetry with:
  - events
  - progression changes
  - node health
- identify:
  - latency clusters
  - relay failures
  - cache staleness
  - region overload

v0 requirement:

One “incident view”:
- time window
- region
- key metrics
- linked bundles
## v0 Success Criteria

The system is considered successful when:

1. A client can enable `/detailedlogging` and produce a valid bundle
2. An edge node can do the same
3. Bundles can be pulled by a trusted research node
4. Bundles can be correlated with:
   - region
   - session
   - time window
5. A real incident (e.g. lag spike) can be reconstructed using:
   - client bundle
   - edge bundle
   - server metrics
6. Insights can be generated without modifying authoritative systems

## Implementation Guidance (v0)

Build in this order:

1. Capture Profile + Window (client only)
2. Bundle format + local storage
3. `/detailedlogging` command
4. Basic pull endpoint (operator API)
5. Edge node capture support
6. Correlation tooling (simple timeline view)

Do not build:

- full analytics pipelines
- real-time dashboards
- cross-region aggregation systems

Focus on:
- correctness
- bounded capture
- correlation viability
## Open Questions

- Should bundles be encrypted at rest by default?
- What is the minimum viable schema for cross-version compatibility?
- How should partial or corrupted bundles be handled?
- Should correlation_id be auto-generated for operator-triggered tests?
- What is the acceptable clock drift tolerance for correlation?
- How do we safely expose anonymized research datasets later?

## Summary

Research Telemetry v0 introduces:

- opt-in detailed logging
- bounded local capture
- pull-based retrieval
- cross-layer correlation

It enables the platform to:

- diagnose real player experience
- validate edge node effectiveness
- identify scaling inefficiencies
- improve stability without compromising authority

Telemetry is not truth.

Telemetry is evidence that helps explain truth.