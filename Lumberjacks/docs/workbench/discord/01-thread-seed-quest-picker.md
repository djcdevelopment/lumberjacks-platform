*Meta: forum thread title suggestion — "Quest picker". Paste everything below the divider as the
thread's opening post.*

---

**Quest picker + absorption engine**

What it is: your guild's real quest tracker, harvested into one self-contained web page. Open it,
check off the quests you're actually chasing, and it saves a small `quest-view.json` file to your
computer — your own personal list, nobody else sees it unless you use it further. The already-built
page is sitting in the repo, so opening it needs nothing installed, just a browser.

If you already run the ComfyNetworkSense mod, the file you save gets read by the mod too — that
part is live and tested end to end against real guild data. The one honest gap: there's no
packaged download yet. Getting your *own* guild's fresh data in means cloning the repo and running
a couple of Python scripts locally, not just opening a page.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **QP-1** — Write the missing `gm-template-example.json`. The config (`sources.json`) already
  points at this file — a disabled `rangers-example` entry — it's just never actually been
  written. Small task. Done when `validate.py` accepts it.
- **QP-2** — Run the harvester against your own guild's tracker export. Done when a new catalog
  shows up in the picker and every anomaly the harvest flags has an explanation — none left
  unaccounted for.

What a useful reply looks like:

- What you ran (which task, on what — OS, browser, mod version if relevant).
- What actually happened — errors pasted verbatim, not summarized.
- What you expected instead.
