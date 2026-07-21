# ADR 0011: Graceful Degradation and Designated Combat Zones

**Status:** Accepted
**Date:** 2026-03-26
**Depends on:** ADR 0001 (Thin Client), ADR 0003 (Multi-Lane Transport)

## Context

The platform targets 100+ player communities where network conditions vary wildly — from fiber connections to mobile hotspots. Classic MMO wisdom (EVE Online's Time Dilation, RuneScape's 600ms tick) teaches that the right response to load is graceful degradation, not failure. Meanwhile, real-time combat has fundamentally different tick rate and latency requirements than base-game exploration and building.

Two problems needed solving:
1. How does the platform behave when a player's connection is poor?
2. How do we support high-fidelity combat without forcing the entire world to run at combat tick rates?

## Decision

### Graceful Degradation

**Dialup is the minimum playable spec.** The platform must be playable (not optimal, but functional) at extremely constrained bandwidth. Quality degrades before authority degrades — meaning:

- **Reduce update frequency** before dropping updates entirely. A player on a slow connection gets fewer entity updates per second, not stale ones.
- **Reduce visual fidelity** before reducing gameplay fidelity. Distant structures become silhouettes, player models simplify, particle effects disappear — but inventory, placement, and progression remain authoritative.
- **Interest management tiers** control how much data each client receives based on relevance, distance, and connection quality. A player 500m from an event doesn't need 20Hz updates about it.
- **Never degrade authority.** The server's word is final regardless of client connection quality. A lagging client may see delayed confirmations, but the server never accepts stale client state as truth.

### Designated Combat Zones

**Combat happens in designated areas with elevated infrastructure.** Rather than engineering the entire world for the worst-case tick rate (high-frequency combat between many players), combat is spatially bounded:

- **Base-game tick rate (20Hz)** handles exploration, building, gathering, and social interaction. This is sufficient for realistic physics interactions (tree collisions, chain reactions) at the density we expect.
- **Combat zones** are designated areas where players opt in to PvP or PvE challenges. These zones can:
  - Run at higher tick rates when active
  - Leverage volunteer edge nodes from community members for additional compute
  - Burst to external cloud compute for tournament or challenge events
  - Apply stricter interest management (only combatants receive high-frequency updates)
- **Zone transitions are explicit.** Players enter combat zones intentionally — no surprise tick rate changes or sudden bandwidth spikes.

## Rationale

**Learned from the greats.** EVE Online proved that slowing simulation is preferable to crashing it (Time Dilation). RuneScape proved that a 600ms tick is sufficient for a massive, engaged playerbase if the game is designed around it. WoW proved that zone-based sharding is operationally sustainable at scale. We're applying these lessons from day one rather than discovering them under load.

**Separation of concerns.** Base-game activities (building, exploring, socializing) don't need 60Hz updates. Combat does. Forcing both into the same tick rate either wastes resources on exploration or starves combat. Spatial separation lets us allocate resources where they matter.

**Community edge support.** The community-operated model means some players will volunteer compute resources. Combat zones are the natural place to leverage this — bounded scope, clear start/end, measurable quality contribution.

**Progressive enhancement, not feature flags.** A player on fiber gets beautiful real-time updates. A player on 3G gets the same authoritative game at lower visual fidelity. Neither gets a different game — just a different experience of the same truth.

## Consequences

- The Simulation service must support per-region tick rate configuration (currently all regions default to 20Hz)
- Interest management must factor in connection quality as a bandwidth allocation signal
- Combat zone infrastructure (burst compute, edge node coordination) is a future implementation — the architecture supports it but the MVP doesn't require it
- The datagram lane (ADR 0003) becomes especially important for combat zones where UDP's loss tolerance beats TCP's head-of-line blocking
- Operator tooling must surface per-player connection quality metrics so administrators can identify and assist struggling players
- The base-game 20Hz tick is a design constraint that gameplay must be built around — not a temporary limitation to be "fixed" later
