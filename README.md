# lumberjacks-platform

The sovereign home of the Lumberjacks transport platform: Gateway and service
code, Companion, transport contracts, P7 infrastructure, the live FieldLab
harness, and the roadmap/Workbench implementation.

Start with [`BOUNDARY.md`](BOUNDARY.md) for ownership and artifact contracts,
then [`Lumberjacks/README.md`](Lumberjacks/README.md) for platform development.
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
