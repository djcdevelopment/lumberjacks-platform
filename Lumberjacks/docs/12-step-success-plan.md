# 12-Step Plan For Success

## 1. Define the product boundary

Goal:
Build a community-operated survival platform, not a literal full remake of Valheim.

Tasks:
- Write a one-page product brief.
- Name the target player count for the first public test.
- State what belongs in v1 and what is explicitly out of scope.

Visible:
- `docs/product-brief.md`
- A short FAQ answering "what are we building?" and "what are we not building?"

What it takes to get started:
- One focused session with the core lead group and a final written scope decision.

## 2. Freeze the technical principles

Goal:
Prevent drift into client-authoritative or engine-led architecture.

Tasks:
- Record non-negotiables such as server authority, event-first progression, thin client, and non-authoritative community edge nodes.
- Decide what data must always exist without the client running.

Visible:
- `docs/architecture-principles.md`

What it takes to get started:
- Convert the current design convictions into explicit yes/no rules.

## 3. Choose a strict MVP slice

Goal:
Ship the smallest experience that proves the platform idea.

Tasks:
- Limit the MVP to one zone, one settlement loop, one travel corridor, one progression ladder, one guild loop, and one Discord-linked event flow.
- Define "done" in player terms.

Visible:
- `docs/mvp-scope.md`
- A checklist with a hard acceptance gate.

What it takes to get started:
- Cut every feature that does not prove persistence, scale, progression, or admin usability.

## 4. Design the backend before the client

Goal:
Own the hard systems where scale and trust matter.

Tasks:
- Define the simulation server, event log, progression engine, content registry, plugin host, Discord bridge, and admin UI.
- Write service contracts and ownership boundaries.

Visible:
- Service map
- Message flow diagram

What it takes to get started:
- Agree on the first interfaces before any engine-specific gameplay work.

## 5. Define the event model and progression DSL

Goal:
Make community creativity data-driven.

Tasks:
- Define canonical events.
- Define how quests, ranks, challenges, and rewards consume those events.
- Validate against three existing community scenarios.

Visible:
- `docs/events.md`
- `docs/progression-dsl.md`

What it takes to get started:
- Model real guild, rank, and event workflows from the current community.

## 6. Build interest management and simulation tiers early

Goal:
Prove the scaling model before spending time on polish.

Tasks:
- Implement region partitions.
- Implement relevance bands and activation tiers.
- Define which events wake which systems.

Visible:
- Server debug view showing active subscriptions and region load.

What it takes to get started:
- A harness with bot players moving through regions.

## 7. Build roads, safety, and distant settlement presence

Goal:
Protect travel readability and preserve player-made art at long distance.

Tasks:
- Create road safety corridors.
- Create distant settlement signatures and outlines.
- Define what detail unlocks at each distance band.

Visible:
- A map demo showing roads, landmarks, and settlement proxies.

What it takes to get started:
- One road and one settlement represented in at least three fidelity bands.

## 8. Keep the client thin

Goal:
Avoid locking the whole game into a single engine.

Tasks:
- Build client support for movement, prediction, interaction prompts, LOD display, and reconciliation.
- Keep authoritative rules out of the client.

Visible:
- A connected client that can move, build, and receive world and progression updates.

What it takes to get started:
- Placeholder art is acceptable; stable contracts are not optional.

## 9. Ship the community control plane

Goal:
Reduce volunteer overhead and make rules explainable.

Tasks:
- Build player lookup, guild progress, event setup, rank rules, and trigger inspection tools.
- Add "why did this happen?" audit views.

Visible:
- A web admin panel usable by non-programmers.

What it takes to get started:
- Start with read-only inspection, then add controlled write actions.

## 10. Create the plugin and contributor model

Goal:
Scale creativity without turning the platform into unbounded patchwork.

Tasks:
- Define data-only extensions, scripted rules, and full plugins.
- Define permissions, versioning, and review policy.

Visible:
- Contributor guide
- Three example extensions

What it takes to get started:
- Re-express one current custom guild or weapon feature through the new extension model.

## 11. Add community edge infrastructure safely

Goal:
Let communities contribute compute without compromising fairness.

Tasks:
- Define relay, cache, analytics, and indexing node roles.
- Define trust, health, failover, and visibility rules.
- Keep combat and inventory truth on trusted authority nodes.

Visible:
- Node role dashboard and regional assistance metrics.

What it takes to get started:
- Start with relay and cache only.

## 12. Run staged proving cycles

Goal:
Validate the platform under real community pressure.

Tasks:
- Run internal, trusted-alpha, community-beta, and high-density event tests.
- Track tick stability, reconciliation errors, progression correctness, and admin time per event.

Visible:
- A live scorecard with technical and community metrics.

What it takes to get started:
- Set pass/fail gates for each phase and do not skip them.
