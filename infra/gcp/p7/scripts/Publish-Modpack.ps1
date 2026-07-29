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
    # Windows PowerShell 5.1 does not support Set-Content -Encoding utf8NoBOM. Use the .NET
    # overload so the manifest is portable JSON (and the remote shell never receives a BOM).
    [System.IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
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
    # Do not pipe a Windows PowerShell string directly into ssh: its stream encoding can prefix a
    # UTF-8 BOM, turning `set -euo pipefail` into an unknown command on bash. Base64 is ASCII-only
    # end-to-end, and sudo owns the root-managed P7 modpack mount. A failed remote publish now
    # reliably returns non-zero instead of printing errors and falling through to a success line.
    # CRLF must not reach the remote shell: a here-string carries the .ps1's own line endings, and a
    # Windows checkout makes those CRLF, which turns `set -euo pipefail` into an invalid option name.
    # Same normalisation as Promote-GatewayImage.ps1; a no-op when the file is already LF.
    $encodedRemoteScript = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($remoteScript -replace "`r`n", "`n")))
    & ssh $SshTarget "echo $encodedRemoteScript | base64 -d | sudo bash -s -- '$RemoteRoot' '$ReleaseId' '$temporaryPackage' '$temporaryRemoteManifest' '$packageName' '$hash'"
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
