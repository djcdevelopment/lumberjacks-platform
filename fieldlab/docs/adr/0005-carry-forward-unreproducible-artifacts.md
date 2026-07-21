# ADR 0005 — Carry forward an unreproducible release artifact rather than rebuild it

- **Status:** Accepted (2026-07-21)
- **Rung:** M5 / baseline cutover step 6; binds every release cut that re-cuts an already-tested release

## Context

Step 6 of the baseline cutover required re-provisioning `comfy-lumberjacks-p7` from the merged repo
and proving it reproduces what was world-tested in `m5-recipients-20260720-r1`. That release's mod
DLL hashes `035faa8793114c75…`, and the v2 manifest recorded it as the shipped artifact.

Rebuilding the mod at the baseline commit produced `2d43f8a957a44e2d…` instead — different bytes,
identical source. The cause was already documented as a suspicion in `New-ReleaseCut.ps1`'s closing
notes ("plan risk 12") and is now confirmed: the .NET 8 SDK's implicit source-control tasks embed the
git HEAD sha in the portable PDB, and the PDB checksum rides in the DLL's debug directory. The
artifact's identity bytes therefore change on **every commit**, with unchanged source. No checkout of
the release commit can ever rebuild the DLL that shipped, because the shipped DLL embeds the sha of
that commit's *parent* (builds happen before the release commit exists).

The gateway image was unaffected in practice — `.dockerignore` excludes `.git`, so the build stage
cannot query the repository — and the three sibling service images were built fresh at the baseline
commit and are reproducible from it.

Two options: rebuild everything at the baseline commit and ship a self-consistent but **untested**
mod, or carry the tested artifact forward and admit the manifest is mixed-provenance.

## Decision

**Carry the original world-tested artifacts forward unchanged, and record the reproducibility gap
explicitly in the manifest.** The v3 manifest for `m5-recipients-20260720-r1` pins:

- `mod.clean_build_sha256 = 035faa87…` and `gateway.image_id = sha256:69e025e8…` — the **original**
  m5 artifacts, byte-for-byte, built before the monorepo cutover.
- `eventlog` / `progression` / `operatorapi` — **newly built** at `source.baseline_commit`
  `807769a9…`, and reproducible from it.

The manifest carries an `artifact_provenance` block naming the two pre-cutover commits, an
`equivalence` statement (both landed in `baseline` unmodified, so the tree is source-equivalent even
where the binaries predate it), and a `build_contract.mod_repeatability` field that states the
rebuild hash observed and why it differs. `build_contract.deterministic` is set to `false`.

The release bundle is the transport for this: it ships the artifact itself, so an unreproducible
binary is still verifiable by hash at every hop.

## Consequences

- **The release stayed the thing that was tested.** The re-provisioned VM ran mod `035faa87…` at both
  the runtime and fallback paths and passed all eight §9 acceptance criteria. A rebuilt mod would have
  invalidated the world test the exercise existed to reproduce.
- **The manifest is honest rather than tidy.** A reader can see exactly which artifacts the
  `baseline_commit` explains and which it does not. This is preferable to a self-consistent manifest
  that quietly describes different bytes than the ones that shipped.
- **This is a deferral, not a fix.** The underlying defect is real: a build system whose output hash
  depends on commit identity cannot be audited by rebuild. Two known remedies exist and neither was
  chosen here — pin `EnableSourceControlManagerQueries=false` in the csproj (hash becomes a function
  of source alone, loses embedded provenance), or reorder cuts to commit-first-build-second (keeps
  provenance; a rebuild then needs the same origin URL). **That choice is still open** →
  `DECISIONS-PENDING.md`.
- **Bounded applicability.** This ADR licenses carrying an artifact forward when re-cutting a release
  that has *already been world-tested*. It does not license shipping unreproducible artifacts in a
  first cut; there, the artifact and the commit should agree.
- **Supply-chain caveat, stated plainly.** Trust in the mod DLL now rests on hash continuity from the
  original build plus the bundle's integrity, not on independent rebuild. That is weaker. It is
  accepted here because the alternative — shipping untested bytes to close a test — is worse.

## Related

`Lumberjacks/docs/roadmap/m5-recipients-build-candidate-v3.json` (the `artifact_provenance` and
`build_contract` blocks); `Lumberjacks/docs/roadmap/m5-v3-acceptance-receipt.json`;
`infra/gcp/p7/scripts/New-ReleaseCut.ps1` (the original plan-risk-12 note);
`retro/SESSION-RETRO-2026-07-21.md` lesson `L-2026-07-21-3`.
