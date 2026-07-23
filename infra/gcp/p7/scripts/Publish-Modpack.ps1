[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')] [string] $ReleaseId,
    [Parameter(Mandatory)] [ValidatePattern('^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')] [string] $ModRelease,
    [Parameter(Mandatory)] [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })] [string] $PackagePath,
    [string] $SshTarget = 'comfy-p7',
    [string] $RemoteRoot = '/mnt/comfy-p7/lumberjacks/modpack',
    [switch] $NoValheimRestartRequired,
    [string] $Notes = 'alpha client-pull release'
)

$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath
$hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$packageName = 'Comfy-P7-Alpha-Mods.zip'
$relativePackage = "releases/$ReleaseId/$packageName"
$manifest = [ordered]@{
    schema_version = 1
    release = $ReleaseId
    mod_release = $ModRelease
    package_kind = 'comfy_p7_alpha_modpack'
    package_file = $relativePackage
    package_sha256 = $hash
    package_size_bytes = $package.Length
    created_utc = (Get-Date).ToUniversalTime().ToString('o')
    requires_valheim_restart = -not $NoValheimRestartRequired
    notes = $Notes
}

$temporaryManifest = Join-Path ([System.IO.Path]::GetTempPath()) "lumberjacks-$ReleaseId-current.json"
try {
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryManifest -Encoding utf8NoBOM
    $temporaryPackage = "/tmp/lumberjacks-$ReleaseId-$packageName"
    $temporaryRemoteManifest = "/tmp/lumberjacks-$ReleaseId-current.json"
    & scp $package.FullName "${SshTarget}:$temporaryPackage"
    if ($LASTEXITCODE -ne 0) { throw 'package upload failed' }
    & scp $temporaryManifest "${SshTarget}:$temporaryRemoteManifest"
    if ($LASTEXITCODE -ne 0) { throw 'manifest upload failed' }

    $remoteScript = @'
set -euo pipefail
root="$1"
release="$2"
package_tmp="$3"
manifest_tmp="$4"
package_name="$5"
expected_hash="$6"
release_dir="$root/releases/$release"
mkdir -p "$release_dir"
actual_hash="$(sha256sum "$package_tmp" | awk '{print $1}')"
test "$actual_hash" = "$expected_hash"
install -m 0644 "$package_tmp" "$release_dir/$package_name"
install -m 0644 "$manifest_tmp" "$release_dir/manifest.json"
printf '%s\n' "$(cat "$manifest_tmp")" >> "$root/releases.jsonl"
cp "$manifest_tmp" "$root/current.json.tmp"
mv -f "$root/current.json.tmp" "$root/current.json"
rm -f "$package_tmp" "$manifest_tmp"
printf 'published %s %s\n' "$release" "$actual_hash"
'@
    $remoteScript | & ssh $SshTarget "bash -s -- '$RemoteRoot' '$ReleaseId' '$temporaryPackage' '$temporaryRemoteManifest' '$packageName' '$hash'"
    if ($LASTEXITCODE -ne 0) { throw 'remote publish failed' }

    [pscustomobject]@{
        release = $ReleaseId
        mod_release = $ModRelease
        package_sha256 = $hash
        package_size_bytes = $package.Length
        remote_manifest = "$RemoteRoot/current.json"
        gateway_restart_required = $false
        valheim_restart_required = -not $NoValheimRestartRequired
    }
}
finally {
    Remove-Item -LiteralPath $temporaryManifest -Force -ErrorAction SilentlyContinue
}
