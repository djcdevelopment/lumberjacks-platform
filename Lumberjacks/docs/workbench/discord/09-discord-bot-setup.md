# Discord bot setup (one-time)

One bot, three jobs, all of them plumbing:

1. **Provision the forum from this repo** — create `#workbench` as a Forum channel with
   the 8-tag taxonomy in [`07-forum-tags-setup.md`](07-forum-tags-setup.md), open the six
   posts from the seed files, pin the guideline post.
2. **Keep the posts honest** — when a seed file changes in the repo, show the diff and
   update the live post after you approve it.
3. **Collect feedback** — export forum threads into the JSON that
   `tools/workbench/distill_feedback.py` reads.

Everything runs through one script:
[`tools/workbench/discord/workbench_discord.py`](../../../../tools/workbench/discord/workbench_discord.py).
Standard library only, no install step, no dependency on the operator's local model
fleet, and nothing left running between sessions — you run it, it converges, it exits.

## What this bot will never do

It does structure. It never does conversation. There is no code path that sends a
sentence nobody wrote in this repo: every message body is a seed file, rendered verbatim.
It never replies, reacts, DMs, or mentions anyone (`allowed_mentions` is empty on every
write). Replies to the community come from you, on your batch rhythm.

`00-announcement.md` is on a hardcoded denylist. No flag posts it, because announcing is
a decision, not a provisioning step. Posting it stays a manual action (DEREK-BATCH-1
item 10). Making the bot able to do that would be a code change plus a checklist item.

It also never deletes. A tag it doesn't recognise, a post you wrote by hand, a message
that would have to disappear for content to shrink — each is reported and left alone.

## 1. Create the application

