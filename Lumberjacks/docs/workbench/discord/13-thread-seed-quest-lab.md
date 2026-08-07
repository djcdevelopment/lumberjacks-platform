*Meta: forum thread title suggestion — "Quest Lab". Paste everything below the divider as the
thread's opening post.*

---

**Quest Lab**

What it is: a mod for your own single-player world that shows you, live, what Valheim is actually
doing — and whether a quest could fire on it. Drop one DLL in, type `lab_setup`, and it raises a
practice ground for you: eight rune monuments, a station under each, an armoury. Punch a tree and
you'll see a row appear with three lines. The third line is the whole point — it's the honest
answer to "can I build a quest on what I just did?"

Then it lets you write one. `lab_setup` also leaves you a starter quest file. Edit it, type
`lab_reload`, and the Quests tab tells you what changed and what will fire.

Honest status: local-only, and there's one thing you should know before you spend an evening on
it. All eight schools are **hooked** — the lab can show you every one of them. But exactly one of
them can currently have a quest **bound** to it: a creature kill. A quest with a `hit` trigger
parses perfectly, reports no error at all, and can never fire. That's not a bug in the lab, it's
the state of the quest evaluator the lab shares with the live server mod — and making that visible
instead of letting you discover it the hard way is why this thing exists. The starter file ships
one of each so you can see the difference on your first launch.

Also honest: only the harvest school has ever been *witnessed* firing in a live session. The other
seven are patched and report as hooked, but nobody has actually watched one happen. That's what
QL-1 is.

It runs entirely on your machine, sends nothing anywhere, does nothing at all on a dedicated
server, and uninstalling it changes nothing about your game.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **QL-1** — Install it, run `lab_setup`, and pick one school that isn't harvest. Do the things
  its Spellbook page tells you to do, and tell us whether the rows showed up as the Tome predicted.
  Seven of the eight have never been seen by anyone; being the first person to witness one is a
  real contribution and takes about ten minutes.
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
`lab_setup` and `lab_reload`. **F6** opens the lab's window.
