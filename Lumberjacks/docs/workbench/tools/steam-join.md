# Steam Self-Service Join

An invite link, a Steam sign-in, and a mod-pack zip that already has your
own credentials baked in — no config file to hand-edit.

## What it is

A live service at `https://comfy-p7.duckdns.org/join`. The flow:

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

The join flow works end to end on `comfy-p7.duckdns.org` today: invite,
Steam sign-in, personalized zip, self-service update and reissue.

## How to get in (there's no local "run it" — this is a live service)

1. Ask for an invite in this tool's Discord thread.
2. Open the invite link you're sent.
3. Select **Sign in with Steam** and complete Steam's prompt.
4. Press **Download my mod pack**.
5. Extract the `Valheim` folder from the zip into your local Valheim
   install folder, letting it merge.
6. Close and restart Valheim so BepInEx reloads the config and plugin.

Already installed and just need the latest build? Open
`https://comfy-p7.duckdns.org/join/update` (or the direct
`http://8.231.129.249:42317/join/update` while TLS isn't on yet — see
below) and sign in again; this does **not** rotate your credential.

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

- **Plain HTTP today.** The endpoint is unencrypted HTTP on a non-default,
  world-open port. TLS is built and staged — the certificate name, the
  Caddy sidecar, and the firewall rule all exist — but it has not been
  switched on. Until it is, your enrollment credential crosses a plaintext
  public link during install.
- **No rate limiting yet.** The runbook lists rate limiting as something to
  add "before wider public use," alongside credential revocation/rotation
  and access logging — none of that exists today.
- **Alpha cohort only, one client at a time**, per the shared-queue
  limitation above.
- **Dashboard `GET` routes aren't access-controlled**, even though the
  join/credential flow is.

None of that should stop you from trying the flow — it's exactly why SJ-1
below exists — just don't treat this as a hardened public service yet.

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
