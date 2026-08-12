# lumberjacks-platform Repository Working Notes

## Operating boundary

This sovereign repository owns the Lumberjacks transport platform, Gateway,
services, Companion, P7 infrastructure, live FieldLab harness, roadmap journal,
and the telemetry Workbench package. Integration with other repositories uses
exact packages or hash-verified release artifacts; scripts do not reach into
sibling checkouts or assume `C:\work` paths.

State-changing automation must dot-source `tools/Assert-RepoIdentity.ps1` and
call `Assert-RepoIdentity` before the first mutation. Paths derive from the
script location or an explicit parameter.

## Release boundaries

Read [`docs/RELEASE-READINESS.md`](docs/RELEASE-READINESS.md) before changing a
cross-repository pin. `Comfy.Transport.Contracts` publishes only through the
`nuget-v*` workflow; it never treats an existing version as a skipped success.
Quest dependencies move from the exact interim profile to the exact public
profile only through `tools/dependencies/Set-DependencyProfile.ps1`, as one
transaction. NetworkSense and Quest releases are downloaded inputs verified
from their producer manifests; missing releases never authorize sibling-source
builds.

Current external blockers (verified 2026-08-12): `NUGET_API_KEY` is absent,
none of the three public 0.1.0 contract packages is available on NuGet.org,
and `comfy-quest` has no GitHub release. Therefore do not create a release tag,
publish a package/release, activate the public profile, or deploy as part of
readiness work.

## Landing work

“Go”, “push”, “land it”, or “ship it” authorizes commit and direct push to
`main`. Stop only for force-push, history rewrite, deleting someone else’s
work, or work outside this repository. Pull with `--ff-only` before pushing.
Never bypass hooks or GitHub push protection.

## Roadmap journal ceremony

A non-merge commit that changes `fieldlab/`, `infra/gcp/p7/`, or the platform
program must append its decision to `Lumberjacks/docs/roadmap/` and stage the
regenerated `Lumberjacks/src/Game.Gateway/Community/roadmap.html` in the same
commit:

```powershell
cd Lumberjacks
node scripts/roadmap.mjs note --milestone <M> --kind <kind> --summary "..." --impact "..." --verification "..."
node scripts/roadmap.mjs check --staged
```

The repository uses `.githooks`; configure it with
`git config core.hooksPath .githooks`.

## Verification

Use SDK 9 at `C:\work\dotnet9\dotnet.exe` when available:

```powershell
C:\work\dotnet9\dotnet.exe build Lumberjacks\Game.sln -c Release
C:\work\dotnet9\dotnet.exe test Lumberjacks\Game.sln -c Release --no-build
cd Lumberjacks; npm ci; npm run roadmap:test; npm run workbench:check
cd ..; python -m unittest discover -s tests
```

Claims are VERIFIED only with reproducible command output; otherwise label
them INFERRED or BLOCKED.
