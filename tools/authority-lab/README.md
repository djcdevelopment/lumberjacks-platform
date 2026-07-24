# AuthorityLab

Small, deterministic R&D harness for M7 authority experiments. The first slice is
pure and synthetic: it does not launch Valheim, contact P7, deploy a mod, or ask a
human to join.

`scenario.yaml` files use JSON syntax, which is valid YAML, so the runner has no
package dependency merely to parse the first fixtures. All serialized contract
fields are snake_case. Run receipts retain source, input, decision, invariant, and
raw-event evidence while normalized decision hashes exclude time, run, and machine
metadata.

Build and test in the same SDK image used by the rest of the repository:

```powershell
docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet build AuthorityLab.sln
docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet test AuthorityLab.sln --no-build
```

Run E00 twice and retain a comparison:

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 -Experiment m7-e00-lab-truth -RunTwice
```

The executable surface is deliberately small: `generate`, `run`, `compare`, and
`check`. A future Gateway/native driver must emit the same receipt shape and must
never label a pure run as Gateway evidence.
