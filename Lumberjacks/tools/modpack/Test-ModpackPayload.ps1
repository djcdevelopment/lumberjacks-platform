#Requires -Version 5.1
<#
.SYNOPSIS
Compare a Companion modpack payload with a configured Valheim installation.

.DESCRIPTION
Validates the archive boundary, rejects duplicate or credential-config entries,
and hashes every Valheim/ payload entry against its current target. Use
-RequireExactMatch before a deliberately no-op install/rollback drill.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackagePath,

    [string]$ValheimRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch]$RequireExactMatch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$package = Get-Item -LiteralPath $PackagePath
$root = [IO.Path]::GetFullPath($ValheimRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "ValheimRoot not found: $root" }

$unsafe = [Collections.Generic.List[string]]::new()
$missing = [Collections.Generic.List[string]]::new()
$different = [Collections.Generic.List[string]]::new()
$matched = [Collections.Generic.List[string]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$payloadCount = 0

$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    foreach ($entry in $archive.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) { continue }
        $name = $entry.FullName.Replace('\', '/')
        if ($name -eq 'README.txt') { continue }
        if (-not $name.StartsWith('Valheim/', [StringComparison]::OrdinalIgnoreCase)) {
            $unsafe.Add("outside_valheim:$name")
            continue
        }

        $relative = $name.Substring('Valheim/'.Length)
        if ([string]::IsNullOrWhiteSpace($relative) -or -not $seen.Add($relative)) {
            $unsafe.Add("duplicate_or_empty:$relative")
            continue
        }
        if ($relative.EndsWith('djcdevelopment.valheim.comfynetworksense.cfg', [StringComparison]::OrdinalIgnoreCase)) {
            $unsafe.Add("credential_config_present:$relative")
            continue
        }

        $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            $unsafe.Add("path_escape:$relative")
            continue
        }

        $payloadCount++
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            $missing.Add($relative.Replace('\', '/'))
            continue
        }

        $entryStream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = ([BitConverter]::ToString($sha.ComputeHash($entryStream))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $entryStream.Dispose() }
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($entryHash -eq $targetHash) { $matched.Add($relative.Replace('\', '/')) }
        else { $different.Add($relative.Replace('\', '/')) }
    }
}
finally {
    $archive.Dispose()
}

$exact = $unsafe.Count -eq 0 -and $missing.Count -eq 0 -and $different.Count -eq 0 -and $payloadCount -gt 0
$passed = $unsafe.Count -eq 0 -and $missing.Count -eq 0 -and $payloadCount -gt 0 -and (-not $RequireExactMatch -or $exact)
[pscustomobject]@{
    schema_version = 1
    verdict = if ($passed) { 'passed' } else { 'failed' }
    package = $package.Name
    package_sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    payload_count = $payloadCount
    matched_count = $matched.Count
    different_count = $different.Count
    missing_count = $missing.Count
    unsafe_count = $unsafe.Count
    exact_live_match = $exact
    require_exact_match = [bool]$RequireExactMatch
    different_entries = @($different)
    missing_entries = @($missing)
    unsafe_entries = @($unsafe)
    valheim_files_changed = $false
} | ConvertTo-Json -Depth 6

if (-not $passed) { exit 1 }
