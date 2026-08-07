*Meta: forum thread title suggestion — "World photography → gallery". Paste everything below the
divider as the thread's opening post. Split out of the "Recoverable pieces" thread on 2026-08-06,
when this stopped being recoverable and started being a thing that runs.*

---

**World photography → gallery**

What it is: point it at a world save and it finds every place people actually built, works out
where a camera should stand and which way it should point for each one, then photographs the lot
while you do something else. A gallery for builds that never had one.

It used to be in the recoverable pile as a *flythrough* — fly a route, record it, cut the video.
That plan got replaced rather than revived. It shoots stills now, there is no video step, and it
works: last run was 1,833 structures found, 161 photographed, **1,411 photographs, none of them
framed by a human**.

How the camera gets placed is just trigonometry on each building's bounding box — how far back to
stand for it to fill the frame, four bearings 45° off its long axis so no shot is dead-on a wall,
and tilt down on flat things and level off on tall ones. The two genuinely interesting bits were
measuring the light instead of guessing at it, and having the game write a receipt for every frame
recording what was asked for against what actually happened.

Honest status: **local-only**. The planning, scoring and gallery half is in the repo at
`tools/selfie-stick/`. The in-game half is a BepInEx plugin that is still archive-only, and you
would also want a ComfyStewardView cache for your world. Nothing is packaged, so this is not a
download-and-run tonight — that gap is the work, not the code.

The whole thing written up, with the arithmetic and what each stage actually emits:
<https://djcdevelopment.github.io/baseline/selfie-stick/>

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **CG-1** — Bring the camera plugin into the repo so the capture half runs from a baseline
  checkout. Start from `handoffs/valheim-camera-proof/` in the archive. Done when the plugin
  builds from a checkout of this repo and a capture run completes without reaching into the
  archive. This is the claiming task for this piece.
- **CG-2** — Resolve prefab hashes to names. Right now a structure's dominant material reads
  `hash:538325542` instead of `wood_wall`, because the offline name table holds 617 *item* names
  and no building pieces. Done when a one-time in-game dump of `ZNetScene`'s prefab table lands as
  a committed lookup and the scanner reports a material name.

What a useful reply looks like:

- What you ran, against what world, on what setup.
- What actually happened — errors pasted verbatim, not summarized.
- What you expected instead.
- If you got frames out: how many were actually worth keeping. The machine scoring the photographs
  is a filter, not a critic, and I would like to know how wrong it is.

Worth knowing before you point this at a server: it photographs other people's builds. The scan
attributes real coordinates to real builders, so the scan output stays out of the repo, every
record it produces is marked unpublished by default, and the public write-up withholds coordinates
and builder IDs throughout. Ask before publishing a gallery of a world you do not own.
