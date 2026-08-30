[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GodotExe,
    [string]$Version = '0.1.0-alpha.1',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

$projectRoot = Join-Path $repoRoot 'Lumberjacks\clients\godot-cs\nature-2.0'
$dotnet = if (Test-Path -LiteralPath 'C:\work\dotnet9\dotnet.exe') {
    'C:\work\dotnet9\dotnet.exe'
} else {
    'dotnet'
}
$dotnetCommand = Get-Command $dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$env:DOTNET_ROOT = $dotnetRoot
$env:DOTNET_ROOT_X64 = $dotnetRoot
if (-not (($env:Path -split ';') -contains $dotnetRoot)) {
    $env:Path = "$dotnetRoot;$env:Path"
}
$resolvedGodot = (Resolve-Path -LiteralPath $GodotExe).Path
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot 'artifacts\native'
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not $releaseRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be inside '$artifactsRoot'."
}

$releaseName = "Lumberjacks-$Version-windows-x64"
$staging = Join-Path $releaseRoot $releaseName
$zipPath = Join-Path $releaseRoot "$releaseName.zip"
$hashPath = "$zipPath.sha256"

foreach ($target in @($staging, $zipPath, $hashPath)) {
    if (Test-Path -LiteralPath $target) {
        $resolved = [IO.Path]::GetFullPath($target)
        if (-not $resolved.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove release target outside '$releaseRoot': $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$clientSource = Get-Content -LiteralPath (Join-Path $projectRoot 'scripts\Networking\SimulationClient.cs') -Raw
if ($clientSource -notmatch [regex]::Escape("CurrentRelease = `"$Version`"")) {
    throw "Client release constant does not match requested version '$Version'."
}

& $dotnet build (Join-Path $projectRoot 'Lumberjacks.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Native client build failed.' }

$exePath = Join-Path $staging 'Lumberjacks.exe'
$ErrorActionPreference = 'Continue'
$importOutput = @(& $resolvedGodot --headless --path $projectRoot --import 2>&1)
$importCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$importOutput | ForEach-Object { Write-Host $_ }
if ($importCode -ne 0 -or ($importOutput -join "`n") -match '(?m)^ERROR:') {
    throw 'Godot project import failed.'
}
$ErrorActionPreference = 'Continue'
$exportOutput = @(& $resolvedGodot --headless --path $projectRoot --export-release 'Windows x64' $exePath 2>&1)
$exportCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$exportOutput | ForEach-Object { Write-Host $_ }
$exportText = $exportOutput -join "`n"
if ($exportCode -ne 0 -or
    $exportText -match '(?m)^ERROR:' -or
    $exportText -match 'completed with warnings') {
    throw 'Godot Windows export failed or completed with warnings.'
}

$expected = @(
    $exePath,
    (Join-Path $staging 'Lumberjacks.pck'),
    (Join-Path $staging 'data_Lumberjacks_windows_x86_64\Lumberjacks.dll')
)
foreach ($file in $expected) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Godot export did not produce '$file'."
    }
}

$readme = @"
LUMBERJACKS - NORTHWOODS FIELD ALPHA $Version

1. Extract the whole zip.
2. Run Lumberjacks.exe.
3. Choose "Import field pass" and select lumberjacks-access.json from your invitation.

Controls
  WASD          walk
  Right mouse   orbit camera
  Mouse wheel   zoom
  E             read the nearest tree
  Left mouse    swing the axe
  Escape        leave the field

This alpha is unsigned. Windows SmartScreen may warn before first launch.
Verify the zip against the published SHA-256 file or GitHub artifact attestation.
The public game archive never contains an enrollment key or field pass.
"@
Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Value $readme -Encoding utf8

$revision = (& git -C $repoRoot rev-parse HEAD).Trim()
$files = Get-ChildItem -LiteralPath $staging -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
    $relative = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
    [ordered]@{
        path = $relative
        size_bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    schema = 'lumberjacks-native-release/v1'
    release = $Version
    platform = 'windows-x64'
    source_repository = 'djcdevelopment/lumberjacks-platform'
    source_revision = $revision
    signed = $false
    credential_files_included = $false
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding utf8

if (Get-ChildItem -LiteralPath $staging -Recurse -File |
        Where-Object { $_.Name -match '(?i)(access|credential|secret|enrollment).*\.json$' }) {
    throw 'Credential-shaped file found in public staging directory.'
}

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zip = Get-Item -LiteralPath $zipPath
$sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$sha  $($zip.Name)" -Encoding ascii

[ordered]@{
    schema = 'lumberjacks-native-release-result/v1'
    release = $Version
    source_revision = $revision
    package = $zip.FullName
    package_size_bytes = $zip.Length
    package_sha256 = $sha
    checksum = $hashPath
} | ConvertTo-Json -Compress
