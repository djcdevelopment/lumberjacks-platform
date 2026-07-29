# Saved replies — starter set

*Set these up once in Discord (or keep this file open during a batch pass and copy-paste).
Each one is a complete answer to something the one-pagers already predict will recur. Add
one personalizing sentence when you use one — a verbatim paste twice to the same person
reads colder than you are. When a question recurs that isn't here, the reply you write
becomes both a new saved reply AND an "Already answered" line in that tool's one-pager.*

## SR-1 — Python / openpyxl missing (quest-picker)

> The picker kit needs Python 3.9+ and one package. If `python --version` works but the
> harvest step fails on an import: `pip install openpyxl` and re-run. Everything else in
> the kit is standard library on purpose — if a different import fails, that's a real bug,
> post the exact error.

## SR-2 — Where's the download

> Every tool's download (when one exists) is on the Workbench page — the card's access
> button. If a card says "not published yet," that's the honest state, not a broken link:
> the code exists but hasn't been packaged for cold-start yet.

## SR-3 — quest-view.json goes where

> `Valheim/BepInEx/config/comfy-network-sense/quest-view.json` — note the folder name. If
> you saw `comfy-control` anywhere, that's the old pruned path and the file will be
> silently ignored there (this bit us too; the docs are fixed).

## SR-4 — Can I get an invite to the server

> Not open yet — the roadmap's headline is honest about that ("volunteer platform not
> ready"). The alpha is a small fixed cohort while the netcode work is paused. When it
> widens, it'll be announced in this forum first — there's no waitlist to get onto, and
> asking twice doesn't move anything (I know, I know).

## SR-5 — Can you add <feature> to the mod

> Right now I'm not adding scope to the mod — the networking lane is deliberately paused
> and the focus is making what exists runnable by more people than me. If the idea's a
> good fit for one of the Workbench tools, post it in that tool's thread as a feature
> note; the ones that recur become first tasks someone can actually own.

## SR-6 — Why is the main repo private / can I see the code

> The archive repo (github.com/djcdevelopment/comfy) is public and holds the recoverable
> pieces. The live repo opens per-piece: reach Steward on a tool (two landed changes, or
> revive a pruned piece) and you get code access to that piece. That's the ladder working
> as designed, not a paywall — the whole path is in the pinned post.

## SR-7 — It didn't work (no details)

> Want to help me help you: paste (1) the exact command you ran, (2) the full error text
> — verbatim, even if it's long, (3) what you expected instead. "It broke" with those
> three things attached is one of the most useful posts this forum gets.
