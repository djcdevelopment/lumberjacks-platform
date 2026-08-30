# Lumberjacks 0.1.0-alpha.1

This is the first public Windows x64 field build: ten invite-only players, one shared Northwoods clearing, and one authored 173-year-old pine whose axe marks, health, and final fall direction are server-authoritative and persistent.

Download the zip, verify its companion SHA-256 file, extract everything, and run `Lumberjacks.exe`. The executable is intentionally unsigned for this alpha, so Windows SmartScreen may ask for confirmation. A valid personal `lumberjacks-access.json` field pass is required to enter the server; it is obtained from an invitation and is never included in this public archive.

Controls: WASD to walk, right mouse to orbit, mouse wheel to zoom, E to read the nearest tree, left mouse to swing the axe, and Escape to leave.

Build provenance is published as a GitHub artifact attestation. Verify it with:

```powershell
gh attestation verify .\Lumberjacks-0.1.0-alpha.1-windows-x64.zip -R djcdevelopment/lumberjacks-platform
```
