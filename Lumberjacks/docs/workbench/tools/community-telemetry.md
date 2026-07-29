# Live Community Telemetry

An aggregates-only telemetry API with a test suite that fails the build if
a player ID, name, or position ever shows up in a response — plus the
whole stack, runnable on your own machine.

## What it is

A versioned, explicitly-unstable public API (`GET /api/v0/telemetry/...`)
served by the Gateway, covering `server` (identity, uptime, replication
config), `tick` (per-phase timing against a 50 ms budget), `sessions`
(counts by protocol/region only), `delivery` (cumulative networking
counters, UDP reject rate), `regions` (world-level facts only), `events`
(an anonymized, allow-listed feed of recent gameplay events from a bounded
200-entry ring buffer), `valheim` (a sanitized mod heartbeat), and `cutover`
(the Lumberjacks-authority rollout state). Every response carries
`"api_version": "v0"` and `"stability": "unstable"`, and every `/api/v0/*`
route (plus `/community`) allows cross-origin `GET` from anywhere, no
credentials — it's built to be a public, script-friendly surface.

`GET /community` is a single self-contained HTML page, served by the
Gateway, that polls those endpoints every 2 seconds for a live,
out-of-the-box dashboard.

A local Docker Compose stack (`Lumberjacks/infra/docker/docker-compose.yml`)
reproduces the whole backend on your machine: Postgres, the Gateway
(everything above, plus the community pages, on port 4000), the event log
service, the progression service, and an Operator API. A separate Vite app
(`clients/admin-web`) gives an admin console over that Operator API. Every
v0 endpoint works even without Postgres — they read in-memory state and
degrade gracefully, which the repo verifies by running the built image with
no `ConnectionStrings__GameDb` set at all.

## What it is NOT

Not something you need to deploy anywhere to try. Every piece above runs
on `localhost` from a normal `docker compose up`.

Not the same thing as the separate `Lumberjacks/tools/omen-dashboard/`
proxy you may see sitting next to this. That's a different,
operator-only tool that mirrors the **live GCP P7 deployment** through an
SSH/IAP tunnel using the operator's own Google Cloud identity — it is not
something you can use without GCP access to that project, and it's not
part of this one-pager. If you're not the operator, ignore it.

Not authenticated. The Operator API and admin console have no login yet —
"admin" today means "on the right network," not "logged in." Don't run the
stack somewhere that matters.

## Status

The aggregates API is live on P7 and the public `/community` page reads it
today — but the Docker stack that reproduces the whole system locally, and
the operator dashboard that views it, aren't published anywhere as a
download. You run them yourself, from this repo.

## Run it in about 30 minutes

From the repo root:

1. `docker compose -f Lumberjacks/infra/docker/docker-compose.yml up --build -d`
   — builds in-container, so your local .NET SDK version doesn't matter.
2. `curl -s http://localhost:4000/api/v0/telemetry/server` — confirm it's
   up (tick counter, uptime, replication config).
3. `docker compose -f Lumberjacks/infra/docker/docker-compose.yml ps` —
   confirm every service says "running": `postgres` (5435), `gateway`
   (4000), `eventlog` (4002), `progression` (4003), `operatorapi` (4004).
4. Open `http://localhost:4000/community` in a browser.

Also worth opening: `/networksense` (a G3 tick/session/delivery HUD),
`/events` (the live anonymized gameplay-event feed), `/roadmap` (the
volunteer roadmap), and `/testing` (simulated scenario cards).

To stop: `docker compose -f Lumberjacks/infra/docker/docker-compose.yml down`
(add `-v` to also wipe the Postgres volume).

## What you'll see

`/community` polls every 2 seconds and is deliberately sparse on an idle
local stack: sessions, delivery, regions, gameplay, and quest panels hide
themselves until they have real data, but a live trace rail stays visible
so you can watch each poll and state transition happen in real time. If a
poll ever fails, the page shows a "reconnecting / stale" chip and keeps the
last good values — it's built to never fabricate data. There's no
graphical game client to log into (the Godot client was dropped) — "in
game" here means this browser tab, or simulated sessions from the
`tools/synthclient` load-test harness, or a real Valheim client connected
through the mod's Gateway bridge.

## What's rough

- **No graphical client.** To generate real sessions, RTT, and delivery
  data (as opposed to the structure/region events a couple of `curl`s can
  fake), you need the `tools/synthclient` harness running in a `dotnet/sdk:9.0`
  container attached to the compose network — there's no simpler path yet.
- **No auth on the admin surfaces.** The Operator API and the admin
  console are open to anything that can reach the port. Don't expose this
  stack.
- **The GCP-mirroring dashboard lives right next to this and is easy to
  confuse with it.** `Lumberjacks/tools/omen-dashboard/` is a separate,
  operator-only proxy for the live P7 deployment — see "What it is NOT."

## First tasks

- **CT-1 — Bring the local stack up and post a screenshot of `/community`
  running against it.** Done when: the compose stack is up, `/community`
  is rendering from your own local Gateway (not P7), and the screenshot
  plus anything that didn't work as documented are in the thread.
- **CT-2 — Add one new aggregate tile with privacy tests still green.**
  Done when: a new aggregate is exposed by the v0 API and rendered on the
  page, and the existing privacy test suite still passes unmodified.

## Where to talk about it

Its Discord thread (link lands with the announcement).

## License & privacy

BSL 1.1 public-source posture — this code is in `Lumberjacks/` in this
repo, covered by the root `LICENSE` / `LICENSING.md`.

Privacy: v0 is aggregates-only by tested design — no player ID, name, or
position ever appears in a v0 response, enforced by an automated test
suite (`tests/Game.Simulation.Tests/TelemetryV0EndpointsTests.cs`,
`tests/Game.Gateway.Tests/TelemetryV0SessionsEndpointsTests.cs`) that
asserts connected players' identifiers never show up anywhere in the
output. Keep that bar: a change that makes those tests fail is a change
that doesn't land, no matter how useful the tile.
