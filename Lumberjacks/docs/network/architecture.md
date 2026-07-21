# Network Architecture

## Boundary map

```text
Godot / test clients
  input capture, rendering, interpolation
              |
              | JSON or binary WebSocket; optional UDP binding
              v
Game.Gateway
  session admission, protocol parsing, routing, UDP binding, tick broadcast
              |
              | in-process calls and shared contracts
              v
Game.Simulation
  input queue, fixed tick, physics, world state, hashes, interest filtering
              |
              v
Game.Contracts                     Game.Persistence
  message meaning, binary framing    durable world and progression state
```

`Game.Contracts` is multi-targeted for the .NET 9 server and .NET 8 Godot client.
The active C# Godot project references it directly, so both ends use the same binary
envelope, message IDs, payload serializers, and entity types.

## Authority boundary

The client sends direction, speed, action flags, and an input sequence. It does not
send an authoritative position. `InputQueue` schedules intent for a future tick;
`SimulationStep` computes motion; `TickLoop` hashes and publishes the resulting
state. The echoed input sequence provides the protocol support needed for later
client reconciliation without transferring authority to the client.

## Representation boundary

The logical message and its transport are separate decisions:

- `MessageClassification` assigns reliable or datagram semantics.
- `BinaryEnvelope` carries version, message type, lane, sequence, and payload length.
- `PayloadSerializers` compact the hot path.
- JSON remains available for compatibility and lower-frequency messages.

The lane bit says what a message can tolerate. The physical path—UDP, binary
WebSocket, or JSON WebSocket—is chosen later.

## Delivery boundary

WebSocket establishes the session and always provides a usable path. A session may
bind a UDP endpoint using its eight-byte token. For a binary session with a bound
endpoint, entity updates use UDP. If UDP is absent or a send fails, the broadcaster
uses binary WebSocket. JSON sessions receive JSON WebSocket updates.

This is progressive enhancement: loss of the fidelity path changes transport
quality, not ownership of state.

## Visibility boundary

`SpatialGrid` indexes world positions. `InterestManager` filters player updates for
each observer:

| Distance | Update cadence |
|---|---:|
| Near, 0–100 units | Every tick, 20 Hz |
| Mid, 100–300 units | Every fourth tick, 5 Hz |
| Far, over 300 units | No transient position update |

Reliable world mutations are not meant to disappear merely because a transient
position is outside the fidelity budget.

## Domain-mechanic boundary

Godot labs are deliberately allowed to hold richer state than the wire. The tree
felling lab, for example, uses a polar trunk model and exposes a six-float,
24-byte `CompactTreeState`. That projection demonstrates a network budget; it is
not yet a serializer in `Game.Contracts`, and natural-resource binary broadcasting
is explicitly left for later in `TickBroadcaster`.

That distinction—rich model versus transmitted projection—is central to the design.
