# G3/G4/G5 UI first pass — design rationale

Status: **First-pass mockups**, adopted 2026-07-12. Implements the client/UX track of
[community-telemetry-strategy.md](../community-telemetry-strategy.md) (goals G3, G4, G5),
built as siblings of the Live Community View (`/community`, G2) on top of the Public
Telemetry API v0 ([docs/api/telemetry-v0.md](../api/telemetry-v0.md)).

All three pages are single self-contained HTML files (inline CSS+JS, zero external
dependencies, CSP-locked to `default-src 'self'`), served verbatim by the Gateway using
the same pattern as `/community`: read once at startup into memory, with a try/catch
fallback to a minimal inline page if the file is missing
(`src/Game.Gateway/Endpoints/{NetworkSenseEndpoints,GameplayEventsEndpoints,LocalTestingEndpoints}.cs`).
HTML/CSS/JS was drafted by Gemini Pro (`gcp-gemini-pro` via HEARTH) from a detailed spec
per page, then reviewed and security-hardened by hand — see "Gemini usage" at the bottom
of this doc.

**Honesty rule applied throughout:** live data appears only where the backend genuinely
exists (G3, entirely). Where it doesn't (G4's gameplay-event feed, G5's scenario
triggers), the page shows clearly-labeled SAMPLE data or a "would call this endpoint"
simulation behind a persistent, non-dismissible banner — never presented as live.

---

## G3 — NetworkSense HUD (`GET /networksense`)

**What it is.** A glanceable, color-coded overlay panel showing live server health: a
tick-budget health bar (p99 vs the 50ms budget), sessions by protocol/region, the
delivery-path mix, and a rolling sparkline of tick timing. Polls three v0 endpoints every
2s: `/api/v0/telemetry/tick`, `/api/v0/telemetry/sessions`, `/api/v0/telemetry/delivery`,
each independently try/caught so one endpoint's outage doesn't blank the other panels.

**The key design choice.** This is deliberately an *overlay*, not a dashboard: a single
~320px-wide, semi-transparent, rounded panel fixed to a corner of the viewport, sized and
styled like a game HUD element rather than an admin console. There's no dense grid of
small-print numbers — the tick-health readout is one big color-coded gauge (green under
70% of budget, yellow 70–100%, red at/over budget or on any overrun), sessions/delivery
appear as compact chips, and history is a small sparkline, not a table of samples.

