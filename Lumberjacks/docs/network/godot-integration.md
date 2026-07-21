# Godot Integration

## Role

Nature 2.0 is a thin C# Godot client built over the network contracts. It captures
input, renders authoritative state, interpolates remote motion, maps coordinate
systems, and hosts isolated design labs. It does not own the server tick or canonical
world truth.

## Shared contracts

`clients/godot-cs/nature-2.0/nature 2.0.csproj` references `Game.Contracts`.
`Game.Contracts` targets both .NET 9 and .NET 8 so the server and Godot can share:

- message names and compact IDs;
- binary envelope parsing;
- payload serializers;
- entity records and vector definitions.

This reduces duplicated protocol implementations, though compatibility still needs
tests whenever a payload changes.

## Client data flow

```text
Godot input at 20 Hz
    -> PayloadSerializers.WritePlayerInput
    -> BinaryEnvelope
    -> binary WebSocket
    -> Gateway / InputQueue / SimulationStep
    -> binary or JSON entity update
    -> SimulationClient receive queue
    -> CoordinateMapper
    -> GameState signals
    -> scene rendering and interpolation
```

`SimulationClient` accepts JSON and binary entity updates. `PlayerController` emits
direction, speed, actions, and a monotonically increasing sequence.

## Current limits

- Nature 2.0 does not currently bind the optional UDP channel.
- It sends binary input over WebSocket and receives the broadcaster's fallback path.
- Its binary input envelope currently marks the lane as `Reliable`, while the shared
  message classification defines player input as `Datagram`; the type still routes,
  but the lane metadata should be aligned.
- Input sequence data is exposed, but full local prediction/replay/reconciliation is
  not implemented.
- Natural-resource binary updates are not implemented in `TickBroadcaster`; changed
  resources use the JSON path.
- The 24-byte compact tree projection remains inside the lab code rather than the
  shared protocol.

These are integration stages, not architectural failures. Naming them prevents a
validated lab or server capability from being mistaken for an end-to-end feature.

## Labs versus the live client

The lab scenes reuse Godot as an inspection and tuning surface, but their pure C#
models are intentionally separable from rendering. `TerrainSim` can run in batch or
on a server. `TreeFellingSim` has no Godot dependency. Network promotion happens only
after a stable projection and serializer are defined.

See [Research to lab method](../labs/research-to-lab-method.md).

## Evidence

- `clients/godot-cs/nature-2.0/scripts/Networking/SimulationClient.cs`
- `clients/godot-cs/nature-2.0/scripts/Player/PlayerController.cs`
- `clients/godot-cs/nature-2.0/scripts/Core/GameState.cs`
- `clients/godot-cs/nature-2.0/scripts/Core/CoordinateMapper.cs`
- [ADR 0006](../adrs/0006-godot-game-client.md)
- [Godot migration retrospective](../retrospective-godot-cs-migration-2026-03-29.md)
