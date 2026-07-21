# ADR 0013: Dual-Channel UDP Transport

**Status:** Accepted
**Date:** 2026-03-27
**Depends on:** ADR 0003 (Multi-Lane Transport Strategy)

## Context

ADR 0003 established the concept of a dual-lane transport model: a "Reliable" lane for authoritative/state-mutating events, and a "Datagram" lane for transient, supersedable state (like movement hints). While ADR 0003 successfully decoupled delivery semantics from the protocol layout, the platform initially transmitted both lanes over a single TCP-backed WebSocket connection.

To truly decouple "Cosmetic/Transient" traffic from "Game-Critical" traffic and avoid TCP head-of-line blocking during network congestion, the architecture required a true datagram transport implementation as identified in the ADR 0003 upgrade path.

## Decision

Implement and deploy a dedicated UDP datagram transport channel alongside the existing WebSocket reliable channel.

Specifically:
- The WebSocket layer (port 4000) will continue to host the Reliable lane.
- A new `UdpTransport` background service (port 4005) is introduced to carry Datagram lane messages (`entity_update`, `player_input`).
- Each WebSocket `session_started` handshake now securely provisions a crypto-random 8-byte `udp_token`. Clients use this token as a prefix on all outbound UDP packets to bind the stateless UDP stream to the authenticated WebSocket session without expensive per-packet cryptography.
- `TickBroadcaster` selectively routs `Datagram` categorized messages to the UDP interface, while falling back to the WebSocket if UDP is unavailable or the client hasn't bound yet.

## Consequences

Positive:
- **Head-of-Line Blocking Eliminated:** Dropped physics update packets no longer stall subsequent, fresher physics updates. 
- **Graceful Degradation:** Meets the core architectural thesis of remaining playable heavily relying on the deterministic core via the reliable TCP pipe if UDP traffic is restricted or highly lossy.
- **Seamless Upgrade:** Due to the fallback system in `TickBroadcaster`, backwards compatibility with existing standalone WebSocket clients is preserved.

Negative:
- **NAT / Firewall Issues:** Pure UDP connections face stricter NAT traversal and firewall blocking rules than WebSocket (HTTP edge-proxied) traffic.
- **Multi-Port Exposure:** Deployments (such as Azure Container Apps) now require exposing both the HTTP/WS load balancer port and a dedicated UDP port.
- **E2E Testing Complexity:** Requires test frameworks and clients capable of coordinating asynchronous hybrid-transport assertions.
