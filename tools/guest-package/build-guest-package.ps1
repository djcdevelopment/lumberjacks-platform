param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\..\fieldlab\runs\releases\m1-clean-20260717-r1.json'),
    [string]$BundleRoot = (Join-Path $PSScriptRoot '..\..\fieldlab\runs\releases\m1-clean-20260717-r1'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\fieldlab\handoffs\guest-client-pack\comfy-guest-m1-clean-20260717-r1'),
    [switch]$NoZip
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ComfyGuestCommon.psm1') -Force

function Fail([string]$Message) { throw $Message }
function Copy-Stamped([string]$Source, [string]$Destination) {
    $dir = Split-Path -Parent $Destination
    if (!(Test-Path -LiteralPath $dir -PathType Container)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$manifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$bundleRoot = (Resolve-Path -LiteralPath $BundleRoot).Path
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$inputsPath = Join-Path $PSScriptRoot 'guest-package-inputs.json'
$inputs = Get-Content -LiteralPath $inputsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseId = [string]$manifest.release_id
if ($releaseId -ne [string]$inputs.release_id) { Fail 'inputs release_id does not match manifest' }
$dllSource = Join-Path $bundleRoot 'mod\ComfyNetworkSense.dll'
if (!(Test-Path -LiteralPath $dllSource -PathType Leaf)) { Fail 'sealed DLL is missing' }
$dllHash = Get-ComfySha256 $dllSource
if ($dllHash -ne ([string]$manifest.mod.clean_build_sha256).ToLowerInvariant()) { Fail 'sealed DLL hash does not match manifest clean_build_sha256' }
$dllBytes = [IO.File]::ReadAllBytes($dllSource)
$dllString = [Text.Encoding]::ASCII.GetString($dllBytes)
if ($dllString.Contains('LumberjacksModReleaseId')) { Fail 'sealed DLL unexpectedly carries release metadata' }

if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'scripts\lib') -Force | Out-Null
Copy-Stamped $manifestPath (Join-Path $OutputRoot 'manifest.json')
Copy-Stamped $inputsPath (Join-Path $OutputRoot 'guest-package-inputs.json')
Copy-Stamped $dllSource (Join-Path $OutputRoot 'ComfyNetworkSense.dll')
Copy-Stamped (Join-Path $PSScriptRoot 'Install-ComfyGuest.ps1') (Join-Path $OutputRoot 'scripts\Install-ComfyGuest.ps1')
Copy-Stamped (Join-Path $PSScriptRoot 'Invoke-GuestPreflight.ps1') (Join-Path $OutputRoot 'scripts\Invoke-GuestPreflight.ps1')
Copy-Stamped (Join-Path $PSScriptRoot 'Uninstall-ComfyGuest.ps1') (Join-Path $OutputRoot 'scripts\Uninstall-ComfyGuest.ps1')
Copy-Stamped (Join-Path $PSScriptRoot 'Collect-ComfyGuestDiagnostics.ps1') (Join-Path $OutputRoot 'scripts\Collect-ComfyGuestDiagnostics.ps1')
Copy-Stamped (Join-Path $PSScriptRoot 'lib\ComfyGuestCommon.psm1') (Join-Path $OutputRoot 'scripts\lib\ComfyGuestCommon.psm1')

$python = 'C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe'
$guide = & $python (Join-Path $PSScriptRoot '..\render_guest_guide.py') --manifest $manifestPath --inputs $inputsPath --output (Join-Path $OutputRoot 'GUEST-GUIDE.md')
if ($LASTEXITCODE -ne 0) { Fail 'guide renderer failed' }
$guide = & $python (Join-Path $PSScriptRoot '..\render_guest_guide.py') --manifest $manifestPath --inputs $inputsPath --output (Join-Path $OutputRoot 'GUEST-GUIDE.md') --drift-scan
if ($LASTEXITCODE -ne 0) { Fail 'guide drift scan failed' }
$captured = [DateTime]::Parse([string]$manifest.captured_utc).ToUniversalTime().ToString('o')
$entries = @()
Get-ChildItem -LiteralPath $OutputRoot -File -Recurse | Where-Object { $_.Name -ne 'guest-index.json' } | ForEach-Object {
    $relative = $_.FullName.Substring($OutputRoot.Length + 1).Replace('\','/')
    $entries += [ordered]@{ path = $relative; sha256 = Get-ComfySha256 $_.FullName; bytes = $_.Length }
}
$entries = @($entries | Sort-Object path)
$index = [ordered]@{ schema = 'comfy-guest-package/v1'; release_id = $releaseId; created_utc = $captured; manifest_sha256 = Get-ComfySha256 (Join-Path $OutputRoot 'manifest.json'); dll_release_identity = 'manifest-only; assembly metadata absent in sealed cut'; files = $entries }
$indexText = ($index | ConvertTo-Json -Depth 8)
Write-ComfyUtf8NoBom (Join-Path $OutputRoot 'guest-index.json') $indexText

if (!$NoZip) {
    $zipPath = $OutputRoot.TrimEnd('\') + '.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in @(Get-ChildItem -LiteralPath $OutputRoot -File -Recurse | Sort-Object FullName)) {
            $relative = $entry.FullName.Substring($OutputRoot.Length + 1).Replace('\','/')
            $ze = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $ze.LastWriteTime = [DateTimeOffset]::Parse($captured)
            $stream = $ze.Open(); try { $bytes = [IO.File]::ReadAllBytes($entry.FullName); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
}
Write-Output $OutputRoot
