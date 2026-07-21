# Risk 12 — release reproducibility: DECIDED

2026-07-19. Supersedes the proposal in this file's first revision (`6df6691`), which
deliberately decided nothing pending an acceptance test. **The test has now run and
passed. This document records the decision it authorises.**

## The decision

1. **The Docker image is the authoritative release artifact**, and the *published
   application payload inside it* is the unit of reproducibility.
2. **The local `bin/Release` publish output is advisory only.** It is not the artifact
   that ships, it is not what verification reads, and its hashes do not attest to what
   was deployed.
3. **Risk 12's non-reproducibility does not apply to the shipped Gateway.** It applies
   to the local `dotnet build` path, which is now advisory.
4. `EnableSourceControlManagerQueries=false` **remains optional hardening and is not
   part of this decision.** The test exposed no nondeterminism that would require it.

## What must match — the acceptance criteria

A Gateway build is reproducible when, for the same source tree, two independent
`--no-cache` builds produce:

| # | Criterion | Result |
| --- | --- | --- |
| 1 | Extracted `/app/Game.Gateway.dll` byte-identical | **PASS** |
| 2 | Shipped `/app/Game.Gateway.pdb` byte-identical | **PASS** |
| 3 | Complete published file inventory and per-file hashes identical | **PASS** — 48/48 |
| 4 | Baked `LumberjacksExpectedModRelease` identical and correct | **PASS** |
| 5 | Runtime configuration inputs identical (`Env`, `Entrypoint`, `WorkingDir`, `ExposedPorts`, `User`) | **PASS** |

**Byte-identical *complete Docker images* are explicitly NOT required** — see the
boundary in §3. A build that satisfies 1-5 is reproducible even when image IDs differ.

## 1. What risk 12 recorded

From `New-ReleaseCut.ps1`'s trailing note (root cause found 2026-07-18, named in
`909315b`): the SDK's source-control tasks embed the git HEAD sha as `SourceRevisionId`,
it reaches the portable PDB, the PDB checksum rides in the DLL's debug directory, and
cuts are ordered build-then-commit — so the shipped DLL carries the *parent* commit's
sha and no checkout of the release commit can rebuild it.

## 2. The test

`.dockerignore:13` excludes `.git/` and the Dockerfile has no `COPY . .`, so the SDK
inside the build stage has no repository to query. Three independent `--no-cache`
builds, all with `--build-arg LUMBERJACKS_EXPECTED_MOD_RELEASE=m9-repro-20260719-r1
--build-arg LUMBERJACKS_REQUIRE_RELEASE=1`:

| Build | HEAD | Image id |
| --- | --- | --- |
| `lj-repro:a` | `26c6f2b` | `sha256:e2bc3c4b571ca50ac7d05c461af2d454e7a7e1f68afa391086e438f5f501e5cb` |
| `lj-repro:b` | `6df6691` | `sha256:44a5094fe2e8db37caa73975e63221871bd0fae628d13dcaabca050ef34c8e71` |
| `lj-repro:c` | `6df6691` | `sha256:f4e508978914cb92905ec31009f9191d9ff1cfad26a188eb874b81a157fef7bc` |

Comparison method: `docker create` → `docker cp /app/.` → `sha256sum` every file, sorted
by path, then `diff` the manifests. No reliance on image digests.

**A vs B — different HEADs, one genuinely changed source file.** 47 of 48 files
identical. The sole difference is `Community/roadmap.html`
(`2cd868f8…` → `fef38496…`), which the roadmap ritual regenerated in `6df6691`; it is a
`Content` item with `CopyToPublishDirectory`, not an `EmbeddedResource`, so it cannot
reach the assembly. That difference also proves the comparison detects real changes
rather than being vacuously green.

**B vs C — identical source, independent builds.** All 48 files byte-identical.

**Across all three builds:**

```
Game.Gateway.dll  7f1a7cdcba85d2897cbfb618171dac2dad036386875abea1e932e765497b7113
Game.Gateway.pdb  38b5d367f98b9e5a6a29999753e4c7686171c359148c9229297efda1156de6c7
baked release id  m9-repro-20260719-r1   (read from each image, verifier exit 0)
```

The DLL and PDB are byte-identical across two different HEADs. Since risk 12's stated
mechanism is precisely the sha reaching the PDB, this is the decisive result: **no HEAD
sha reaches the shipped artifact.**

## 3. The boundary — payload vs complete image

The application payload is deterministic. The complete Docker image is not, and this is
expected rather than a defect:

| Property | Deterministic? | Evidence |
| --- | --- | --- |
| Published files and hashes | **Yes** | B vs C, 48/48 identical |
| `Game.Gateway.dll` / `.pdb` | **Yes** | identical across all three builds |
| Baked release id | **Yes** | `m9-repro-20260719-r1` from all three |
| Runtime config (`Env`/`Entrypoint`/`WorkingDir`/`ExposedPorts`/`User`) | **Yes** | identical across all three |
| Base image layers (7 of 8) | **Yes** | identical across all three |
| **Final application layer digest** | **No** | B `f12e8eab…` vs C `70a006b3…` — *identical file content* |
| **Image id** | **No** | differs on every build |
| **`Created` timestamp** | **No** | wall-clock per build |

The final layer digest differing while every file inside it is byte-identical is tar
member metadata (mtimes), not application content. **Do not read that as a
non-reproducible build.** Attestation/provenance manifests are emitted per build by
BuildKit and vary likewise.

Practical consequence: **an image id is a deployment identifier, not a reproducibility
claim.** Manifests should record image ids to say *what ran*, and payload hashes to say
*what it was*. The two answer different questions and only the second is reproducible.

## 4. Explicitly not decided here

- **`EnableSourceControlManagerQueries=false`** — optional hardening only. It would make
  local `bin/Release` builds match container builds byte-for-byte, restoring meaning to
  the advisory check. The shipped artifact does not need it, and per the acceptance
  criteria nothing in this test motivates it. Decide on its own merits.
- **Cut ordering.** Build-then-commit stays. Its defect touches only the advisory path.
- **The mod.** `ComfyNetworkSense` is built on the host with full git visibility, so risk
  12 applies to it unchanged. This decision covers the Gateway only.
- **Existing recorded hashes.** Per the original note they "attest what this machine
  built at this exact HEAD"; not re-litigated.

## Related

- Gateway image release id and admitted mod release are **separate identities**:
  `C:\work\comfy\infra\gcp\p7\scripts\New-GatewayReleaseCut.ps1`
- Authoritative gate, reads the completed image:
  `C:\work\comfy\infra\gcp\p7\scripts\Test-GatewayImageRelease.ps1`
- Regression suite: `C:\work\comfy\infra\gcp\p7\scripts\Test-GatewayImageReleaseRegression.ps1`
- Defect evidence: `C:\work\comfy\fieldlab\evidence\p7-session-20260719-release-gate-defect\`
