"""Render the guest guide from the sealed manifest and committed guest inputs."""
import argparse
import json
from pathlib import Path
import re
import sys


def render(manifest, inputs):
    mod = manifest["mod"]
    limitations = " ".join(manifest.get("limitations", []))
    return f"""# Comfy guest package — {manifest['release_id']}

This package installs the ComfyNetworkSense clean-build artifact for Valheim.

## Before installing

- Licensed Steam copy of Valheim, build **{inputs['valheim_build']}**.
- BepInExPack Valheim **{inputs['bepinex_version']}** installed in the Valheim directory.
- A one-use bootstrap URL supplied by the host. Do not paste its response into public logs.

## Install

Run PowerShell 5.1 from this package directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\Install-ComfyGuest.ps1 -BootstrapUrl '<one-use bootstrap URL>'
```

The installer discovers the Steam library, checks BepInEx, calls the bootstrap endpoint once,
backs up existing files, merges only the `[Lumberjacks]` section, and installs the DLL atomically.
It writes `BepInEx\\comfy-guest-install.json`; keep that receipt for uninstall.

## Join

Use the join address **{inputs['join_address']}** and the password sent separately by the host.
The gateway endpoint is **{inputs['gateway_base_url']}**. Its anonymous health path is
`{inputs['gateway_health_path']}` and enrollment path is `{inputs['enrollment_path']}`.

## Troubleshooting

Run:

```powershell
powershell -NoProfile -File .\\scripts\\Invoke-GuestPreflight.ps1 -ValheimPath 'C:\\Path\\To\\Valheim'
```

Every failed check includes a remedy. To remove this package, run
`Uninstall-ComfyGuest.ps1`; uninstall is receipt-driven and restores the exact pre-install files.

## Artifact identity and limits

- Release identity comes from `manifest.json`, not from the DLL.
- Mod version: **{mod['version']}**.
- DLL SHA-256: `{mod['clean_build_sha256']}`.
- The DLL is a **clean-build artifact, IL-equal to the runtime DLL**; it is not claimed to be byte-identical to the historical runtime artifact.
- The sealed manifest describes a candidate cut. {limitations}
- {inputs['reissue_note']}
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", type=Path, required=True)
    ap.add_argument("--inputs", type=Path, required=True)
    ap.add_argument("--output", type=Path, required=True)
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--drift-scan", action="store_true")
    args = ap.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    inputs = json.loads(args.inputs.read_text(encoding="utf-8-sig"))
    text = render(manifest, inputs)
    if args.check:
        if not args.output.exists() or args.output.read_text(encoding="utf-8") != text:
            print("guest guide is stale", file=sys.stderr)
            return 1
        return 0
    if args.drift_scan:
        source = args.manifest.read_text(encoding="utf-8-sig") + args.inputs.read_text(encoding="utf-8-sig")
        source_values = set(re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', source))
        patterns = [r"\b(?:\d{1,3}\.){3}\d{1,3}:\d+\b", r"0\.5\.\d+", r"\b[0-9a-fA-F]{64}\b"]
        for pattern in patterns:
            for hit in re.findall(pattern, text):
                if not any(hit in value for value in source_values):
                    print(f"guide drift literal is not sourced from manifest or inputs: {hit}", file=sys.stderr)
                    return 1
        return 0
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(text, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
