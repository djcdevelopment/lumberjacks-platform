# Spatial Interest Management

Compression reduces the cost of one update. Interest management limits how many
updates a client receives and how often it receives them.

## Spatial index

`SpatialGrid` partitions the XZ world plane into cells and tracks entity positions.
It supports insertion, movement, removal, and proximity queries without scanning the
entire world for every observer.

## Fidelity tiers

`InterestManager.FilterForObserver` applies distance and tick cadence:

| Tier | Distance | Cadence | Meaning |
|---|---:|---:|---|
| Near | 0–100 units | Every tick (20 Hz) | Immediate interaction fidelity |
| Mid | 100–300 units | Every fourth tick (5 Hz) | Context with interpolation |
| Far | Over 300 units | Omitted | A newer transient state may replace it later |

Filtering is evaluated per observer. A changed entity can therefore be full-rate for
one client, throttled for another, and absent for a third.

## What it filters

The current broadcaster applies `InterestManager` to changed player updates. Changed
natural resources use a simpler region-wide JSON path, with a code comment reserving
binary resource updates for later. The architecture supports broader interest
management, but the implementation should not be described as uniformly applying
all tiers to every entity type.

## Valheim adapter boundary (observed 2026-07-15)

The Valheim authoritative-ZDO adapter is a separate visibility boundary. The
Harmony mod suppresses selected Valheim sends, and Valheim's own sync-list
construction determines which ZDOs are relevant to a peer before those envelopes
enter the Lumberjacks Gateway. The Gateway consumer then applies the enrolled
envelopes; it does **not** currently re-run `InterestManager` tiers over the
Valheim ZDO stream.

This is compatible with the authority and delivery tenets only when it is stated
as an adapter exception: Valheim native relevance is the projection filter,
Lumberjacks owns sequencing/durability/application, and client-consumption proof
is separate from Gateway receipt proof. It must not be reported as uniform
Lumberjacks AoI coverage for every Valheim entity type.

This is an intentional phase-1 choice, not a permanent ceiling. A later
performance phase may move the relevance decision into ComfyNetworkSense so the
mod can suppress/aggregate earlier and reduce Gateway receipt, WAL, and
serialization work. That migration requires a paired native-vs-Lumberjacks
comparison proving identical peer-visible ZDO sets before it replaces the native
sync-list boundary.

The single-client P7 audit observed 100% enrolled coverage and zero native-only
traffic while the consumer drained the durable queue. Raw evidence is retained
outside this repository at:

```text
C:\work\comfy\fieldlab\runs\audits\p7-efficiency\
C:\work\comfy\fieldlab\evidence\p7-primary-v1-authoritative-zdo-20260715-v0527.md
```

The next AoI audit must measure Valheim relevance selection separately from
Lumberjacks player-tier filtering before making a multi-client scalability claim.

## Subscription-change events (`interest_subscription_changed`)

The tiers above are evaluated fresh every tick and are otherwise invisible — you cannot see
*when* a player crossed a boundary for another observer. The `interest_subscription_changed`
event surfaces exactly those transitions, so replication-policy experiments (Goal 6) have an
evidence trail instead of only aggregate counters.

A **subscription** is deliberately defined by the outer interest *radius*, not by the mid-tick
send throttle: an observer is subscribed to every other player within `SubscriptionRadius`
(MidRadius=300 under tiered, NearRadius=100 under radius), whether or not a given tick actually
carries the mid band. The throttle changes send *rate*; it does not change what the observer is
interested in. This keeps the event low-frequency — a pair only churns when relative distance
actually crosses the radius, not on every burst tick.

### Configuration (intent & usage)

| Variable | Default | Intent / usage |
|---|---|---|
| `Replication:SubscriptionEvents` | `false` | **Master switch.** Off = the diff/emit pass never runs (zero cost — this is why it is safe to leave in the default/benchmark path). On = emit `interest_subscription_changed` to the EventLog when a player enters/leaves an observer's interest radius. Intended as an **opt-in diagnostic feed for replication-policy experiments**, not a runtime feature. No-op under the `full` policy (nothing to observe). |
| `Replication:SubscriptionSampleTicks` | `20` | **Sampling interval.** Take a full subscription snapshot every Nth tick (20 ≈ 1 Hz at a 20 Hz tick). Larger = coarser/cheaper and lower event volume; `≤1` = sample every tick (heaviest). Bounds event rate without changing correctness. |

Both parse tolerantly (a bad value warns and falls back to the default; it never crashes
startup — same contract as the other `Replication:*` knobs). Env-var form uses `__`, e.g.
`Replication__SubscriptionEvents=true`.

Design properties worth keeping in mind when using it:

- **Off the hot path.** The sample is snapshotted on the tick thread (O(n), no distance math) and
  the O(n²) diff + fire-and-forget POST run on a background task, so the feed does **not** inflate
  the broadcast wall time that capacity runs measure. Verify this each run (flag off vs on — see
  the runbook).
- **Self-bounding volume.** One event per changed observer per sample, hard-capped at 500
  events/sample; departed observers age out naturally.
- **`actor_id` = the observer**; the payload lists the added/removed target players, the resulting
  `subscribed_count`, the active `subscription_radius`, and `policy`. Full payload in the
  [event catalog](../events.md#interest_subscription_changed).

To exercise it under load, see
[Testing `interest_subscription_changed` under load](interest-subscription-events-testing.md)
— the short version is *enable the flag, use `tiered`/`radius`, and drive real movement with
`BOT_WANDER=1`* (stationary bots never cross a boundary and produce nothing).

## Scaling argument

Without interest management, total world population determines each client's
downstream traffic. With a spatial filter, downstream cost is driven primarily by
nearby changed entities. That is the link between the 100-player goal and the
bandwidth target: binary encoding alone cannot cap a crowded global broadcast.

## Failure risks

- stale grid membership can make entities vanish or appear in the wrong tier;
- hard distance boundaries can expose update-rate transitions;
- five-Hz mid-band motion requires interpolation;
- new entity types can bypass the intended policy if added only to the broadcaster;
- reliable mutations need separate delivery rules from transient positions.

## Evidence

- `src/Game.Simulation/World/SpatialGrid.cs`
- `src/Game.Simulation/World/InterestManager.cs`
- `src/Game.Simulation/World/InterestSubscriptionTracker.cs`
- `src/Game.Gateway/WebSocket/TickBroadcaster.cs`
- `tests/Game.Simulation.Tests/SpatialGridTests.cs`
- `tests/Game.Simulation.Tests/InterestManagerTests.cs`
- `tests/Game.Simulation.Tests/InterestSubscriptionTrackerTests.cs`
- [ADR 0015](../adrs/0015-spatial-interest-management.md)
- [Testing `interest_subscription_changed` under load](interest-subscription-events-testing.md)
