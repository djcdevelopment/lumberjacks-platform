# Architecture Principles

## Mandatory Principles

1. Server authority owns truth.
Authoritative state for combat, inventory, progression, guilds, building ownership, and persistence lives on trusted server infrastructure.

2. The client is thin.
The client is responsible for presentation, input, prediction, interpolation, and user experience. It is not the source of truth.

3. Event-first progression.
Ranks, quests, achievements, and community challenges are evaluated from emitted events, not from hidden one-off game code.

4. Interest management is core infrastructure.
Subscription, relevance, and activation tiers are first-order architecture, not late optimization.

5. Distant art uses proxies.
Player-built settlements and landmarks must be visible at long distance through cheap signatures, outlines, or LOD representations before full detail loads.

6. Travel must be readable.
Roads, landmarks, and danger warnings must help players understand space before accidental combat punishes them.

7. Community edge nodes may assist but not own truth.
Community-provided compute may relay, cache, index, or precompute. It may not own combat truth, inventory truth, or progression truth.

8. All meaningful state must exist without the client running.
Operators must be able to inspect, repair, and reason about the world from backend tools alone.

9. Plugins target stable SDKs only.
Extensions must use published contracts and permissions, not private internal service details.

10. Explainability matters.
If a player gains or loses rank, reward, or status, operators must be able to answer why from durable system evidence.

## Early Consequences

- Every meaningful action should emit a durable event.
- Every region should have visible load and subscription telemetry.
- Every system boundary should have an owning service.
- Every content definition should be versioned and validated.
