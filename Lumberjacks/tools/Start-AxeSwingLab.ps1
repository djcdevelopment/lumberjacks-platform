[CmdletBinding()]
param(
    [ValidateSet('vulkan', 'd3d12')]
    [string]$Renderer = 'vulkan',
    [ValidateSet('arc', 'head', 'contact', 'bite')]
    [string]$Stage = 'head',
    [string]$Godot = 'C:\work\godot-4.6.1\editor\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe'
)

$ErrorActionPreference = 'Stop'
$lumberjacksRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $lumberjacksRoot
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

$project = Join-Path $lumberjacksRoot 'clients\godot-cs\nature-2.0'
if (-not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw "Godot 4.6.1 console executable not found: $Godot"
}
if (-not (Test-Path -LiteralPath (Join-Path $project 'project.godot') -PathType Leaf)) {
    throw "Lumberjacks Godot project not found: $project"
}

Write-Host "[lumberjacks] axe $Stage lab renderer=$Renderer"
& $Godot --path $project --rendering-driver $Renderer -- "--lab=axe-$Stage"
if ($LASTEXITCODE -ne 0) { throw "Axe swing lab exited with code $LASTEXITCODE" }