1. [Discord Developer Portal](https://discord.com/developers/applications) → **New
   Application** → name it → create.
2. **Bot** → **Privileged Gateway Intents** → enable **Message Content Intent**. Without
   it every exported message comes back with empty `content`, so job 3 silently produces
   nothing. Leave the other toggles off; permissions are granted at invite time.
3. **Bot → Token → Reset Token**, confirm, copy immediately — it is shown once.

## 2. Put the token outside this repo

The token is a password. **It must live outside this repository.** A credential in the
working tree is one `git add -A` away from being committed, and this repo's automation
commits and pushes `main` on its own — so the script simply refuses to read a token from
any path inside the repo, and `.gitignore` blocks `*.token` / `*.env` as a backstop.

Where the script looks, in order:

1. `$env:WORKBENCH_DISCORD_TOKEN`
2. `--token-file <path>`, or the file named by `$env:WORKBENCH_DISCORD_TOKEN_FILE`
3. `%USERPROFILE%\.baseline\workbench-discord.token`
4. `%USERPROFILE%\.baseline\discord.env`

**Either file format works.** A file holding nothing but the token is fine, and so is an
env-style line — `KEY=<token>`, `DISCORD_TOKEN=<token>`, `export TOKEN=<token>`, quoted or
not, with or without a BOM or CRLF endings. If a file holds several keys, name the token's
line one of `WORKBENCH_DISCORD_TOKEN`, `DISCORD_BOT_TOKEN`, `DISCORD_TOKEN`, `BOT_TOKEN`,
`TOKEN`, `KEY`.

Windows PowerShell 5.1, so two commands (`&&` is a parser error in this shell) and
`-Encoding ascii` deliberately — PS 5.1's `utf8` writes a BOM, and while the reader now
strips one, not every tool does:

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\.baseline" | Out-Null
```

```powershell
Set-Content -Path "$env:USERPROFILE\.baseline\discord.env" -Value 'KEY=PASTE_TOKEN_HERE' -NoNewline -Encoding ascii
```

Then prove it works before anything else:

```powershell
python tools\workbench\discord\workbench_discord.py whoami
```

It reports the bot's identity, whether it can see the server, and whether `#workbench`
exists yet — and if the bot has not been invited, it prints the exact invite URL for you.
Read-only; it writes nothing.

## 3. Invite it with the minimum permissions

```powershell
python tools\workbench\discord\workbench_discord.py invite --app-id YOUR_APP_ID
```

That prints the permission set and the exact URL. The integer is **326417583120**:
Manage Channels, View Channels, Send Messages, Read Message History, Manage Threads,
Create Public Threads, Send Messages in Threads. No Administrator, no Manage Roles, no
Manage Server, no Mention Everyone, no moderation bits.

Manage Channels is the only broad one, and it is there so the bot can create the forum
channel and edit its tag list. **Tighter alternative:** create the empty `#workbench`
Forum channel yourself first, invite the bot without Manage Channels, then grant Manage
Channels to the bot on that one channel in its permission settings. The script works
either way — it adopts an existing channel by name.

After it joins: open the `#workbench` channel settings → **Permissions** → add the bot,
and deny **View Channels** for it at the server level so it can only see this one
channel. It has no reason to read anything else.

## 4. Provision the forum

```powershell
python tools\workbench\discord\workbench_discord.py check
```

Repo-only sanity pass — no token, no network. Prints the taxonomy it parsed out of the
07 doc, the six posts, their tags, and any post that cannot be published yet.

```powershell
python tools\workbench\discord\workbench_discord.py plan
```

Reads the live server, computes the difference, prints it, and writes an approval receipt
under `tools/workbench/discord/receipts/`. **Nothing is written to Discord by `plan`.**
Add `--offline` to predict against an empty server without a token — that is how the
pre-approval receipt in that folder was produced.

Read the receipt. If it is what you want in the server:

```powershell
python tools\workbench\discord\workbench_discord.py apply --yes --expect-plan <hash-from-the-receipt>
```

`--yes` is required; without it the script prints the plan and refuses. `--expect-plan`
makes it refuse if anything changed since the receipt you read — drop it if you don't
care. Thread ids and URLs land in `tools/workbench/discord/provision-state.json`, which
is what fills `discussion.href` in `workbench.json` later.

Re-running is safe and is the point: `apply` converges. A tag someone deleted comes back,
an edited pin is restored to the repo text, a missing post is recreated. A run with
nothing to do says so and exits.

### The catalog links gate

Seeds `01`–`04` carry `<ONEPAGER-URL>` and `<ACCESS-URL>`. Those resolve from
`workbench.json` — the one-pager is the tool's anchor on the catalog page, the access
link is its `access.href` — but only once the site is live. Until then those four posts
are **blocked**, so the bot cannot put a literal `<ONEPAGER-URL>` in front of the
community, and cannot link a page that 404s.

Before the `/workbench` deploy you can still provision the channel, the tags, the pinned
guideline post and the recoverable-pieces post. After the deploy, either set
`site_base_url` in `provision.json` or pass it once:

```powershell
python tools\workbench\discord\workbench_discord.py --site-base-url https://comfy-p7.duckdns.org plan
```

and the remaining four unblock.

## 5. Content maintenance

When a seed file changes, run `plan` again. It diffs the live post against the repo and
lists what would be edited; `apply` performs the edit. Two limits worth knowing:

- **A bot can only edit its own messages.** If a post was pasted by hand, the script
  reports it as unmaintainable rather than pretending. That is the reason to let the bot
  create the posts in the first place.
- **Content can grow, not shrink.** Adding paragraphs appends a message; removing enough
  text that a message would disappear is reported and left for you, because deleting a
  posted message is not something a script should decide.

Long seeds are split across several messages in the same thread on paragraph boundaries
(Discord caps one message at 2000 characters). No "(1/2)" markers — that texture is
exactly what this is avoiding.

## 6. Feedback export → the candidate journal

```powershell
python tools\workbench\discord\workbench_discord.py export --out ..\workbench-exports
python tools\workbench\distill_feedback.py --export-dir ..\workbench-exports
```

`export` writes one DiscordChatExporter-shaped JSON file per thread — the same shape the
distiller already reads, so nothing downstream changes. Messages the bot itself posted
are left out: they are seed files this repo already holds, and the distiller's keyword
heuristics would otherwise file our own "errors pasted verbatim" line as a bug report.

Keep the export directory outside the repo (or gitignored). It contains real display
names next to what people said — the same reason
`tools/workbench/candidate-issues-README.md` calls the candidate journal internal-only.

New candidates land in `Lumberjacks/docs/workbench/candidate-issues.jsonl` for your
weekly skim; anything by you or already recorded is skipped.

### DiscordChatExporter still works

[DiscordChatExporter](https://github.com/Tyrrrz/DiscordChatExporter) remains a valid way
to produce the same input, and is the better tool if you want HTML transcripts or a whole
guild at once. The same bot token works:

```powershell
DiscordChatExporter.Cli exportguild -t "BOT_TOKEN" -g GUILD_ID -f Json --include-threads all -o ./exports/
```

`-f Json` is required — the distiller reads that shape only. Note that DCE exports the
bot's own posts too, so a DCE-fed run will surface a few candidates from our own seed
text; dismiss them once.

## Finding IDs

**Settings → Advanced → Developer Mode**, then right-click a server or channel for **Copy
Server ID** / **Copy Channel ID**. The guild id is already in
`tools/workbench/discord/provision.json`.

## When something goes wrong

Run `whoami` first whenever anything looks wrong — it separates "bad token" from "not
invited" from "cannot see the channel" in one call.

| Symptom | Cause |
|---|---|
| `error: no bot token` | Step 2 — env var unset and no file at any searched path. The error lists every path it tried. |
| `does not look like a bot token` | The file holds a placeholder, an application id, or a key name. It never echoes the value; it reports the length. |
| `HTTP 401` | Token was reset in the portal; copy the new one. |
| `whoami` says guild FAILED | The bot is not on the server. Open the invite URL it prints. |
| `HTTP 403 Missing Access` | The bot cannot see `#workbench`; add it in the channel's permission settings. |
| `HTTP 403 Missing Permissions` on create | Invited without Manage Channels — re-invite with the URL from step 3, or create the channel by hand. |
| Exported messages have empty `content` | Message Content Intent is off (step 1.2). |
| `expected 8 tags ... parsed N` | `07-forum-tags-setup.md` changed shape. Fix the doc or the parser — the tool refuses to guess a taxonomy. |
| Post reported as created by hand | A human pasted it. Delete it and let the bot recreate it, or keep maintaining that one by hand. |

`python tools/workbench/discord/workbench_discord.py self-test` runs the whole thing —
parsing, placeholder guard, chunking, provisioning, drift repair, export, and the
handoff into `distill_feedback.py` — against a simulated guild. No token, no network.
