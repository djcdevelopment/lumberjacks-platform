# lumberjacks-platform

The sovereign home of the Lumberjacks game and transport platform: the Godot client,
gameplay labs, Gateway and service code, Companion, transport contracts, P7
infrastructure, the live FieldLab harness, and the roadmap/Workbench implementation.

Lumberjacks is the game built here. Valheim is a separate commercial game whose mod
and community workloads exercise some of this network infrastructure; the names and
player paths are intentionally distinct.

Start with [`BOUNDARY.md`](BOUNDARY.md) for ownership and artifact contracts,
then [`Lumberjacks/README.md`](Lumberjacks/README.md) for platform development.
The accepted axe-mechanics lineage and its retrospective are in
[`Lumberjacks/docs/labs/axe-lab-series.md`](Lumberjacks/docs/labs/axe-lab-series.md)
and [`Lumberjacks/docs/retro/2026-09-03-axe-labs-01-05.md`](Lumberjacks/docs/retro/2026-09-03-axe-labs-01-05.md).
The extraction record and baseline commit map live in
[`PROVENANCE.md`](PROVENANCE.md).
Cross-repository publication/import blockers and the no-deploy verification
commands live in [`docs/RELEASE-READINESS.md`](docs/RELEASE-READINESS.md).

```powershell
C:\work\dotnet9\dotnet.exe build Lumberjacks\Game.sln -c Release
C:\work\dotnet9\dotnet.exe test Lumberjacks\Game.sln -c Release --no-build
cd Lumberjacks
npm ci
npm run roadmap:test
npm run workbench:check
```

Cross-repository inputs are packages or hash-verified files. No command in
this repository requires a sibling checkout.
