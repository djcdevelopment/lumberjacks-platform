*Meta: forum thread title suggestion — "Community telemetry". Paste everything below the divider
as the thread's opening post.*

---

**Community telemetry**

What it is: a live, aggregates-only view of what's happening on the server — a tick-health panel,
a gameplay-events timeline, a local-testing panel. The API behind it is tested to refuse to ever
return a player ID, name, or position — that's enforced by a test suite, not just a promise. The
live page reads straight off the real server and is open right now, no install, no login.

Honest status: what's local-only, at least for now, is the rest of it — the full stack that
produces this (the database, gateway, and telemetry services together) and the private operator
viewer both currently only run on my machine, and that code isn't public yet. It opens once
someone's a Steward on this piece (see the pinned how-this-works post). So today's version of
"running" this tool is watching the live page closely, not standing up your own copy — that part's
still a step ahead.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **CT-1** — Open the live community page and the NetworkSense page next to it. Watch them for a
  few minutes — it's most interesting while someone's actually playing. Tell us if the numbers
  make sense, and whether the "sample data" banners are clear about what's real and what isn't.
- **CT-2** — Tell us what aggregate you wish this page showed and doesn't. You don't need the code
  for this one — it's genuinely useful on its own, and it's the list whoever ends up stewarding
  this piece will work from first.

What a useful reply looks like:

- What you looked at, and roughly when (server activity varies a lot).
- What you actually saw — errors or blank panels pasted verbatim, not summarized.
- What you expected instead.
