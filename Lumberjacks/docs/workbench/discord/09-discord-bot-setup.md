# Discord bot setup for feedback exports (one-time)

A read-only bot whose only job is letting you run
[DiscordChatExporter](https://github.com/Tyrrrz/DiscordChatExporter) against the
workbench forum category, producing the `--export-dir` input for
`tools/workbench/distill_feedback.py`. The bot never posts; the distiller never
calls Discord itself, it only reads files DiscordChatExporter already saved.

## Create and invite the bot

1. [Discord Developer Portal](https://discord.com/developers/applications) > **New
   Application** > name it > create.
2. **Bot** > **Privileged Gateway Intents** > enable **Message Content Intent**
   (otherwise every export comes back with empty `content`). Leave every other
   permission toggle on that page off; permissions are granted at invite time.
3. **Bot > Token > Reset Token**, confirm, copy immediately -- shown once. Treat
   it like a password; it is not committed anywhere.
4. **OAuth2 > General**, copy the **Application ID**, then open (replacing
   `YOUR_APP_ID`; `66560` = View Channels + Read Message History and nothing
   else -- "read messages" is the same bit as View Channels today, no separate
   checkbox exists for it):
   ```
   https://discord.com/oauth2/authorize?scope=bot&permissions=66560&client_id=YOUR_APP_ID
   ```
5. Once it joins, open the workbench forum category's settings > **Permissions**
   > add the bot > confirm only View Channels and Read Message History are
   checked. Deny View Channels for the bot everywhere else so it only sees that
   category.

## Find your IDs

**Settings > Advanced > Developer Mode**, then right-click a server or a
channel/thread for **Copy Server ID** / **Copy Channel ID**.

## Export, then distill

```
# Whole guild, including forum threads, as JSON:
DiscordChatExporter.Cli exportguild -t "BOT_TOKEN" -g GUILD_ID -f Json --include-threads all -o ./exports/

# One channel or thread by ID instead:
DiscordChatExporter.Cli export -t "BOT_TOKEN" -c CHANNEL_ID -f Json -o ./exports/

# List channel/thread IDs first, if you don't have them:
DiscordChatExporter.Cli channels -t "BOT_TOKEN" -g GUILD_ID
```

`-f Json` is required -- the distiller only reads DiscordChatExporter's JSON
export shape, not HTML or plain text. Then:

```
python tools/workbench/distill_feedback.py --export-dir ./exports
```

New candidates land in `Lumberjacks/docs/workbench/candidate-issues.jsonl` for
your weekly skim; anything by you or already recorded is skipped.
