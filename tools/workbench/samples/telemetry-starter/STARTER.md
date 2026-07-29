# Community Telemetry — starter kit

The Lumberjacks Gateway publishes an **aggregates-only** telemetry API (`v0`, explicitly
unstable) and a live community page built on it. This kit lets you poke both **today,
with nothing but Python** — no repo access, no account.

**Privacy, stated plainly:** the v0 API is aggregates-only *by tested design* — an
automated test suite asserts no player ID, player name, or position ever appears in any
response. If you ever see one, that's a bug worth reporting loudly in the thread.

## See it live (0 minutes)

- Community page: `<SITE>/community` — self-contained HTML polling the API every ~2s.
- The API itself: `<SITE>/api/v0/telemetry/server` (and: `tick`, `sessions`, `delivery`,
  `regions`, `events`, `valheim`, `cutover`).

`<SITE>` is the server shown on the Workbench page. Every response carries
`api_version: v0` and `stability: unstable` — that's honesty, not neglect: the shape may
change while it's v0.

## Poll it from Python (2 minutes)

```
python poll_telemetry.py https://<SITE>
```

Prints a one-screen summary of every endpoint. Pure standard library. The full
endpoint-by-endpoint reference (with real sample JSON) lives in `telemetry-v0.md`,
included in this kit.

## First task CT-1 (small)

Build **one chart, tile, or dashboard widget** from any v0 endpoint — any stack you like
(a static HTML page, a spreadsheet, a terminal script) — and post a screenshot plus how
you made it in the community-telemetry thread. What people build here directly shapes
which aggregates v1 keeps.

## The bigger piece (the claim path)

The full local stack (Gateway + eventlog + progression + postgres via docker compose) and
the operator dashboard run from the source tree, which is granted per-piece when you
claim a tool (see the Workbench ladder). CT-2 — adding a new aggregate tile with the
privacy tests still green — is the natural second step once you're there.
