<#
.SYNOPSIS
Run the existing Workbench privacy scanner against one public-safe support capsule.

The deny-list scanner intentionally accepts directories/zips, while the Workbench
runner produces one JSON capsule. This adapter stages exactly that one file under a
temporary directory and removes the staging directory after the bounded scan.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Path).Path
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "support export is not a file: $source" }
$scanner = Join-Path $PSScriptRoot 'Test-WorkbenchZipPrivacy.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("workbench-support-scan-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $scratch 'support-capsule.json')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scanner -Path $scratch
    if ($LASTEXITCODE -ne 0) { throw "public-safe support export privacy scan failed (exit $LASTEXITCODE)" }
    [pscustomobject]@{ schema_version = 1; verdict = 'public_safe_support_export_clean'; file = $source }
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
