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

Run the first creative runtime-envelope experiment twice:

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 -Experiment cre-e01-runtime-envelope -RunTwice
```

CRE-E01 emits `performance.gate_decision` rows for green, amber, and red synthetic
combat-pressure bands. It proves budget, criticality, degradation, and route semantics
without Unity, Steam, or a gameplay-authority change.

Run the real Gateway seams in-memory for E02 or E03:

```powershell
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 -Experiment m7-e02-recipient-fanout -Driver gateway
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 -Experiment m7-e03-motion-fingerprints -Driver gateway
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 -Experiment m7-e02-recipient-fanout -Driver gateway_durable
.\tools\authority-lab\Invoke-AuthorityExperiment.ps1 -Experiment m7-e03-motion-fingerprints -Driver gateway_udp
```

The Gateway runs instantiate `ValheimZdoRedirectService` and
`UdpTransport.HandleValheimMotionFrameAsync` directly. They are not pure substitutes,
but they also do not require a running server or public endpoint. `gateway_durable`
restarts the redirect service against a temporary WAL and verifies pending/ACK recovery;
`gateway_udp` starts the real UDP listener on loopback, binds both client endpoints, and
verifies target delivery through `TrySend`.

Normalize a captured native probe without claiming native authority:

```powershell
docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet run --no-build --project src/AuthorityLab -- normalize-native --scenario /repo/fieldlab/experiments/m7/m7-e04-native-candidate-capture/scenario.yaml --input /repo/path/to/native.jsonl --output /repo/fieldlab/experiments/m7/m7-e04-native-candidate-capture/runs/native-<timestamp>
```

The normalizer retains the exact source, emits `authority.native_candidate_observed`
rows, and records ignored or malformed input rather than dropping it. A fixture
smoke receipt exists under E04; it is parser evidence, not a native capture.

Replay the normalized candidates through the current pure distance-band policy:

```powershell
docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet run --no-build --project src/AuthorityLab -- replay-native --run /repo/fieldlab/experiments/m7/m7-e04-native-candidate-capture/runs/native-<timestamp> --output /repo/fieldlab/experiments/m7/m7-e04-native-candidate-capture/runs/replay-<timestamp>
```

Replay emits explicit `authority.lumberjacks_decision` and
`authority.decision_compared` rows with `observation_only`; it does not claim
that the native client used the Lumberjacks decision.

The executable surface is deliberately small: `generate`, `run`,
`normalize-native`, `compare`, and `check`. A future native replay comparator must
emit the same receipt shape and must never label a native observation as a
Lumberjacks decision without an explicit comparison.