**The reason.** NetworkSense sits over live gameplay in a project whose ethos is
"everyone is an alpha tester" — every session is implicitly a playtest, and the person
watching this HUD is usually also playing, not sitting at a monitoring desk. An overlay
has to convey health in peripheral vision, at a glance, without competing for attention
with the game itself. Dense tables are the right tool for *post-hoc* analysis (someone
sits down afterward and studies a session); overlays are the right tool for *in-the-moment*
awareness (something's degrading right now, glance at the corner, confirm or dismiss).
Conflating the two would produce a page that's mediocre at both jobs — too sparse for
real analysis, too busy to glance at. Keeping them as genuinely different UI genres (this
overlay vs. `/community`'s admin-style grid) is the actual design decision; the specific
gauge/chip/sparkline choices just follow from "overlay, not table."

**Data source.** 100% live v0 API — `tick_timing.phases.total.{p50_ms,p99_ms}` vs
`budget_ms`/`overruns` for the health gauge and sparkline, `replication.{policy,sent,culled}`
for the substats, `by_protocol`/`by_region` from `/sessions` for the session chips, and
`delivery`/`transitions` from `/delivery` for the delivery-mix bar. No sample data
anywhere on this page — it degrades to a per-panel "DISCONNECTED" / "WARMING UP..." state
(the latter matching `/tick`'s null-until-first-window-closes behavior) rather than ever
fabricating numbers.

**Future work.** None needed to make this page "real" — it already is. Future iterations
might add interest-tier metrics as G6's replication-policy work lands, or a second
overlay variant for narrower viewports.

---

## G4 — Gameplay Event Telemetry (`GET /events`)

**What it is.** A chronological evidence timeline of gameplay events — first hit, killing
blow, weapon usage, projectile, trigger — each entry showing a timestamp, an anonymized
actor pseudonym, region, weapon (where relevant), and a provenance badge. A row of toggle
filters lets the viewer narrow to specific event types.

**The key design choice.** The provenance badge vocabulary and colors are lifted
*verbatim*, not reinvented, from the existing achievements provenance model
(`src/Game.Contracts/Achievements/Achievement.cs`'s `ProvenanceTier` and its wire strings,
mirrored in `clients/admin-web/src/App.tsx`'s `provenanceBadge()`):

| Tier | Label | Color | Meaning |
|---|---|---|---|
| `observed` | Observed | `#58a6ff` (blue) | Directly derived from authoritative server events |
| `reconstructed` | Reconstructed | `#d29922` (amber) | Inferred from aggregate/derived data |
| `verified` | Verified | `#3fb950` (green) | Confirmed by an independent second signal |
| `community_awarded` | Community-awarded | `#d2a8ff` (violet) | Explicitly granted by members/stewards |

**The reason.** The strategy's core discipline — stated up front in
community-telemetry-strategy.md and already load-bearing in the achievements slice — is
"evidence immutable, interpretation separate." A gameplay event (a killing blow, a
trigger firing) *is* evidence in exactly the same sense an achievement-unlocking event is:
a claim that only means something once you know how confidently it's backed. Achievements
already solved "how do we show confidence without pretending all confidence levels are
equal" with this four-tier vocabulary, including the explicit rule (carried over here)
that `Observed`/`Reconstructed` can auto-populate but `Verified`/`Community-awarded` are
extension points requiring a second signal or human authority. Inventing a second
color/label scheme for gameplay events would fragment the mental model the moment a
community member looks at both this page and the achievements view side by side — they'd
have to learn two vocabularies for the same underlying idea. Reusing the tiers verbatim
means "provenance" becomes one concept that spans the whole telemetry surface, not a
per-feature convention.

**Data source.** SAMPLE data only, behind a persistent "Sample data — backend pending"
banner (not dismissible, not hidden on a successful-looking response) — the quest
trigger system does not yet emit anything queryable. The page *does* attempt a real fetch
first (to the documented future endpoint below) and only falls back to the hardcoded
20-event sample array on any failure; today that fallback always fires. ~20 sample events
are weighted realistically: mostly `observed` (since real gameplay events would be
directly-observed-server-event-derived), with a few `reconstructed` and at most one each
of `verified`/`community_awarded`, matching the real system's actual distribution where
those two tiers are rare, human/multi-signal-gated extension points.

**Intended future endpoint.**
```
GET /api/v0/telemetry/events
{
  "api_version": "v0",
  "stability": "unstable",
  "events": [
    {
      "event_id": "evt-...",
      "event_type": "killing_blow",         // first_hit | killing_blow | weapon_usage | projectile | trigger
      "occurred_at": "2026-07-12T21:58:03Z",
      "actor_label": "Player-3F9C",          // pseudonymous only — see privacy note below
      "region_id": "region-spawn",
      "weapon": "iron_axe",                  // nullable
      "provenance": "observed"               // observed | reconstructed | verified | community_awarded
    }
  ]
}
```
**Open design question for whoever builds this endpoint:** the v0 API's hard privacy rule
(no player id, name, or position — see `TelemetryV0Endpoints`'s doc comment and its
`NoV0ResponseLeaksConnectedPlayerIdentifiersOrPositions` test) means a real `actor_label`
cannot be a persistent player identity either, or it becomes a de-anonymization vector
across a session (the same "Player-3F9C" appearing on ten kills is functionally a player
id). The sample data above models an *opaque* short pseudonym, but doesn't resolve
whether the real implementation should rotate pseudonyms per-session, hash to a stable
per-world (not per-player-account) token, or omit the actor field from the public feed
entirely and only expose actor-level detail through an authenticated/delayed channel.
That's a decision for whoever builds G4's backend, flagged here rather than answered.

---

## G5 — Local Testing Tools (`GET /testing`)

**What it is.** A control panel of four scenario cards — Spawn enemy group (configurable
count), Run benchmark scenario (configurable player/bot count + duration), Start/stop
telemetry capture, Begin/end replay route — each requiring an explicit confirm step before
"running," plus a live, timestamped status strip logging every action.

**The key design choice.** Two things, deliberately paired: (1) every card action is
confirm-then-log, never silent — clicking a card pops a native `confirm()` naming the
exact action and its parameters, and only after confirmation does the status strip append
a line; (2) the benchmark card is explicitly a *thin wrapper* around the existing
`scripts/load-test-dual-channel.js` harness — its inputs (`player_count`, `duration_sec`)
map 1:1 onto that script's actual CLI arguments, and its card subtext says outright "Wraps
scripts/load-test-dual-channel.js — no second load path." Right now, with no backend
wired, every card (including benchmark) only ever logs "Would POST to `<endpoint>`
(backend pending)" — nothing on this page can currently spawn load or mutate server
state. The one live network call is a read-only 5s poll of `/api/v0/telemetry/server`
(an existing aggregates-only endpoint) purely to show whether the gateway is reachable.

**The reason.** The strategy is explicit that this goal is about replacing ad-hoc console
commands with "UI over console commands" and favoring clickable workflows — the whole
point is that a scenario like "spawn 20 enemies and watch what happens" should be a
one-click, self-documenting, repeatable action instead of something only the person who
remembers the console incantation can run. But some of these actions are also the
heaviest and most disruptive things this repo's testing tooling can do (the benchmark
card, wired up, would generate real load against a real server) — a UI that makes heavy
actions *easier* to trigger has to also make them *harder to trigger by accident* and
*impossible to run silently*. The confirm step plus the always-visible status strip do
that: nothing fires without an explicit second click, and every fired-or-attempted action
leaves a visible, timestamped trace, so "what did this panel just do" is always
answerable by reading the strip rather than guessing. The benchmark-wraps-the-harness
constraint exists because this repo already has exactly one blessed load-generation path
(`load-test-dual-channel.js`, used for the host-capacity benchmark work); a second,
UI-native load implementation would inevitably drift from it in bot behavior, protocol
mix, or RTT methodology, and then "the load test" and "the benchmark UI" would quietly
stop meaning the same thing. Wrapping, not reimplementing, keeps CLI automation and GUI
testing provably aligned.

**Data source.** No mutating live data — every trigger endpoint below is fictitious today
and the page states so on every use ("(backend pending)"). The only live call is the
read-only gateway-reachability poll against `/api/v0/telemetry/server`, chosen because
it's already a public, aggregates-only, non-destructive v0 endpoint — reusing it here
adds no new data-exposure surface.

**Intended future endpoints** (none implemented; all are POST, all currently just logged
as "would call"):
```
POST /api/v0/testing/spawn-enemies   { "count": <int> }
POST /api/v0/testing/benchmark       { "players": <int>, "duration_s": <int> }
                                        → server-side, shells out to
                                          scripts/load-test-dual-channel.js
POST /api/v0/testing/capture/start
POST /api/v0/testing/capture/stop
POST /api/v0/testing/replay/start
POST /api/v0/testing/replay/end
```
These are local/developer-testing endpoints, not public telemetry — they should NOT be
added under `/api/v0/telemetry/*` when built; a separate, presumably auth-gated,
`/api/v0/testing/*` (or non-versioned dev-only) surface is more appropriate, since unlike
the read-only telemetry API these are mutating and potentially disruptive to a shared
server.

---

## Gemini usage

Each page was drafted by `gcp-gemini-pro` via HEARTH from a detailed spec (exact v0 JSON
shapes, the design direction above, CSP/self-contained constraints, and the sample-data
honesty rule) written to a temp file and passed as the prompt. All three generations
succeeded on the **first pass** — no retries needed. After generation, every page was
reviewed by hand for:

- **XSS / escaping.** All three pages already escaped server- or user-derived strings
  before `innerHTML` insertion (region ids, protocol/delivery keys, sample event fields,
  form input values echoed into the status strip). G5 went further and used
  `document.createTextNode`/`textContent` exclusively for its log entries, sidestepping
  the question entirely. No unescaped interpolation of dynamic data into `innerHTML` was
  found in any of the three pages — no fixups were required here.
- **CSP / external resources.** All three carry the exact required
  `default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'` meta tag and
  contain zero external URLs (`grep`-verified: no `http://`, `https://`, CDN, or font
  references in any of the three files).
- **Graceful degradation.** G3 shows per-panel stale/disconnected states and a
  warming-up state matching `/tick`'s null-until-first-window semantics; G4's fetch
  attempt always falls back to sample data (the endpoint doesn't exist, so this path is
  exercised on every load today) and never throws uncaught; G5's only live call
  (gateway-reachability poll) fails closed to an "unreachable" indicator without blocking
  the rest of the UI.
- **Branding/title consistency.** Minor hand-edit: titles/headers were normalized to the
  `Lumberjacks — <page name>` convention already used by `/community`
  (Gemini's drafts used ad-hoc titles like "G4 Gameplay Event Telemetry" / "G5 Local
  Testing Tools").

No other hardening was needed — the specs' explicit escaping/CSP/honesty requirements
were followed closely enough that this was mostly a verification pass, not a rewrite.

## Verification

Built and ran the Gateway container DB-less (`docker build --target gateway`, `docker run
-p 4106:4100 -e Urls=http://0.0.0.0:4100`, no Postgres attached — Gateway logs the
expected "Could not load persisted data — running with in-memory defaults only" warning
and continues, same as `/community` today). All three new routes returned HTTP 200 with
their key markers present (`id="hud-overlay"`/`id="panel-tick"` for `/networksense`, the
"Sample data — backend pending" banner and `id="timeline"` for `/events`, `id="logArea"`
and "backend pending" text for `/testing`), alongside the pre-existing `/community` and
all five `/api/v0/telemetry/*` endpoints (also 200). `dotnet build src/Game.Gateway` and
`dotnet test tests/Game.Simulation.Tests` both ran clean via the `dotnet/sdk:9.0` Docker
image (149/149 passing, matching the pre-existing baseline — no test count regression),
including the privacy test
(`NoV0ResponseLeaksConnectedPlayerIdentifiersOrPositions`) run individually to confirm it
still holds; none of the three new pages add a new data endpoint, so the privacy
guarantee is unaffected by construction — all three consume only the existing
aggregates-only v0 endpoints.
