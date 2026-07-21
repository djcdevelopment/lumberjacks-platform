# Testing `interest_subscription_changed` under load

A runbook for exercising the interest-subscription event feed during a load test. Fold this
into the next capacity run (see [host-capacity benchmark](../benchmark-host-capacity-2026-07-12.md)) —
it produces replication-policy evidence (Goal 6) at near-zero extra effort while bots are
already moving.

The event and its config are documented in
[interest-management.md → Subscription-change events](interest-management.md#subscription-change-events-interest_subscription_changed).
This page is the *how to test it* companion.

## Why it needs a specific setup

Two things are easy to get wrong and both produce a silent "no events, looks broken":

1. **The feed is OFF by default.** It only runs when `Replication:SubscriptionEvents=true`.
2. **Stationary bots never cross a tier boundary.** A subscription only changes when a player
   moves in or out of another observer's interest radius (300u under the default tiered policy).
   The default load-test bots random-walk in a tight cluster and rarely cross 300u, so you get
   almost nothing. **You must drive real movement** — set `BOT_WANDER=1` so bots travel toward
   waypoints across the whole region (±450u), which repeatedly crosses the 300u mid boundary.

It is also a **no-op under the `full` policy** (no interest filtering, so there is no boundary
to observe). Use `tiered` (default) or `radius`.

## Prerequisites

- Gateway (`Game.Gateway`) running, with an **EventLog reachable** at `ServiceUrls:EventLog`
  (default `http://localhost:4002`). The host-capacity benchmark runs the gateway *DB-less and
  EventLog-less*; that mode will emit-and-fail-silently (the POST is fire-and-forget and logs a
  warning). To actually capture events either bring up the full stack
  (`infra/docker/docker-compose.yml`, EventLog on `:4002`) **or** point the gateway at a mock
  sink (below).
- `scripts/load-test-dual-channel.js` (Node) as the movement driver.

## Steps

### 1. Enable the feed on the gateway

```bash
# on the gateway process/container
Replication__SubscriptionEvents=true      # master switch (default false)
Replication__SubscriptionSampleTicks=20   # sample every N ticks; 20 ≈ 1 Hz at 20 Hz tick (default)
Replication__Policy=tiered                # tiered (default) or radius — NOT full
ServiceUrls__EventLog=http://localhost:4002   # where events are POSTed (default)
```

Confirm at startup — the gateway logs one of:

- `Replication:SubscriptionEvents on — sampling interest subscriptions every 20 tick(s) at radius 300` ✅ active
- `Replication:SubscriptionEvents requested but inactive (policy=full …)` ⚠️ won't emit — wrong policy or no HTTP factory

### 2. (Optional) mock EventLog sink

If you only want to *see* the events without the full stack, run a trivial sink and point the
gateway at it:

```bash
# logs every POSTed event body to stdout on :4002
node -e 'require("http").createServer((q,s)=>{let b="";q.on("data",d=>b+=d);q.on("end",()=>{if(q.url.startsWith("/events")){try{console.log(JSON.stringify(JSON.parse(b)))}catch{console.log(b)}}s.writeHead(200);s.end("{}")})}).listen(4002,()=>console.error("sink on :4002"))'
```

### 3. Drive movement (the important part)

```bash
# 100 bots, 60s, WANDERING so they cross the 300u boundary
BOT_WANDER=1 node scripts/load-test-dual-channel.js ws://localhost:4000 100 60
```

`BOT_WANDER=1` is what generates subscription churn. Without it, expect near-silence.

### 4. Observe the events

- **Via EventLog API** (full stack): paginated, filterable —
  ```bash
  curl 'http://localhost:4002/events?type=interest_subscription_changed&limit=20'
  ```
- **Via mock sink:** watch its stdout.
- **Via gateway logs:** a debug line appears only if a single sample hit the 500-event safety cap:
  `interest_subscription_changed sample capped at 500 events …`.

## What a good result looks like

Each event carries `actor_id` = the observer and a payload of:

```jsonc
{
  "tick": 1234,
  "subscribed_count": 7,          // observer's total subscriptions after this change
  "added": ["player-…"],          // entered its interest radius this sample
  "removed": ["player-…"],        // left it
  "added_count": 1, "removed_count": 0,
  "subscription_radius": 300,     // MidRadius (tiered) / NearRadius (radius)
  "policy": "tiered"
}
```

Sanity checks to bank as evidence:

- **Rate scales with movement & population**, and **inversely with `SubscriptionSampleTicks`.**
  Doubling bots or halving the sample interval should roughly track event volume.
- **Policy sensitivity:** rerun with `Replication__Policy=radius` (100u boundary) — expect *more*
  churn than tiered's 300u (players cross the smaller radius more often). `full` → zero events.
- **Boundary radius** in the payload matches the active policy (300 tiered / 100 radius).
- **No hot-path regression (the key one for a capacity run):** the feed is computed off the tick
  thread. Run the same bot load once with the flag **off** and once **on**, and compare the
  broadcast-phase p50/p99 from `/tick` (or the windowed tick log). They should be statistically
  unchanged — that is the proof the diagnostic doesn't distort the very numbers a capacity run
  measures. Record both in the benchmark doc.

## Interpreting volume

At 20 Hz with `SubscriptionSampleTicks=20`, a sample is taken ~once per second. Each sample emits
at most one event per observer whose subscription changed, hard-capped at 500 events/sample. So
worst case ≈ `min(500, movers)` events/sec. If that is too much for a run, raise
`SubscriptionSampleTicks` (coarser) — it bounds rate without changing correctness.

## Evidence

- `src/Game.Gateway/WebSocket/TickBroadcaster.cs` — sampling + emit (off-tick-thread)
- `src/Game.Simulation/World/InterestSubscriptionTracker.cs` — compute + diff
- `tests/Game.Simulation.Tests/InterestSubscriptionTrackerTests.cs`
- [Event catalog](../events.md#interest_subscription_changed)
