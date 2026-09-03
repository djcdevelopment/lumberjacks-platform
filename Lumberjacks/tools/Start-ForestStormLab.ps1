[CmdletBinding()]
param(
    [ValidateSet('d3d12', 'vulkan')]
    [string]$Renderer = 'vulkan',
    [ValidateSet('feel', 'default', 'stress')]
    [string]$Preset = 'default',
    [int]$TreeCount = 0,
    [string]$Scenario = '',
    [int]$WarmupSeconds = 10,
    [int]$CaptureSeconds = 0,
    [double]$StormStartSeconds = 0,
    [string]$Receipt = '',
    [string]$Screenshot = '',
    [int]$GpuIndex = -1,
    [switch]$ValidateOnly,
    [switch]$DisableVsync,
    [switch]$ImportAssets,
    [string]$Godot = 'C:\work\godot-4.6.1\editor\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe'
)

$ErrorActionPreference = 'Stop'
$lumberjacksRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $lumberjacksRoot
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

$project = Join-Path $lumberjacksRoot 'clients\godot-cs\nature-2.0'
if (-not (Test-Path -LiteralPath $Godot)) { throw "Godot 4.6.1 console executable not found: $Godot" }
if (-not (Test-Path -LiteralPath (Join-Path $project 'project.godot'))) { throw "Lumberjacks Godot project not found: $project" }

$pineImport = Get-ChildItem -LiteralPath (Join-Path $project '.godot\imported') -Filter 'Pine_1.gltf-*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ImportAssets -or -not $pineImport) {
    Write-Host '[lumberjacks] importing new Godot assets'
    & $Godot --headless --editor --path $project --quit
    if ($LASTEXITCODE -ne 0) { throw "Godot asset import exited with code $LASTEXITCODE" }
}

$engineArgs = @('--path', $project, '--rendering-driver', $Renderer)
if ($GpuIndex -ge 0) { $engineArgs += @('--gpu-index', "$GpuIndex") }
if ($ValidateOnly) { $engineArgs += '--headless' }
if ($DisableVsync) { $engineArgs += '--disable-vsync' }

$userArgs = @('--lab=forest-storm', "--forest-preset=$Preset", "--warmup-seconds=$WarmupSeconds")
$userArgs += "--storm-start-seconds=$($StormStartSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))"
if ($TreeCount -gt 0) { $userArgs += "--tree-count=$TreeCount" }
if ($Scenario) { $userArgs += "--scenario=$([IO.Path]::GetFullPath($Scenario))" }
if ($CaptureSeconds -gt 0) { $userArgs += "--capture-seconds=$CaptureSeconds" }
if ($Receipt) { $userArgs += "--receipt=$([IO.Path]::GetFullPath($Receipt))" }
if ($Screenshot) { $userArgs += "--screenshot=$([IO.Path]::GetFullPath($Screenshot))" }
if ($ValidateOnly) { $userArgs += '--validate-only' }

Write-Host "[lumberjacks] forest/storm lab renderer=$Renderer preset=$Preset"
& $Godot @engineArgs -- @userArgs
if ($LASTEXITCODE -ne 0) { throw "Forest/storm lab exited with code $LASTEXITCODE" }
