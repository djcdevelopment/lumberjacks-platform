[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^companion-bootstrap-[0-9]{8}-r[0-9]+$')] [string] $ReleaseId,
    [string] $SshTarget = 'comfy-p7',
    [string] $RemoteRoot = '/mnt/comfy-p7/lumberjacks/modpack/companion-bootstrap',
    [string] $Notes = 'public credential-free Companion bootstrap'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')
$lumberjacksRoot = Join-Path $repoRoot 'Lumberjacks'
$builder = Join-Path $lumberjacksRoot 'tools\companion\New-CompanionBootstrap.ps1'
if (-not (Test-Path -LiteralPath $builder)) { throw "Companion bootstrap builder not found: $builder" }

$artifact = & $builder -ReleaseId $ReleaseId
if (-not $artifact.package -or -not $artifact.manifest) { throw 'Bootstrap builder did not return package paths.' }

$package = Get-Item -LiteralPath $artifact.package
$hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $artifact.sha256) { throw "builder hash $($artifact.sha256) did not match package hash $hash" }

$relativePackage = "releases/$ReleaseId/$($package.Name)"
$manifest = [ordered]@{
    schema_version = 1
    release = $ReleaseId
    package_kind = 'lumberjacks_companion_bootstrap'
    package_file = $relativePackage
    package_sha256 = $hash
    package_size_bytes = $package.Length
    created_utc = (Get-Date).ToUniversalTime().ToString('o')
    entrypoint = 'bootstrap/Start-LumberjacksCompanion.cmd'
    notes = $Notes
}

$temporaryManifest = Join-Path ([System.IO.Path]::GetTempPath()) "lumberjacks-$ReleaseId-companion-current.json"
try {
    [System.IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
    $temporaryPackage = "/tmp/lumberjacks-$ReleaseId-$($package.Name)"
    $temporaryRemoteManifest = "/tmp/lumberjacks-$ReleaseId-companion-current.json"
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
printf 'published companion bootstrap %s %s\n' "$release" "$actual_hash"
'@
    $encodedRemoteScript = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($remoteScript))
    & ssh $SshTarget "echo $encodedRemoteScript | base64 -d | sudo bash -s -- '$RemoteRoot' '$ReleaseId' '$temporaryPackage' '$temporaryRemoteManifest' '$($package.Name)' '$hash'"
    if ($LASTEXITCODE -ne 0) { throw 'remote publish failed' }

    [pscustomobject]@{
        release = $ReleaseId
        package_sha256 = $hash
        package_size_bytes = $package.Length
        remote_manifest = "$RemoteRoot/current.json"
        gateway_restart_required = $false
        package = $package.FullName
    }
}
finally {
    Remove-Item -LiteralPath $temporaryManifest -Force -ErrorAction SilentlyContinue
}
