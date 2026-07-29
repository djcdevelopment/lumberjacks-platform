# Comfy Guest Package

An earlier/parallel LAN-oriented guest install mechanism for the Valheim mod
(ComfyNetworkSense). A guest installs a sealed mod DLL + config into their own
Valheim install by running a PowerShell script against a package built here,
authenticating through a one-use bootstrap URL. Predates, and now sits
alongside, the newer Steam self-service join flow in
`Lumberjacks/src/Game.Gateway/Valheim/` (`SteamEnrollmentService.cs`,
`SteamEnrollmentEndpoints.cs`, `EnrollmentPages.cs`), which enrolls through
gateway web pages instead of a locally-run script package.

## Scripts

- `Install-ComfyGuest.ps1` -- installs the sealed DLL and merges Lumberjacks
  config keys into the guest's BepInEx config. Verifies the DLL sha256
  against `manifest.json`, calls the bootstrap URL for enrollment values,
  backs up anything it overwrites, writes a `comfy-guest-install.json`
  receipt, and rolls back on any failure.
- `Invoke-GuestPreflight.ps1` -- read-only checks (DLL hash, BepInEx present,
  Valheim not running, config writable, gateway health/TLS, and unless
  `-NoBootstrap` a live bootstrap probe). Emits a PASS/FAIL JSON verdict;
  makes no changes.
- `Uninstall-ComfyGuest.ps1` -- reverses an install from its receipt: restores
  backed-up files, removes files that didn't exist before install, strips
  only the config keys it added, then deletes the receipt and backups.
- `Collect-ComfyGuestDiagnostics.ps1` -- copies the guest's mod config and
  BepInEx log into an output folder with known secrets redacted, then fails
  hard if secret-shaped patterns (SteamID64s, key/token/password strings)
  still appear in the output.
- `build-guest-package.ps1` -- assembles the shippable package from a sealed
  release under `fieldlab/runs/releases/<release>/`: checks the DLL hash
  against the release manifest, copies DLL/manifest/inputs/scripts into an
  output folder, renders `GUEST-GUIDE.md` via `tools/render_guest_guide.py`
  (with a `--drift-scan` pass), writes a `guest-index.json` manifest, and
  zips the result unless `-NoZip` is passed.
- `lib/ComfyGuestCommon.psm1` -- shared helpers: sha256, atomic file replace,
  BepInEx `[Lumberjacks]` config-section merge/strip, Steam library discovery
  (`Find-ComfySteamValheimInstall`, reads `steamapps/libraryfolders.vdf`),
  secret redaction, and the bootstrap HTTP GET (`Get-ComfyBootstrap`).

## Inputs file

`guest-package-inputs.json` holds the per-release build inputs: `release_id`
(must match the release manifest), the gateway base URL and its
health/bootstrap/enrollment paths, the server join address, BepInEx/Valheim
version pins, and a free-text `reissue_note`.

## Running a build

```powershell
.\tools\guest-package\build-guest-package.ps1 `
  -ManifestPath fieldlab\runs\releases\<release>\<release>.json `
  -BundleRoot   fieldlab\runs\releases\<release> `
  -OutputRoot   fieldlab\handoffs\guest-client-pack\comfy-guest-<release>
```

Those three params default to the `m1-clean-20260717-r1` release, and
`-InputsPath` defaults to the `guest-package-inputs.json` beside this README.
Requires a working `py.exe -3` or `python.exe` on `PATH` (renders
`GUEST-GUIDE.md`). Add `-NoZip` to skip producing the `.zip` next to the
output folder.

## Release bundles are not in the repo

`fieldlab/runs/` is gitignored, so no sealed release ships with a clone --
`fieldlab/runs/releases/<release>/` is a machine-local build artifact. Point
`-ManifestPath` and `-BundleRoot` at wherever the bundle you want to package
actually lives.

`tests/test_guest_package.py` therefore builds against a synthetic release in
`tests/fixtures/guest-package/` (a manifest plus its own inputs file; the
stand-in DLL is written at test time from bytes held in the test module). That
covers all of this tooling on any fresh checkout. No script here parses the
DLL -- each only hashes it, copies it, or compares its hash to the manifest --
so the stand-in exercises the same paths the real artifact would. The one
thing a fixture cannot answer, whether the real sealed DLL still matches the
manifest shipped beside it, lives in `SealedReleaseTests`, which runs only on
a machine that has the bundle and skips with an explicit reason elsewhere.

## Known gap

`guest-package-inputs.json` carries this TODO as written, unresolved:

> "reissue_note": "TODO(stage-4-deploy): document GET /join/reissue after deployment."

The guest-facing behavior of that endpoint is not documented yet.
