# Quest Lab

A private-world mod that shows you, live, what Valheim is doing — and whether a
quest could actually fire on it. Then lets you write one and watch it fire.

## What it is

A client-only BepInEx plugin, `network/mod/ComfyQuestLab`. Three surfaces, one
window (`F6`):

1. **What just happened** — a live console of what the game just did, one row per
   event, each ending in the honest verdict: a stable bindable event name, or
   *diagnostic only — never bindable*. That boundary is the reason the tool exists.
2. **Spellbook** — a page per rune school: what it covers, something to go and try,
   and the trap. Eight schools, and the same rune is the console's filter, so
   learning the book teaches the console for free.
3. **Quests** — your own quest files, each with an armed verdict, why, its cooldown,
   how many times it fired, and any advice. Plus the last event the matcher was
   actually handed, which is how you find out why something didn't fire.

`lab_setup` (typed into Valheim's own console, `F5`) writes a starter quest file,
safely removes any marked old build, raises a ground welcome camp, and builds a fresh
canopy-clear black-marble course: eight rune monuments, 10 m halls, 9 m hub-to-station
walks, and each target/tool at its point of use. `lab_reload` re-reads your quest files and tells
you what changed. `lab_target` still puts a fresh practice target in front of you for
quick one-offs.

The web version of the spellbook is [`/questlab`](https://am4.tail8e749c.ts.net/questlab).

## What it is NOT

**Not a quest editor, and not a submission tool.** You edit JSON in a text editor;
the lab reads it and tells you the truth about it. Turn-in still belongs to the
guild's Discord bot and the shipping mod's evidence flow.

**Not the shipping mod.** It never changes the game — every hook is a postfix that
reads and records, and a postfix that throws is swallowed, because a patch that
throws takes Valheim's damage path with it. It does nothing at all on a dedicated
server (detected in `Awake`, before a single patch is applied). Uninstall it and
nothing about your game changes.

**It does not read the shipping mod's quest file.** Its own quests live under
`comfy-quest-lab/quests/`, and it never touches `comfy-network-sense/`.

## Status

`local-only`. The mod works: the gallery builds and stands, the console fires, the
quest lane loads and evaluates. What has been **witnessed in a live session** is
narrower than what is wired — see "What's rough".

## Run it in about 10 minutes

1. Download the zip from [`/workbench/downloads/quest-lab`](https://am4.tail8e749c.ts.net/workbench/downloads/quest-lab)
   and check its SHA-256 against what the Workbench card claims.
2. Drop `ComfyQuestLab.dll` into `Valheim/BepInEx/plugins`. That is the whole install.
3. Launch a **private, single-player** world.
4. Press `F5` (Valheim's console) and type `lab_setup`.
5. Press `F6` (the lab's own panel). Different key — this is the usual first stumble.
   The panel owns the mouse while open; use its `−` / `+` controls to persistently tune
   the whole grid between 65% and 200% for windowed, 1080p, or 4K play.
6. Punch a tree. Watch the row appear, and read its third line.
7. Open the **Quests** tab. Edit
   `BepInEx/config/comfy-quest-lab/quests/starter.json`, then run `lab_reload`.

## What you'll see

The starter file holds two quests that disagree with each other on purpose:

| | |
| --- | --- |
| `first_blood` — `kill` / `Greyling` | **armed.** Kill the Greyling under the combat monument. |
| `punchwood` — `hit` / `tree_or_bush` | **armed.** `hit` remains a compatibility alias for creature and resource damage. |

Killed it already? `lab_target` puts a fresh one in front of you, and `lab_target <school>` does
the same for any of the eight. You never have to go hunting for the thing your quest is about —
that is the entire reason the gallery exists.

All eight schools route stable creator events into the exact `QuestTriggerEvaluator`
source shared with ComfyNetworkSense. The runtime covers all 86 practical atlas signatures:
57 safe signatures normalize to 34 bindable events, while low-level witnesses appear only
under the diagnostic profile and cannot complete a quest. Local/RPC and overload alternatives
share an action key so one action cannot double-complete a zero-cooldown quest.

**The name a quest matches on is not the prefab name.** The matcher compares against
the creature's `m_name`, a localization token. For `Neck` the token contains the
prefab name and typing the obvious thing works. For `Greydwarf_Elite` the token is
`$enemy_greydwarfbrute` — they share nothing, so such a quest never fires and never
errors. The console shows both names whenever they disagree, and the advisor says
so outright.

## What's rough

- **The exact r10 presentation cut still needs its final live pass.** An exact-r4 OMEN
  suite already witnessed 8/8 schools and completed 8/8 ordinary example quests with
  zero same-action doubles. r10 changes the zoomable panel and compact physical course,
  so the final release claim waits for those suites and Derek's visual choice on r10.
- **`QuestViewLoader.Parse` throws on the first bad quest in a file**, so a file with
  three problems reports one. The lab says so rather than implying you are done.
- **The parser is regex-based, not a JSON validator.** A trailing comma can silently
  drop a quest. The lab compares parsed count against `"quest_id"` occurrences and
  flags a disagreement, which catches most of it, but a malformed file can still
  surprise you.
- **Most course supplies remain intentional drops.** Tools, arrows, materials, and fuel glint
  beside the interaction that consumes them. Welcome food is the exception: the verified
  vanilla item ZDO and exact visual RPC mount it on three picnic-table item stands.
- **`lab_reload` clears cooldowns**, unlike the shipping mod, where a 60 s cooldown
  persists for the session. Deliberate — waiting a minute to retest an edit is the
  flow `lab_reload` exists to protect — but it means the lab is *slightly* more
  permissive than live between reloads. `[Quests] questCooldownSeconds` matches the
  shipping default if you want the real feel.

## First tasks

- **QL-1 — Try the compact course.** Done when: you run `questlab_batch prepare
  all-schools`, follow its short circuit, and post whether every station was obvious
  without extra instructions. Event coverage is witnessed; creator usability is the test.
- **QL-2 — Author a quest that fires.** Done when: you edit `starter.json` into a
  quest of your own, `lab_reload` reports it armed, and you make it fire. Post the
  file — a second worked example is worth more than any amount of documentation.

## Where to talk about it

Its [Discord thread](https://discord.com/channels/1531911987074957442/1531926985314668635).

## License & privacy

BUSL-1.1 with the community-steward safe harbor, converting to AGPL-3.0-only. See
[`docs/legal/LICENSING.md`](../../../../docs/legal/LICENSING.md).

**Privacy: it runs entirely locally and sends nothing anywhere.** There is no
network call in the mod. Your quest files, the event ring, and the gallery all live
on your own machine, and the ring is in memory only. If you paste a console
transcript into the thread, that is you sharing it, not the lab.
