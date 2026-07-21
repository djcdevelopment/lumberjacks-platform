# ADR 0007 — Choose prune signals after checking what the merge did to them; exclude proven-live code

- **Status:** Accepted (2026-07-21)
- **Rung:** repo curation; binds any future bulk prune of `baseline`

## Context

The July 2026 consolidation carried across everything both source repos held, including a large body
of content with no consumer in the merged program. A prune was wanted, with the explicit premise that
**over-pruning was acceptable** — both source repos still exist at `C:\work\comfy` and
`C:\work\lumberjacks`, and git history holds everything regardless.

The two signals a bulk prune normally reaches for both turned out to be *actively misleading here*,
and using either unqualified would have caused real damage:

- **Git-history staleness.** Useless. The subtree merges attribute all 1045 tracked files to the
  consolidation date, so every file reads "last touched 2026-07". There is no staleness gradient at
  all.
- **Basename-orphan detection** (a file whose name is never mentioned anywhere else). Meaningful for
  docs and scripts, catastrophic for source: 199 basenames were never mentioned, and **196 of them
  were live C#**, because C# is referenced by namespace and type, not filename. Handing that list to a
  pruning agent would have proposed deleting the code that builds production.

## Decision

Bulk prunes of this repo follow four rules.

1. **Exclude proven-live code from the prune surface entirely.** `Lumberjacks/src` and `network/mod`
   were never offered for review: that code builds the five images serving production, which is the
   strongest evidence of liveness available. Prune surface is documentation, scratch, evidence, and
   finished experiment material.
2. **Validate each signal against the repo's own history shape before trusting it.** Both signals
   above were tested and discarded on evidence, not intuition. Judgement fell back to content plus
   inbound-reference greps.
3. **Every deletion gets an adversarial second pass.** Each proposed `PRUNE` went to a skeptic that
   grepped the repo for inbound references and spot-read anything decision-record shaped (ADRs,
   release manifests, receipts, security notes, the only documentation of a production behaviour),
   defaulting to *uphold* because recovery is cheap. It overturned **62** verdicts and correctly
   predicted every downstream breakage (dangling links, a test that would fail).
4. **Budget a cross-zone reconciliation pass.** Per-zone agents cannot see each other, and produce
   incoherence *by construction*: generated `docs/repo-map/` outputs survived while their generator
   `tools/repo_activity.py` was pruned, and index READMEs survived pointing only at deleted files. A
   second pass removed 11 such orphans.

Additionally: **the reasons must outlive the files.** `Lumberjacks/docs/roadmap/prune-audit-20260721.json`
records the method, both rejected signals, and a per-file rationale for all 268 primary deletions.

## Consequences

- **279 of 1045 tracked files removed** (1045 → 766), 67,714 deletions, with the live pipeline
  verified intact afterwards: the v3 bundle still validates, the drill plan-only still resolves all
  four gated identities, `docker compose config` still resolves all five images, and `roadmap:check`
  passes.
- **Tests were made resilient rather than patched.** `tests/test_entrypoint_links.py` hardcoded nine
  entrypoint paths, five of which the prune removed; it now *discovers* them. Removing a directory no
  longer turns that test red for a reason unrelated to link integrity.
- **Recovery is a first-class part of the decision, not a consolation.** Deleting aggressively is only
  correct because both source repos survive. If either is ever archived or deleted, this ADR's premise
  weakens and the bar for future prunes should rise accordingly.
- **Cost.** 14 agents, ~987k tokens, 27 minutes, run through the HEARTH gateway to `gcp-gemini-pro` so
  the reading was done on near-free trial credit rather than frontier tokens. Cheap for the size of
  the question; not free.
- **Known residue, accepted.** Some surviving docs describe pruned material (e.g.
  `docs/quest-vertical-slice-architecture.md` expresses its dataflow in `handoffs/comfy-control-surface/`
  paths). Rather than rewrite them, the surviving indexes state plainly that the referenced source is
  gone and where it went.

## Related

`Lumberjacks/docs/roadmap/prune-audit-20260721.json`; `README.md` ("The July 2026 prune");
`retro/SESSION-RETRO-2026-07-21.md` lessons `L-2026-07-21-6` and `L-2026-07-21-7`;
memory `streamline-over-two-repo-ceremony`.
