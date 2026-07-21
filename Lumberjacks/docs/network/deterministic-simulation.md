# Deterministic Server Simulation

## Contract

Clients submit intent; the server owns position, velocity, world mutation, tick
order, and the state hash. This is the architectural base beneath every client.

```text
PlayerController / test bot
       -> PlayerInputMessage(direction, speed, actions, sequence)
       -> InputQueue.Enqueue(current tick)
       -> InputQueue.DrainForTick(next tick)
       -> SimulationStep
       -> WorldState
       -> StateHasher
       -> TickBroadcaster
```

## Fixed tick

`TickLoop` runs every 50 milliseconds, or 20 Hz, using `PeriodicTimer`. Each tick:

1. increments the authoritative tick;
2. drains inputs scheduled for that tick;
3. applies simulation rules;
4. records changed players and resources;
5. computes the world state hash;
6. broadcasts the changed state.

The rendering frame rate has no authority over this loop.

## Input scheduling

`InputQueue` is the thread boundary between asynchronous gateway I/O and the single
simulation loop. Inputs are scheduled for a future tick. When several inputs target
the same player and tick, the sequence number establishes which input is newest.

The five-byte input contains enough information to reproduce motion without trusting
a client-supplied position.

## Reconciliation support

Each entity update carries:

- the authoritative simulation tick;
- the last processed input sequence;
- a state hash.

Those values make prediction and reconciliation possible, but the Nature 2.0 client
currently interpolates authoritative results rather than running a full local replay
and correction loop. The protocol is prediction-ready; the client feature is not
complete.

## Consolidation

The simulation began as a separate service boundary and was later hosted in the
Gateway process to remove a high-frequency HTTP hop. `Game.Simulation` remains a
separate project and dependency boundary, but the live tick path is in-process.

## Evidence

- `src/Game.Simulation/Tick/InputQueue.cs`
- `src/Game.Simulation/Tick/SimulationStep.cs`
- `src/Game.Simulation/Tick/TickLoop.cs`
- `src/Game.Simulation/Tick/StateHasher.cs`
- `tests/Game.Simulation.Tests/`
- [ADR 0014](../adrs/0014-input-driven-deterministic-simulation.md)
