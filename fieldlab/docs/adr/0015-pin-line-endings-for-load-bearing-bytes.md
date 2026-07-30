# ADR 0015 — Pin line endings for bytes that are hashed or parsed elsewhere

- **Status:** Accepted (2026-07-30)
- **Rung:** cross-cutting — repo hygiene, publish/verify integrity, Linux-destined infra

## Context

The live Community Workbench on AM4 was recorded in the handoff as still serving a stale
pre-review render, with a republish owed by the operator. It was not stale. The served page was
byte-exact with the committed render.

The false reading came from the verification gate itself.
[`workbench-verify-live.mjs`](../../../Lumberjacks/scripts/workbench-verify-live.mjs) reads the
local HTML as a **raw Buffer** and sha256's it against the live `X-Workbench-Sha256` header. The
repo had **no `.gitattributes`**, so a Windows checkout with `core.autocrlf=true` wrote a CRLF
working copy. Measured:

| | sha256 |
|---|---|
| raw CRLF working copy | `2bb23be9…` |
| LF-normalized bytes | `976f51cc…` |
| served by AM4 | `976f51cc…` |

**The gate failed against a deployment that was byte-correct.** After pinning line endings and
refreshing the working tree, `npm run workbench:verify-live -- --post-publish` returned **PASS —
69 checks, 0 failed, 0 warnings.**

This is the second appearance of the same root cause in two days. The previous session's retro
recorded `L-2026-07-29-1` — "prove bytes with shas, never with narrative", naming baseline's
`autocrlf` smudge as the hazard and `* -text` as the guard — but captured it as a **practice note
inside one workbook**, scoped to one copy operation. Nothing generalized it, so it recurred
somewhere else the next day.

A second, latent instance was already in flight: the P7 systemd unit and `bootstrap.sh.tftpl`
committed hours earlier ([ADR 0014](0014-boot-must-converge-or-say-so.md)) ship to Linux, where a
carriage return in `ExecStart=` is passed through as a literal and a CRLF shebang fails outright.
Those survived only because `autocrlf=true` normalizes on commit — a property of one contributor's
git config, not of the repository.

## Decision

**Bytes whose exact value is load-bearing get their line endings pinned in `.gitattributes`, at the
repo, not left to any contributor's git configuration.**

Two classes qualify:

1. **Generated artifacts that are hashed and published** — `*.html`. Their sha256 is compared
   against a live server header; a line-ending difference is indistinguishable from a bad deploy.
2. **Files parsed on Linux** — `*.sh`, `*.sh.tftpl`, `*.service`, `Caddyfile`, `*.yml`/`*.yaml`.
   A carriage return there is a syntax error or a silently corrupted argument.

Binary and hash-pinned artifacts (`*.zip`, `*.dll`, images) are marked `binary` so nothing
transforms them at all.

Corollary, and the part that generalizes past line endings: **when a verification gate fails,
establish whether the artifact is wrong or the gate is wrong before acting on it.** For a byte
comparison that is one command — `tr -d '\r' < file | sha256sum`.

## Consequences

- The published-artifact gate can pass on a Windows checkout. Before this it could not, which is
  worse than a gate that is merely absent: a gate that fails on correct input trains its operator
  to ignore it, and the recorded response here was to schedule an unnecessary republish.
- The P7 systemd unit and shell scripts are now protected by the repo rather than by luck.
- **Stored blobs were already LF** — `git add --renormalize .` staged nothing. This changes only
  what a checkout writes into the working tree, so it carries no history rewrite and no risk to
  existing commits.
- A lesson captured as a local practice note did not prevent recurrence. Cross-cutting hazards
  belong in a repo-wide mechanism, not in the document of whoever hit them first. That is the
  reason this is an ADR and not a third workbook paragraph.
- **Not addressed:** `workbench-verify-live.mjs` still hashes a raw Buffer, so it remains sensitive
  to any future line-ending drift. Normalizing inside the script was rejected as papering over the
  real invariant — published bytes should *be* LF, not merely compare equal after normalization.

## Related

- [0009](0009-verify-against-an-independent-source.md) — a check that reads its own output is not a
  check. This is the neighbouring failure: a check that reads an independent source but compares it
  against a locally-mangled copy.
- [0014](0014-boot-must-converge-or-say-so.md) — same session, same shape: a signal that reported
  the opposite of reality.
- `L-2026-07-29-1` in [`SESSION-RETRO-2026-07-29.md`](../../retro/SESSION-RETRO-2026-07-29.md) —
  the narrow capture this ADR generalizes.
