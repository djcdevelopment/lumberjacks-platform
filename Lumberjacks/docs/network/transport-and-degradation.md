# Transport and Degradation

## Logical lanes

Every message has delivery semantics before it has a socket:

- **Reliable** — session state, authoritative mutations, snapshots, events, errors.
- **Datagram** — transient input and entity state that a newer update supersedes.

Unknown message types fail safe to the reliable lane.

## Session and UDP binding

1. The client opens a WebSocket.
2. The gateway creates or resumes a session.
3. `session_started` supplies an eight-byte UDP token and UDP port.
4. A UDP-capable client sends a packet prefixed with that token.
5. The gateway associates the observed UDP endpoint with the WebSocket session.
6. Datagram-lane entity updates may now use UDP.

The token associates transports; it is not described here as an authentication
substitute.

## Downstream selection

For each visible player update, `TickBroadcaster` selects:

```text
binary session + bound UDP endpoint
    -> UDP binary update
    -> if unavailable/fails: binary WebSocket update

binary session without UDP
    -> binary WebSocket update

JSON session
    -> JSON WebSocket update
```

This preserves a working authoritative path when a cloud load balancer, firewall,
NAT, or client implementation cannot use UDP.

## Current client reality

The Node load-test client exercises UDP binding. Nature 2.0 currently sends binary
input and receives binary updates over WebSocket; `SimulationClient.cs` contains no
UDP binding path. Its traffic is therefore the designed fallback, not proof that the
Godot client uses both physical channels.

## Operational evidence

The dual-channel load report records:

- local clients binding UDP and receiving transient updates there;
- Azure blocking UDP ingress in the tested configuration;
- automatic binary WebSocket fallback without code changes or disconnects.

This validates degradation behavior. It does not prove UDP availability on every
deployment target.

## Evidence

- `src/Game.Gateway/WebSocket/SessionManager.cs`
- `src/Game.Gateway/WebSocket/UdpTransport.cs`
- `src/Game.Gateway/WebSocket/TickBroadcaster.cs`
- `src/Game.Gateway/WebSocket/GameWebSocketMiddleware.cs`
- `scripts/load-test-dual-channel.js`
- [ADR 0013](../adrs/0013-dual-channel-udp-transport.md)
- [Load-test results](../load-test-dual-channel-results.md)
