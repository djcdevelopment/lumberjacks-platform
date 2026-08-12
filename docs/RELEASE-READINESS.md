# Cross-repository release readiness

Status captured 2026-08-12. This is a readiness contract, not a publication or
deployment receipt. No tag, NuGet package, GitHub release, or deployment was
created while adding these lanes.

## Current blockers

| Boundary | Ready in this repository | External blocker |
| --- | --- | --- |
| Transport NuGet | Tag workflow, strict nupkg validator, absent-version preflight, and public-availability poll | `NUGET_API_KEY` is not configured; NuGet.org does not serve `Comfy.Transport.Contracts` 0.1.0 |
| Public Quest packages | Exact public profile and coordinated repin transaction | NuGet.org does not serve `Comfy.Quest.Contracts` or `Comfy.Quest.Studio` 0.1.0 |
| NetworkSense mod | Pure manifest-v1/SHA256SUMS consumer verifier and executed tamper negative | No NetworkSense GitHub release is currently published; deployment remains an explicit later operator action |
| Quest assets | Manifest-v1 importer, unpinned lock state, deterministic offline check, and fixture tamper tests | `comfy-quest` has no GitHub release, so there is deliberately no live tag or manifest-hash pin |

The absence claims above were checked against the GitHub Releases API, the
NuGet.org flat-container API, and the repository's configured GitHub secret
names on 2026-08-12. Re-check them immediately before a release; they are not
permanent facts.

## Transport package publication

`.github/workflows/publish-nuget.yml` runs only for `nuget-v*` tags and the
first-publication lane accepts only `nuget-v0.1.0`. It verifies that the tag
targets the workflow commit, refuses to continue if the exact public version
already exists, packs with that full repository commit, and strictly validates
the package ID, version, repository URL/commit, dependency-free nuspec, managed
assembly, source/readme bytes, and the complete payload. It publishes without
`--skip-duplicate`, polls NuGet.org, then validates the bytes NuGet.org serves.

Local publication rehearsal (no push):

```powershell
tools\nuget\Test-TransportNuGetReadiness.ps1 -DotNet C:\work\dotnet9\dotnet.exe
```

The workflow must not be triggered until the `nuget-production` environment
has an authorized `NUGET_API_KEY` and the tag is intentionally created at the
audited commit. A failure because 0.1.0 already exists is a hard stop: inspect
the public package rather than treating it as a duplicate success.

## Quest dependency profiles

The active `interim` profile pins both Quest packages to exact
`[0.1.0-local]` and retains `packages-local` ahead of NuGet.org. The committed
`public` profile pins both to exact `[0.1.0]` and removes the local source.
Activation is one validated transaction across both NuGet configs, central
versions, Companion, and Companion.Tests; an old Studio `ProjectReference` is
converted to the package seam before any write is committed. The public command
also refuses to mutate files until NuGet.org serves both exact 0.1.0 packages.

```powershell
tools\dependencies\Set-DependencyProfile.ps1 -Profile interim -Check
tools\dependencies\Test-DependencyProfiles.ps1

# Only after BOTH public Quest packages have been downloaded and inspected:
tools\dependencies\Set-DependencyProfile.ps1 -Profile public -WhatIf
tools\dependencies\Set-DependencyProfile.ps1 -Profile public
```

If validation or a write fails, the transaction restores the original file
contents. Do not activate `public` one package at a time.

## NetworkSense mod release input

The verifier reads exactly four local files: `ComfyNetworkSense.dll`,
`release-manifest.json`, `boundary-receipt.json`, and `SHA256SUMS`. It performs
no source-control lookup, network access, build, image operation, remote
session, deployment, or other state change.

```powershell
infra\gcp\p7\scripts\Test-ModReleaseArtifact.ps1 `
  -ArtifactDirectory <downloaded-release-directory> `
  -ExpectedTag <mod-vX.Y.Z[-label]> `
  -ExpectedSourceRevision <full-40-hex-source-commit> `
  -ExpectedReleaseId <baked-release-id> `
  -ExpectedDependencyProfile public
```

`tools\Test-ArtifactHashGate.ps1` builds a synthetic producer-compatible
fixture outside the repository, proves the positive path, appends a byte to
the DLL, and requires the verifier to reject the tampered bundle. It also
mechanically rejects forbidden process, source-control, remote, image, or build
commands in the verifier.

The retained split-proof candidate is compatible with the consumer contract:
`mod-v0.5.80-split-proof`, source
`8d9ced6179569d9049af982bb47e41e2f8b56a19`, baked release
`m7-c10b-20260807-r42`, dependency profile `interim`, and DLL SHA-256
`6d07d0756c7d928113ec4f0b10e83c73a2f4e6f8119cc4d2f6dfa66c7dbfdc24`.
That is compatibility evidence, not a live GitHub release or deployment.

## Quest asset import

The Quest import lane remains deliberately unpinned until a real
`comfy-quest` release exists. Its fixture tests are the positive and tamper
proof; they do not fabricate a live tag or manifest hash. See the importer
help and the explicitly `unpinned` lock at
`Lumberjacks/docs/workbench/quest-release.lock.json` for the boundary state:

```powershell
tools\workbench\Import-QuestRelease.ps1 -?
tools\workbench\Test-QuestReleaseImporter.ps1
```

When a release exists, first inspect its manifest-v1 bundle, then run the
import with the explicit release tag and expected manifest SHA-256. Commit the
updated lock, vendored Quest Lab HTML/stage archives, and `workbench.json`
hashes first; then use the established Workbench render/check provenance commit
for generated `workbench.html`. `-Check` is offline and deterministic; it must
pass from a fresh checkout after the import.

```powershell
tools\workbench\Import-QuestRelease.ps1 `
  -ReleaseTag <quest-vX.Y.Z-split-proof> `
  -ExpectedManifestSha256 <audited-64-hex-manifest-hash>
tools\workbench\Import-QuestRelease.ps1 -Check
```

The exact destinations are `questlab.html` in Gateway Community plus
`tools/workbench/dist/{quest-lab.zip,quest-picker.html,quest-picker.zip}`. The
producer manifest is vendored beside the lock and the Quest Lab/Picker rows in
`workbench.json` receive the verified ZIP hashes, byte counts, and release
timestamp. A manifest-hash mismatch is rejected before any destination changes.
