# Quest Lab

A private-world mod that shows you, live, what Valheim is doing — and whether a
quest could actually fire on it. Then lets you write one and watch it fire.

## What it is

A client-only BepInEx plugin, `network/mod/ComfyQuestLab`. Three surfaces, one
window (`F6`):

1. **What just happened** — a live console of what the game just did, one row per
   event, each ending in the honest verdict: *a quest can be bound to this today*
   / *the world speaks, but no quest is listening yet* / *nothing binds a quest to
   this yet*. That third line is the reason the tool exists.
2. **Spellbook** — a page per rune school: what it covers, something to go and try,
   and the trap. Eight schools, and the same rune is the console's filter, so
   learning the book teaches the console for free.
3. **Quests** — your own quest files, each with an armed verdict, why, its cooldown,
   how many times it fired, and any advice. Plus the last kill the matcher was
   actually handed, which is how you find out why something didn't fire.

`lab_setup` (typed into Valheim's own console, `F5`) raises a practice gallery —
eight rune monuments, a station under each, an armoury, ~620 pieces — and writes
you a starter quest file. `lab_reload` re-reads your quest files without a restart
and tells you what changed.

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
6. Punch a tree. Watch the row appear, and read its third line.
7. Open the **Quests** tab. Edit
   `BepInEx/config/comfy-quest-lab/quests/starter.json`, then run `lab_reload`.

## What you'll see

The starter file holds two quests that disagree with each other on purpose:

| | |
| --- | --- |
| `neck_romancer` — `kill` / `Neck` | **armed.** Kill a Neck and it fires. |
| `punchwood` — `hit` / `tree_or_bush` | **not armed**, and nothing errors. |

That second one is the whole lesson. All eight schools are *hooked* — the lab can
show you every one of them. But `QuestTriggerEvaluator` matches `kill` triggers
only, so a `hit` quest parses perfectly, reports no problem, and can never fire.
Exactly one school can have a quest *bound* to it today. The Quests tab names
which, and why, per quest.

**The name a quest matches on is not the prefab name.** The matcher compares against
the creature's `m_name`, a localization token. For `Neck` the token contains the
prefab name and typing the obvious thing works. For `Greydwarf_Elite` the token is
`$enemy_greydwarfbrute` — they share nothing, so such a quest never fires and never
errors. The console shows both names whenever they disagree, and the advisor says
so outright.

## What's rough

- **Only harvest has been witnessed firing in a live session.** The other seven
  categories are patched and the seam roster reports them hooked, but no event from
  them has been seen with human eyes. The quest lane itself — the seed, a firing, a
  reload diff — is **verified by unit tests against the real contract, not yet in
  game**.
- **One school can fire a quest.** Not a defect in the lab; it is the state of
  `QuestTriggerEvaluator`, which the lab shares with the shipping mod rather than
  reimplementing. Widening it is a change to the shipping contract.
- **`QuestViewLoader.Parse` throws on the first bad quest in a file**, so a file with
  three problems reports one. The lab says so rather than implying you are done.
- **The parser is regex-based, not a JSON validator.** A trailing comma can silently
  drop a quest. The lab compares parsed count against `"quest_id"` occurrences and
  flags a disagreement, which catches most of it, but a malformed file can still
  surprise you.
- **Item stands in the gallery stay bare.** `SetVisualItem` is a registered RPC, not
  a callable method, so the gear is dropped on the floor beside them instead.
- **`lab_reload` clears cooldowns**, unlike the shipping mod, where a 60 s cooldown
  persists for the session. Deliberate — waiting a minute to retest an edit is the
  flow `lab_reload` exists to protect — but it means the lab is *slightly* more
  permissive than live between reloads. `[Quests] questCooldownSeconds` matches the
  shipping default if you want the real feel.

## First tasks

- **QL-1 — Try it and verify one school.** Done when: you run the lab, try the
  actions for one school, and post in the thread whether the in-game events fired as
  the Tome predicted. Seven of the eight have never been witnessed by anyone; being
  the first is a real contribution.
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
