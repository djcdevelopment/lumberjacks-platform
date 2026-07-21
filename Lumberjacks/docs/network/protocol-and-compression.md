# Protocol and Compression

## Design goal

High-frequency input and spatial state must fit a constrained link without forcing
every message into an opaque binary format. Lumberjacks therefore compresses the hot
path and retains JSON where flexibility and readability are worth more than bytes.

## Envelope

`BinaryEnvelope` uses a six-byte padded header:

| Field | Bits | Purpose |
|---|---:|---|
| Version | 4 | Protocol evolution |
| Message type | 6 | Up to 64 compact message IDs |
| Delivery lane | 1 | Reliable or datagram semantics |
| Sequence | 16 | Ordered/acknowledged message support |
| Payload length | 16 | Up to 65,535 payload bytes |
| Padding | 5 | Byte alignment |

The header describes message semantics but does not require a particular socket.

## Hot-path payloads

### Player input

| Field | Size |
|---|---:|
| Direction | 1 byte |
| Speed percentage | 1 byte |
| Action flags | 1 byte |
| Input sequence | 2 bytes |
| **Payload total** | **5 bytes** |

With the binary envelope, a WebSocket message body is 11 bytes before WebSocket
framing. UDP adds the eight-byte session token before the binary frame.

### Entity update

Entity updates contain a variable-length entity ID, two six-byte compact vectors,
a packed heading, the last input sequence, tick, and state hash. The fixed fields use
24 bytes, followed by the length-prefixed UTF-8 entity ID. An eight-character ASCII
ID therefore produces a 33-byte payload, a 39-byte binary frame, or a 47-byte UDP
packet after the session token. The `PayloadSerializers` comment claiming a typical
19-byte payload is stale and should not be used for budgeting.

New measurements must always say whether a number means payload, envelope plus
payload, or complete transport packet.

### Compact vectors

`CompactVec3` stores each coordinate as a signed 16-bit fixed-point value: six bytes
instead of three 64-bit doubles (24 bytes). The conversion defines range and
precision explicitly rather than relying on general-purpose floating-point JSON.

## Compatibility paths

- Binary hot-path messages use `MessageTypeId` and `PayloadSerializers`.
- JSON envelopes remain available for lower-frequency and compatibility traffic.
- Binary-mode sessions may still travel over WebSocket when UDP is unavailable.
- Unknown logical message types default to the reliable lane.

## Compression is a boundary, not a shortcut

The wire representation should be derived from the minimum state required by a
consumer. It should not become the domain model itself. The tree lab demonstrates
this separation: a rich polar simulation remains local while a compact projection
contains only the values needed to reproduce visible felling state.

Before adding a compact payload, record:

1. the rich source model;
2. the consumer-visible projection;
3. precision and range loss;
4. payload, framed, and transport sizes;
5. round-trip tests;
6. fallback behavior.
