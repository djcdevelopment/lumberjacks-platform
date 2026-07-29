# Workbench Discord provisioning

Config-as-code for the `#workbench` forum on the community server. One script keeps the
channel, its tag taxonomy and its opening posts equal to what is checked in here.

**Setup and full walkthrough:**
[`Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md`](../../../Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md)

```bash
python tools/workbench/discord/workbench_discord.py check       # repo-only, no token
python tools/workbench/discord/workbench_discord.py plan        # dry run + approval receipt
python tools/workbench/discord/workbench_discord.py apply --yes # converge (writes to Discord)
python tools/workbench/discord/workbench_discord.py export --out ../workbench-exports
python tools/workbench/discord/workbench_discord.py self-test
```

| File | What it is |
|---|---|
| `workbench_discord.py` | The whole tool. Standard library only. |
| `provision.json` | Which seed file becomes which post, its tag, and its `workbench.json` tool. |
| `provision-state.json` | Written by `apply`: thread ids and URLs. Feeds the `discussion.href` fill. |
| `receipts/` | Dry-run receipts. `plan` writes one every time. |

The tag taxonomy and the post-guidelines text are **not** duplicated in `provision.json`
— they are parsed out of `Lumberjacks/docs/workbench/discord/07-forum-tags-setup.md`, so
the doc Derek reads stays the single source of truth. If that doc stops parsing to
exactly 8 tags (4 member-facing, 4 status), the tool fails loudly instead of provisioning
a taxonomy nobody wrote.

## The rules this code is built around

- **Structure, never conversation.** Every message body is a seed file, verbatim. No
  replies, reactions, DMs, or mentions. There is no free-text message argument anywhere.
- **The announcement is not postable from here.** `00-announcement.md` is on a hardcoded
  denylist that config cannot shrink.
- **Nothing member-visible without approval.** `plan` never writes; `apply` needs `--yes`
  and can be pinned to the plan hash on the receipt Derek read.
- **No always-on process.** A batch CLI. No gateway connection, no daemon.
- **No operator-fleet dependency.** Standard library, no model calls. See
  `docs/baseline-vision-and-boundary.md`.
- **Credentials live outside the repo.** The token loader refuses any path inside it.
