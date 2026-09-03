# Lumberjacks September launch slice

Status: implemented locally on 2026-09-02; the existing P7 Gateway is live, but the current source
changes have not been promoted to it.

## Names and boundary

- **Lumberjacks** is the Godot game and the network/platform built for it.
- **Valheim** is the separate commercial game whose mod work helped exercise the network.
- Valheim enrollment, Steam identity, field-pass installation, and mod-release gates are not part
  of the Lumberjacks R&D player path.

The launch slice is intentionally narrow: make the current authoritative walk/chop/persistence loop
feel like a game inside a moving forest and storm. Advanced hinge, fracture, collision, and felling
physics remain later work. Continuous vegetation motion is visual-only GPU work; the server still
owns tree health, fall state, stump state, regrowth, player state, and persistence.

## Get into the game from OMEN

The debug export is installed at `%LOCALAPPDATA%\Lumberjacks\RAndD`. From the Isolate checkout:

```powershell
.\tools\lumberjacks\Start-LumberjacksRnd.ps1
```

That command reuses or starts the `comfy-p7` SSH forward, health-checks Gateway at
`127.0.0.1:14000`, and launches the client with
`--server=ws://127.0.0.1:14000/game`. The game can also be opened directly and its address field
already contains that endpoint.

R&D access is a fixed development username/password, configurable with
`LUMBERJACKS_RND_USERNAME` and `LUMBERJACKS_RND_PASSWORD`. It is deliberately not a player identity
system. Current Gateway source accepts it only when the socket is already on the private plane;
public or reverse-proxied sockets cannot use it. The client temporarily sends the old native release
header as well, so it can connect to the pre-R&D Gateway image that is running now. Once the new
Gateway is promoted, release matching is not an R&D admission gate.

## Forest and storm

`ForestStormLab` is an offline tuning and measurement scene:

- deterministic placement from a versioned scenario;
- 2,048-tree feel, 8,192-tree default, and 32,768-tree stress presets;
- 64-meter MultiMesh chunks;
- rooted trunk bend, crosswind, spatial gusts, leaf detail, stiffness, crown mass, and moisture in a
  shared GPU vertex shader;
- storm sky, fog, rain, light response, and a small deterministic lightning treatment;
- deterministic receipts that include scenario, placement, and asset identities.

The playable `World` uses the same shader and Quaternius CC0 tree meshes for authoritative trees.
Each tree retains its existing chop/fall/stump/regrowth state machine and receives stable visual
traits. The storm controller follows the region profile's trade-wind direction.

Run the sandbox directly:

```powershell
.\tools\Start-ForestStormLab.ps1 -Renderer d3d12 -Preset feel
```

Controls are `WASDQE` to fly, right mouse to look, `Tab` to tune, `R` to replay, and `F` to freeze.

## Axe mechanics labs

Five serial, human-reviewed axe labs now isolate the articulated horizontal-fell swing, chopping
head, neutral contact, one fresh stateful bite, and opposing upward under-the-shoulder stroke. The
accepted values are pinned as `accepted-axe-v1` and `accepted-axe-up-v1`; see
[`docs/labs/axe-lab-series.md`](labs/axe-lab-series.md). Lab 05 passed visual acceptance on
2026-09-03. Accumulated notch construction is next but has not started; chip release, fracture, fall,
authority, persistence, and networking remain later gates.

Launch the accepted comparison with `tools\Start-AxeSwingLab.ps1 -Stage opposing`. Left mouse is the
under-swing, right mouse is the top-swing, and `F` continues from exact contact. The accepted witness
is 1.5 m in diameter with centered tangent bit contact. The placeholder character deliberately uses
grounded, volume-preserving slime deformation rather than clipping its capsule through the floor.

The playable camera now targets 1.45 meters above the player origin instead of looking through
rolling terrain from the character's feet. Vulkan is the Windows default for the current foliage
build after the observed D3D12 device-loss cascade; D3D12 remains available as an explicit benchmark
cell rather than a launch dependency.

## Renderer decision protocol

The benchmark owner is `E:\work\b70tools\scripts\godot-forest`. Run both renderers three times at
the same density and viewport, then compare the receipts. The harness records host pressure,
b70tools telemetry, the stable adapter/LUID inventory, and the adapter with the largest observed
render/compute counter delta. On OMEN the intended active card is currently
`adapter_00017310` (`0000:09:00.0`).

```powershell
$project = 'C:\work\lumberjacks-platform\Lumberjacks\clients\godot-cs\nature-2.0'
$godot = 'C:\work\godot-4.6.1\editor\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe'

.\scripts\godot-forest\Start-GodotForestRun.ps1 -ProjectPath $project -GodotPath $godot `
  -Renderer d3d12 -Preset default -ExpectedAdapterId adapter_00017310
.\scripts\godot-forest\Start-GodotForestRun.ps1 -ProjectPath $project -GodotPath $godot `
  -Renderer vulkan -Preset default -ExpectedAdapterId adapter_00017310
.\scripts\godot-forest\Compare-GodotForestRuns.ps1 -RunRoot .\runs
```

The comparison rejects mismatched asset, scenario, placement, density, or viewport identities. If
median p99 differs by no more than five percent, D3D12 remains the default; otherwise the lower
median p99 renderer wins. Short 64-tree runs are smoke tests, not a renderer decision.

## Promotion boundary

Local compilation, tests, D3D12/Vulkan render smokes, export, installation, private tunnel, and a
real connection to the currently live P7 world are verified. A new Gateway image and release client
have not been published from this working tree. Promotion still requires the repository's normal
immutable image/package receipts and rollback checks; it must not silently absorb unrelated dirty
work.
