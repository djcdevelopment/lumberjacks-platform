# ADR 0008: Delivery Lane Classification for Protocol Messages

**Status:** Accepted
**Date:** 2026-03-26
**Depends on:** ADR 0003 (Multi-Lane Transport Strategy)

## Context

ADR 0003 established a dual-lane transport model: a reliable lane for authoritative state and a datagram lane for transient updates. Every protocol message must be classified into one of these lanes *before* implementation, enforcing transport discipline at the protocol layer rather than leaving it as a runtime decision.

The question was: how do we make this classification explicit, maintainable, and verifiable?

## Decision

Every protocol message type has a **static, compile-time classification** mapping it to either `DeliveryLane.Reliable` or `DeliveryLane.Datagram`.

This is implemented as:
- `enum DeliveryLane { Reliable, Datagram }` — the two lanes from ADR 0003
- `MessageClassification.GetLane(string messageType)` — static dictionary lookup
- Unknown message types default to `Reliable` (fail-safe)

### Current Classification

| Message | Lane | Rationale |
|---------|------|-----------|
| `join_region` | Reliable | Session state mutation |
| `leave_region` | Reliable | Session state mutation |
| `session_started` | Reliable | Authentication/handshake |
| `place_structure` | Reliable | Authoritative world mutation |
| `interact` | Reliable | Authoritative action |
| `world_snapshot` | Reliable | Full state transfer |
| `event_emitted` | Reliable | Canonical event delivery |
| `error` | Reliable | Must be received |
| `player_move` | Datagram | Transient, loss-tolerant |
| `entity_update` | Datagram | Transient, superseded by next update |
| `entity_removed` | Datagram | Transient spatial update |

## Rationale

**Taxonomy before implementation.** Classifying messages at definition time forces developers to think about delivery guarantees before writing handler code. This prevents the common MMO mistake of sending everything over TCP "because it's easier" and then discovering head-of-line blocking under load.

**Static map, not runtime inference.** A dictionary lookup is O(1), deterministic, and testable. We verify classification completeness in `Game.Contracts.Tests.MessageClassificationTests`.

**Default to Reliable.** An unclassified message type getting datagram delivery could silently drop authoritative state. Defaulting to reliable is the safe failure mode — worst case is unnecessary ordering overhead, not lost data.

**Separation of classification from transport.** The classification says *what guarantee a message needs*. The transport layer (currently WebSocket for both, future QUIC/UDP for datagram) decides *how to deliver it*. This lets us upgrade transport without reclassifying messages.

## Consequences

- Adding a new message type requires adding it to both `MessageType` constants and `MessageClassification` map
- Tests verify that all known message types have explicit classifications
- The Gateway service uses `MessageClassification.GetLane()` to route messages to the appropriate transport when the datagram lane is implemented
- Until UDP/QUIC is wired, both lanes flow over WebSocket — but the classification is already enforced so the upgrade is a transport change, not a protocol change
