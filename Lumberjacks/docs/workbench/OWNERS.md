# OWNERS.md — Community Workbench ownership ledger

This file is the canonical record of who owns what across every tool in the Community Workbench
catalog (`workbench.json`). The catalog page's ownership badges are meant to read from this file —
if a claim isn't recorded here, it isn't real yet, no matter what got said in a Discord thread.

**Append-only.** A correction or a step-back is a new dated entry, not an edit to an old one — the
history stays intact on purpose, the same way this repo's roadmap journal works. Never delete or
rewrite a past entry.

The ladder is explained in plain language in the pinned Discord post
(`discord/05-pinned-how-this-works.md`). This file is the ledger that backs it, not a replacement
for it.

## The project operator

Where the catalog page or this ledger says **the project operator**, that is Derek
(`djcdevelopment` on GitHub, the operator account on the project Discord). The operator runs the
live services, holds final approval on pull requests, and is the person who confirms stage-4
ownership below. That authority comes from operating the infrastructure and owning the
repositories — and its decisions land here as dated entries like every other claim.

## The ladder

| Stage | Name | What you did | What you get | Recorded how |
|---|---|---|---|---|
| 0 | Curious | Read a tool's one-pager or thread and decided whether it's worth your evening. | A straight answer about what runs, what doesn't, and what it would cost you. Reading owes nobody anything. | Nowhere. No sign-up, no list. |
| 1 | Ran it | Ran the tool locally and posted what happened in its thread — including the part where it broke. | Named in the thread. A report that it failed is worth exactly as much as a report that it worked. | The tool's Discord thread. |
| 2 | Fixed one thing | Completed one of the first tasks listed on a tool's thread. | Credited here; the tool's ownership state moves from **unclaimed** to **trying**. | An entry below, stage 2. |
| 3 | Contributor | Landed two changes on a tool, or revived a recoverable piece back into something that runs, solo. | Code access to that one piece (not the whole repo) and triage rights on its thread. State moves to **claimed**. | An entry below, stage 3. |
| 4 | Owner | Sustained the contribution over time, and the project operator agrees you're the one holding it. | Sets direction for that piece. Can say no to a change, including from the operator. State moves to **owned**. | An entry below, stage 4. |

Stepping back from a claim at any stage is one message in the tool's thread — no explanation
required. It gets recorded as a new entry here too (see format below), moving the tool back toward
**unclaimed**.

## Entries

Append one block per event, oldest first. Do not edit or delete a past entry.

Format:

```
### <date> — <person> — <tool-id> — stage <n>
- evidence: <link(s) to the thread post, commit, or PR that backs this>
- notes: <anything that doesn't fit the format — free text>
```

Valid tool ids (must match `workbench.json`): `quest-picker`, `steward-view`,
`community-telemetry`, `steam-join`, `mcp-mod-channel`, `camera-gallery`,
`quest-submission-bridge`, `quest-lab`.

<!--
EXAMPLE ENTRY — not a real claim. Shown only to illustrate the append format; delete this comment
block once a real entry exists, or leave it as a reference — either is fine, it's inert either way.

### 2026-08-03 — ExampleUser — quest-picker — stage 2
- evidence: https://discord.com/channels/EXAMPLE/EXAMPLE/EXAMPLE
- notes: Completed QP-1 (wrote gm-template-example.json) and validate.py accepted it on the first
  try after one schema fix. Tool state moves unclaimed -> trying.
-->

*(No real entries yet.)*

## Current holders

| Tool ID | Tool | Holder | Stage | Since | Thread |
|---|---|---|---|---|---|
| `quest-picker` | Quest picker + absorption engine | unclaimed | — | — | 01 |
| `steward-view` | ComfyStewardView | unclaimed | — | — | 02 |
| `community-telemetry` | Community telemetry | unclaimed | — | — | 03 |
| `steam-join` | Steam self-service join | unclaimed | — | — | 04 |
| `mcp-mod-channel` | MCP mod channel | unclaimed | — | — | *(no thread yet — dev-only tool; raise interest in 05)* |
| `camera-gallery` | Camera flythrough → gallery | unclaimed | — | — | 06 |
| `quest-submission-bridge` | Quest submission → review bridge | unclaimed | — | — | 06 |
| `quest-lab` | Quest Lab turnkey package | unclaimed | — | — | 13 |

All eight are unclaimed as of this writing. That's a description of where the catalog is today,
not a mark against anyone — see the announcement post's closing note.
