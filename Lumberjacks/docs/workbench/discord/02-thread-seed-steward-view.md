*Meta: forum thread title suggestion — "ComfyStewardView". Paste everything below the divider as
the thread's opening post.*

---

**ComfyStewardView**

What it is: a separate little app (Java, runs on your own machine) that reads a Valheim world save
file — the `.db` — and turns it into a map you can click around: build density, portals, players,
containers, tombstones, signs, who owns what. It's not connected to the live server and it doesn't
watch anyone play. You point it at a copy of a save file and it parses locally, once.

It's already real — the public repo builds a working app, and it's been used to steward
high-player worlds. The honest gap: there are roughly twenty GM probe utilities bundled alongside
it, and they work, but today you'd have to read the source to know what each one answers. That's
first-in-line for someone to fix.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **SV-1** — Run it against a copy of any world save you've got (your own solo world is fine) and
  post one heatmap screenshot in the thread, with the command you actually used and anything that
  tripped you up.
- **SV-2** — Write the missing quickstart for the probe utilities: one line per probe saying what
  it answers, one copy-pasteable command. Done when a steward who's never opened the source could
  pick the right probe from that page alone.

What a useful reply looks like:

- What you ran, and roughly how big your save file was.
- What actually happened — errors pasted verbatim, not summarized.
- What you expected instead.

Worth knowing: run it on a *copy* of a save, never the live one, and share screenshots or
aggregates rather than raw dumps — the output can contain player names.
