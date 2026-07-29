# Wrap-up workbook — Discord forum provisioning

Derek's steps, in order. Everything else is already built and committed. Full reference:
[`09-discord-bot-setup.md`](../../../Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md).

Run commands from the repo root (`C:\work\baseline`). Everything here is **Windows
PowerShell 5.1** — `&&` is a parser error in this shell, so each command stands alone.

---

## A. One-time bot setup (~10 min, browser + one file)

- [ ] **A1.** [Developer Portal](https://discord.com/developers/applications) → **New
      Application** → name it → Create.
- [ ] **A2.** **Bot** → **Privileged Gateway Intents** → turn on **Message Content
      Intent**. (Without it, feedback exports come back with empty message text.) Leave
      the rest off.
- [ ] **A3.** **Bot → Token → Reset Token** → copy it. Shown once.
- [ ] **A4.** Save it outside the repo — two commands, `-Encoding ascii` on purpose
      (PS 5.1's `utf8` writes a BOM):
      ```powershell
      New-Item -ItemType Directory -Force "$env:USERPROFILE\.baseline" | Out-Null
      ```
      ```powershell
      Set-Content -Path "$env:USERPROFILE\.baseline\workbench-discord.token" -Value 'PASTE_TOKEN_HERE' -NoNewline -Encoding ascii
      ```
- [ ] **A5.** Copy the **Application ID** (OAuth2 → General), then:
      ```powershell
      python tools\workbench\discord\workbench_discord.py invite --app-id YOUR_APP_ID
      ```
      Open the URL it prints, pick the community server, authorize.
- [ ] **A6.** Server Settings → Roles → the bot's role → deny **View Channels** at the
      server level. It only needs to see `#workbench`, which it will get through the
      channel's own permissions.

## B. Provision the forum (tonight)

- [ ] **B1.** Confirm the plan against the live server:
      ```powershell
      python tools\workbench\discord\workbench_discord.py plan
      ```
      On an untouched server this reproduces plan hash **`6aba648cba55`** — the same one
      in the receipt you already read. A different hash means something already exists;
      read the new receipt before continuing.
- [ ] **B2.** Go live:
      ```powershell
      python tools\workbench\discord\workbench_discord.py apply --yes --expect-plan 6aba648cba55
      ```
- [ ] **B3.** Eyeball it in Discord: `#workbench` is a Forum, 8 tags present, "Require
      people to select tags" on, guidelines box filled, **How this works** pinned at the
      top, **Recoverable pieces** below it. Quest picker / StewardView / Community
      telemetry / Steam join are **deliberately absent** until the deploy — see C.

## C. After the `/workbench` deploy (batch item 7)

- [ ] **C1.** The four remaining posts carry catalog links, so they need the live site:
      ```powershell
      python tools\workbench\discord\workbench_discord.py --site-base-url https://comfy-p7.duckdns.org plan
      ```
      then `apply --yes --expect-plan <hash from that receipt>`.
- [ ] **C2.** Optional, so you stop passing the flag: set `"site_base_url"` in
      [`provision.json`](provision.json) to `https://comfy-p7.duckdns.org`.
- [ ] **C3.** Hand `tools/workbench/discord/provision-state.json` to an agent for the
      `discussion.href` fill (see the handoff block below).

## D. Then, whenever

- [ ] **D1.** Batch 2 — post `00-announcement.md` yourself. The bot cannot; that is on
      purpose.
- [ ] **D2.** Once threads have traffic:
      ```powershell
      python tools\workbench\discord\workbench_discord.py export --out ..\workbench-exports
      ```
      ```powershell
      python tools\workbench\distill_feedback.py --export-dir ..\workbench-exports
      ```
      Keep the export directory outside the repo — real names next to real quotes.
- [ ] **D3.** Edited a seed file? `plan` shows the diff, `apply` pushes it to the live
      post. Same two commands, always.

---

## Handoff block — paste this to the next agent

> The `#workbench` Discord forum is provisioned by
> `tools/workbench/discord/workbench_discord.py` (config-as-code from the repo's seed
> files; setup doc `Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md`).
>
> **What you need from it:** `tools/workbench/discord/provision-state.json`. Each entry
> has a `url` (the thread) and `workbench_tools` (the `workbench.json` tool ids that
> thread covers). Six posts cover seven tools — `recoverable` maps to both
> `camera-gallery` and `quest-submission-bridge`; `mcp-mod-channel` has no thread.
>
> **What to do with it:** set `discussion.href` per tool in
> `Lumberjacks/docs/workbench/workbench.json`, and `feedback.forum_href` to the forum
> channel URL. Then from `Lumberjacks/`: `npm run workbench:render` and
> `npm run workbench:check`. **Never hand-edit `workbench.html`** — it is generated.
>
> **Feedback direction:** `workbench_discord.py export` → `tools/workbench/distill_feedback.py`
> → `Lumberjacks/docs/workbench/candidate-issues.jsonl`. That journal is **internal
> only** — real display names next to quotes; never link or publish it, and never paste
> it into Discord (`tools/workbench/candidate-issues-README.md`).
>
> **Rules that bind you too:** the bot does structure, never conversation — no
> auto-replies, no generated messages to members, no chat presence. `00-announcement.md`
> is denylisted in code. Nothing HEARTH/Mechnet ships in or is required by any of this
> (`docs/baseline-vision-and-boundary.md`). Every non-merge commit needs a roadmap note
> (`Lumberjacks/AGENTS.md`); milestone **A7**.
