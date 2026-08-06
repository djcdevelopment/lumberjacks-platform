# Steam Self-Service Join

An invite link, a Steam sign-in, and a mod-pack zip that already has your
own credentials baked in — no config file to hand-edit.

> **Not reachable today.** This is built and it answers on the Gateway, but it is not
> routed to the public internet right now — the public `/join` path on the community
> host currently belongs to an unrelated service. The world it would connect you to is
> not open either. Nothing on this page is clickable yet; it is here so you can read
> what exists and see what it would take to open it.

## What it is

An enrollment flow served by the Gateway. When it is routed publicly, it works like
this:

```text
admin generates a random, one-use, 24-hour invite
  -> you open the invite link
  -> you sign in with Steam (OpenID)
  -> the Gateway redeems the invite once and issues a one-use bootstrap
  -> you download a personalized mod pack
  -> the installer/download mints your per-player access token, once
  -> the mod uses that token to talk to the Gateway from then on
```

The generated BepInEx config has your own enrollment id and access token
already filled in — you don't type or paste anything into it. Later
updates and credential reissue go through the same self-service flow
(`/join/update`, `/join/reissue`) without an admin in the loop. The Gateway
never stores your reusable access token itself, only its hash.

## What it is NOT

Not open enrollment. Every invite is admin-generated, random, single-use,
and expires in 24 hours — there's no public sign-up page and no way to
mint your own invite. Ask in this tool's Discord thread to get one.

Not a platform yet, and not pretending to be one. While P7 uses one shared
enrollment queue (`p7-primary-v1`), the runbook is explicit that only one
client should be admitted at a time — "Admit only one client while P7
still uses the shared p7-primary-v1 queue." Multiple simultaneous
volunteers need the queue made recipient-scoped first; that hasn't
happened yet.

Not access-controlled everywhere. The dashboard `GET` routes behind the
same Gateway are not gated the way the join/credential flow is.

## Status

Built, unrouted, and never walked end to end.

The endpoints exist in the Gateway and have test coverage. What has not happened: the
public host does not route `/join` here, no one has completed the Steam round-trip on
this deployment, and the server is closed. The earlier host this doc pointed at
(`comfy-p7.duckdns.org`) is a terminated VM.

## How you would get in, once it opens

Written down so the shape is reviewable — none of these steps are available yet.

1. Ask for an invite in this tool's Discord thread.
2. Open the invite link you're sent.
3. Select **Sign in with Steam** and complete Steam's prompt.
4. Press **Download my mod pack**.
5. Extract the `Valheim` folder from the zip into your local Valheim
   install folder, letting it merge.
6. Close and restart Valheim so BepInEx reloads the config and plugin.

Updating an existing install would go through `/join/update`, which re-signs you in
without rotating your credential.

## What you'll see

After Steam sign-in, a page that hands you a zip download. The zip
intentionally omits your personal config file
(`djcdevelopment.valheim.comfynetworksense.cfg`, so a re-download never
clobbers settings you've already changed) and includes
`Install-LumberjacksMod.ps1`, which copies the mod files while preserving
your existing config. First-time installs do get the full config, with
your enrollment id and access token already filled in under `[Lumberjacks]`.

## What's rough

The operator runbook (`infra/gcp/p7/VOLUNTEER-ENDPOINT.md`) is direct about
what's not done, and this doc isn't going to soften it:

- **Not publicly routed.** The community host serves an unrelated service on
  `/join`, so the Gateway's real enrollment flow is unreachable from outside.
  Untangling that comes before anything else on this list.
- **Never walked end to end.** The Steam round-trip has not been completed once
  on this deployment. That is first task SJ-1, and it cannot start until routing
  is fixed.
- **No rate limiting.** The runbook lists rate limiting as something to add
  "before wider public use," alongside credential revocation/rotation and access
  logging — none of that exists today.
- **One client at a time**, per the shared-queue limitation above.
- **Dashboard `GET` routes aren't access-controlled**, even though the
  join/credential flow is.

Taken together: this is a design and a codebase you can read and critique right
now, not a service you can use. Treating it as a hardened public service would be
wrong in both directions.

## First tasks

- **SJ-1 — Walk the join flow end-to-end as a tester and file friction
  notes.** Done when: you went from invite link to a running modded
  client, and every point where you hesitated, guessed, or had to ask is
  written down in the thread — including the ones that turned out to be
  your own fault.
- **SJ-2 — Write the tester-facing FAQ from those friction notes.** Done
  when: a page answers the questions SJ-1 actually produced, in a
  tester's words rather than the operator's, and the next tester gets
  through without asking any of them.

## Where to talk about it

Its Discord thread (link lands with the announcement) — that's also how
you ask for an invite.

## License & privacy

BSL 1.1 public-source posture — the Gateway code behind this flow is in
`Lumberjacks/` in this repo, covered by the root `LICENSE` / `LICENSING.md`.

Privacy: your access token is stored only as a hash — the Gateway itself
can't hand it back out. The zip you receive carries your own personal
credential; do not share it, screenshot it, paste it into logs or chat, or
commit it. If it's ever exposed, ask for a reissue and get the old record
revoked before using that Steam account again.
