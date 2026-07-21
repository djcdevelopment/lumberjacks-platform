# ADR 0003: Multi-Lane Transport Strategy

## Status

Accepted

## Context

The platform needs a real-time transport strategy between game clients and the gateway service. ADR 0001 established that the client is a thin shell and the server owns truth, which means the transport must support:

- low-latency bidirectional communication for movement, placement, and world updates
- explicit separation between authoritative state and disposable transient state
- compatibility with multiple client engines, including Godot and future replacements
- graceful degradation on weak links as a minimum playable requirement
- a migration path toward QUIC-based transport without forcing the first playable slice to depend on a single browser API

The platform's interest management and activation tier systems will generate variable message rates per region, and hotspot zones will produce bursty traffic under stress. The transport decision must preserve fairness while allowing degraded quality when the network or region is overloaded.

## Decision

Adopt a dual-lane transport model from day one:

1. A reliable, ordered lane for authoritative and user-visible state.
2. An unreliable, best-effort datagram lane for time-sensitive state that can be dropped or superseded.

The protocol layer must remain transport-agnostic. Message classes are defined by delivery semantics, not by a single network API.

For the first playable slice:

- WebSocket over TCP is the default implementation for the reliable lane.
- The envelope format in `packages/protocol` must not become coupled to WebSocket-specific assumptions.
- UDP or QUIC-backed datagram transport may be introduced behind the same message classes when the client and server path is ready.

WebTransport over HTTP/3 remains the intended long-term browser-facing transport because it supports both reliable streams and unreliable datagrams over a single secure connection. It is not required as the only transport for the first playable slice.

## Transport Classes

### Reliable lane

Use for:

- authentication and session bootstrap
- chat and operator-visible actions
- inventory changes
- crafting results
- progression updates
- structure placement confirmation
- authoritative corrections and rollback instructions

Requirements:

- ordered delivery
- retry semantics
- auditability
- strong observability

### Datagram lane

Use for:

- movement inputs
- aim and facing updates
- transient physics hints
- proximity and relevance updates
- rapidly superseded world-state deltas

Requirements:

- low latency
- no assumption of arrival
- no assumption of order
- messages must be idempotent, supersedable, or safely discardable

## Consequences

Positive:
- supports a dialup-style minimum viable network target
- keeps protocol design honest about delivery guarantees
- avoids a rewrite from everything being a reliable socket message later
- creates a direct migration path to QUIC or WebTransport without forcing it before the stack is ready
- lets the platform degrade quality before it degrades authority

Negative:
- requires message taxonomy discipline early
- requires two delivery semantics in client and server code
- increases test coverage needs for loss, reordering, and fallback behavior
- adds more design pressure to classify messages correctly before implementation sprawls

## Upgrade Path

When density or packet-loss conditions measurably degrade player experience, the transport can evolve without changing the message contract:

1. **WebTransport** — HTTP/3-based, supports reliable streams plus unreliable datagrams for browser-facing clients.
2. **UDP or QUIC-native transport** — for native clients and server-hosted datagram lanes.
3. **Hybrid transport** — reliable control and commitment traffic on WebSocket or streams, transient traffic on datagrams.

The protocol contract in `packages/protocol` is expected to survive this transition. A new ADR should be written before introducing a production transport beyond the initial WebSocket path.

## Alternatives Considered

- **WebSocket only**: Simple, but collapses authoritative and transient traffic into one ordered stream and bakes TCP head-of-line blocking into the design.
- **WebTransport only**: Strong long-term fit, but ecosystem maturity and client support are not reliable enough to make it the sole requirement for the first playable slice.
- **Raw UDP only**: Better for some game traffic, but loses easy browser compatibility and makes the reliable lane more expensive to rebuild.
- **gRPC streaming**: Good for service-to-service traffic, but not a strong fit for client transport semantics at this stage.

## Follow-Up Work

- classify all protocol messages by reliable vs datagram delivery semantics
- define sequencing, acknowledgement, and correction rules at the envelope layer
- benchmark WebSocket reliability behavior under constrained-network and hotspot scenarios
- evaluate HTTP/3 and QUIC readiness once the first client slice is running
