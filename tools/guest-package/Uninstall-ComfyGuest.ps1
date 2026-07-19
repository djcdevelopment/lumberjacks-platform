param(
    [Parameter(Mandatory=$true)][string]$ValheimPath,
    [string]$ReceiptPath
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ComfyGuestCommon.psm1') -Force
$bep = Join-Path $ValheimPath 'BepInEx'
if (!$ReceiptPath) { $ReceiptPath = Join-Path $bep 'comfy-guest-install.json' }
if (!(Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) { throw 'comfy guest install receipt is missing' }
$receipt = Get-Content -LiteralPath $ReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
$receiptFull = (Resolve-Path -LiteralPath $ReceiptPath).Path
$bepFull = (Resolve-Path -LiteralPath $bep).Path
foreach ($file in @($receipt.files)) {
    $path = [string]$file.path
    $full = [IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($bepFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw ('receipt path escapes BepInEx: ' + $path) }
    $backup = [string]$file.backup
    if ([bool]$file.existed) {
        if (!$backup -or !(Test-Path -LiteralPath $backup -PathType Leaf)) { throw ('receipt backup missing: ' + $path) }
        Copy-Item -LiteralPath $backup -Destination $full -Force
    } elseif (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Force
    }
}
$backupDir = [string]$receipt.backup_dir
Remove-Item -LiteralPath $receiptFull -Force
if ($backupDir -and (Test-Path -LiteralPath $backupDir)) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
$parent = Split-Path -Parent $backupDir
if ($parent -and (Test-Path -LiteralPath $parent) -and ((Get-ChildItem -LiteralPath $parent -Force | Measure-Object).Count -eq 0)) { Remove-Item -LiteralPath $parent -Force }
Write-Output 'Comfy guest package removed and receipt backups restored.'
