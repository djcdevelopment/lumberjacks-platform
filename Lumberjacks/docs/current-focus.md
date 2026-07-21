# Current Focus

This file describes the repository's current technical center and the latest recorded
work. It does not treat every open item in an older plan as active.

## Technical center: network infrastructure

The primary system is the server-authoritative network core:

- five-byte input intent processed by a fixed 20 Hz simulation;
- compact binary envelopes and spatial payloads;
- spatial interest tiers for per-observer update rates;
- UDP delivery with binary and JSON WebSocket fallbacks;
- shared contracts consumed by the C# Godot client;
- tests and load scripts covering contracts, simulation, gateway behavior, and
  degradation.

The canonical overview is [Network infrastructure](network/README.md), and capability
status is tracked in the [Network evidence index](network/evidence-index.md).

## Latest recorded implementation work: gateway extensions

The July 8–10 commits extend the gateway toward Valheim interoperability:

- priority-manifest planning and activation broadcast;
- datagram-lane priority delivery;
- UDP reset resilience;
- ZDO redirect receipt and counters;
- ZDO injection paths;
- handshake admission and loopback tests.

These are extensions of the gateway and delivery infrastructure. They do not replace
the underlying simulation, compression, or fallback architecture.

## Client layer

Nature 2.0 is the active C# Godot client over the shared contracts. It currently:

- sends binary player input over WebSocket;
- consumes binary or JSON entity updates;
- renders authoritative state and interpolates remote motion;
- renders region-profile terrain and natural resources;
- hosts atmosphere, world-generation, and tree-felling labs.

It does not yet bind the optional UDP channel or implement full local prediction and
reconciliation. See [Godot integration](network/godot-integration.md).

## Research and lab promotion queue

| Capability | Current stage | Next network stage |
|---|---|---|
| Tree felling | Research, pure simulation, lab, and 24-byte projection | Move projection into shared contracts, test serialization, broadcast, consume |
| World generation | Pure simulation, parameter sweep, lab, client terrain rendering | Promote generation into authoritative server pipeline |
| Natural resources | Server entities and JSON updates | Add explicit interest policy and binary hot path if measurements justify it |

Promotion stages and evidence requirements are defined in
[Research-to-lab method](labs/research-to-lab-method.md).

## Documentation rule

Use these labels when updating status:

- **Recorded** for contemporaneous plans or results;
- **Observed** for current code and tests;
- **Reconstructed** for dependency-based historical ordering.

A lab projection is not a serializer, a serializer is not a gateway path, and a
gateway path is not end-to-end until the active client consumes it.
