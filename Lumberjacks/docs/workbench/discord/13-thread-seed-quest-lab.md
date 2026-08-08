*Meta: forum thread title suggestion — "Quest Lab". Paste everything below the divider as the
thread's opening post.*

---

**Quest Lab**

What it is: a mod for your own single-player world that shows you, live, what Valheim is actually
doing — and whether a quest could fire on it. Drop one DLL in, type `lab_setup`, and it raises a
compact black-marble course for you: eight rune monuments, wide halls, short spokes, and each
target/tool beside the interaction that uses it. Punch a tree and
you'll see a row appear with three lines. The third line is the whole point — it's the honest
answer to "can I build a quest on what I just did?"

Then it lets you write one. `lab_setup` also leaves you a starter quest file. Edit it, type
`lab_reload`, and the Quests tab tells you what changed and what will fire.

And you never go hunting. The starter quest targets the Greyling standing under the combat
monument, and `lab_target` puts a fresh one in front of you whenever you need another — for any
of the eight schools. Testing a quest twice should not mean walking across the map to find a
second thing to kill.

Honest status: local-only. All 86 practical atlas signatures are explicitly instrumented; 57 safe
signatures normalize into 34 stable creator events that the lab and ComfyNetworkSense evaluate
from the same source. Diagnostic-only mutations stay visible without ever completing a quest, and
local/RPC or overload witnesses share an action identity so one action cannot double-complete a
zero-cooldown quest. The starter's broad `hit` alias and ordinary `kill` trigger are both bindable.

An exact-r4 OMEN pass witnessed all eight schools and completed all eight example quests with zero
same-action doubles. The current r9 presentation/course cut still needs the same machine-readable
pass plus a final visual review; that is the honest remaining boundary.

It runs entirely on your machine, sends nothing anywhere, does nothing at all on a dedicated
server, and uninstalling it changes nothing about your game.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **QL-1** — Install it, run `questlab_batch prepare all-schools`, and follow the compact circuit.
  Tell us whether the target, tool, and next action were obvious at every rune without somebody
  coaching you. The event lane is witnessed; the creator experience is what this tests.
- **QL-2** — Edit `starter.json` into a quest of your own and make it fire. Post the file. A
  second worked example is worth more than any amount of documentation, and it'll tell us fast
  whether the Quests tab explains itself or whether you had to guess.

A trap worth knowing before you hit it: the name a quest matches on is **not** the prefab name you
see in-game. The matcher compares against the creature's internal name, which is usually a
localization token — `Greydwarf_Elite` is actually `$enemy_greydwarfbrute`, and they share no
letters, so that quest would never fire and never complain. The console now shows you both names
whenever they disagree. If you find another case where what the lab shows you and what actually
matches are different things, that's a bug and we want it.

What a useful reply looks like:

- Which school you tried and what you did in-game.
- What the console actually showed — pasted verbatim, including the third line of each row.
- What you expected instead, if it surprised you.
- Your Valheim version, if a seam reported itself unavailable in `questlab_seams`.

Two keys, because this catches nearly everyone: **F5** is Valheim's own console, where you type
`lab_setup` and `lab_reload`. **F6** opens the lab's window and hands it the mouse. Use the visible
`−` / `+` controls to persistently tune the whole panel from 65% to 200% for your resolution.
