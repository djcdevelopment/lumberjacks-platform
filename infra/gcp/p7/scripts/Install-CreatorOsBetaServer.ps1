#Requires -Version 5.1
<#
.SYNOPSIS
Verify, upload, install, and optionally activate one frozen CreatorOS Beta 1 server release.

.DESCRIPTION
The release directory is an artifact boundary, not a sibling-source checkout. This script
independently checks every declared byte and the native-network/strict-handshake controls before
upload. The VM-side installer preserves the old world/config bytes under the P7 backup root.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string] $ReleaseDir,
    [string] $SshTarget = 'comfy-p7',
    [switch] $Activate,
    [switch] $AllowCandidate
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null
$release = (Resolve-Path -LiteralPath $ReleaseDir).Path
$verifier = Join-Path $PSScriptRoot 'verify-creatoros-beta-server-release.py'
$verifyArguments = @($verifier, '--release-dir', $release)
if ($AllowCandidate) { $verifyArguments += '--allow-candidate' }
$verificationText = @(& python @verifyArguments)
if ($LASTEXITCODE -ne 0) { throw 'CreatorOS server release verification failed.' }
$verification = ($verificationText -join [Environment]::NewLine) | ConvertFrom-Json
if ([string]$verification.status -ne 'valid') { throw 'CreatorOS server verification returned a non-valid result.' }
$releaseHash = [string]$verification.release_manifest_sha256
if ($releaseHash -notmatch '^[0-9a-f]{64}$') { throw 'Verified release hash is invalid.' }
$action = if ($Activate) { 'install and activate' } else { 'install without activation' }
if (-not $PSCmdlet.ShouldProcess("$SshTarget CreatorOSBeta1/$releaseHash", $action)) { return }

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$tempRoot = Join-Path $tempBase ("creatoros-p7-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $archive = Join-Path $tempRoot 'server.tar.gz'
    & tar -czf $archive -C $release server
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the CreatorOS server archive.' }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()

    $controlsRoot = Join-Path $tempRoot 'controls'
    $controlsScripts = Join-Path $controlsRoot 'scripts'
    New-Item -ItemType Directory -Path $controlsScripts -Force | Out-Null
    $controlSources = @(
        [pscustomobject]@{
            Source = Join-Path $repoRoot 'infra\gcp\p7\docker-compose.yml'
            Relative = 'controls/docker-compose.yml'
            Destination = Join-Path $controlsRoot 'docker-compose.yml'
            Target = '/opt/comfy/infra/gcp/p7/docker-compose.yml'
            Mode = '0644'
        },
        [pscustomobject]@{
            Source = Join-Path $repoRoot 'infra\gcp\p7\comfy-lumberjacks-p7.service'
            Relative = 'controls/comfy-lumberjacks-p7.service'
            Destination = Join-Path $controlsRoot 'comfy-lumberjacks-p7.service'
            Target = '/etc/systemd/system/comfy-lumberjacks-p7.service'
            Mode = '0644'
        },
        [pscustomobject]@{
            Source = Join-Path $PSScriptRoot 'stop-p7-stack.sh'
            Relative = 'controls/scripts/stop-p7-stack.sh'
            Destination = Join-Path $controlsScripts 'stop-p7-stack.sh'
            Target = '/opt/comfy/infra/gcp/p7/scripts/stop-p7-stack.sh'
            Mode = '0755'
        }
    )
    foreach ($control in $controlSources) {
        if (-not (Test-Path -LiteralPath $control.Source -PathType Leaf)) {
            throw "CreatorOS platform control is missing: $($control.Source)"
        }
        Copy-Item -LiteralPath $control.Source -Destination $control.Destination
    }
    $controlsArchive = Join-Path $tempRoot 'controls.tar.gz'
    & tar -czf $controlsArchive -C $tempRoot controls
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the CreatorOS platform-controls archive.' }
    $controlsArchiveHash = (Get-FileHash -LiteralPath $controlsArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $remoteInstallerSource = Join-Path $PSScriptRoot 'install-creatoros-beta-server.sh'
    $remoteInstallerHash = (Get-FileHash -LiteralPath $remoteInstallerSource -Algorithm SHA256).Hash.ToLowerInvariant()
    $controlFiles = @($controlSources | ForEach-Object {
        $item = Get-Item -LiteralPath $_.Destination
        [ordered]@{
            path = $_.Relative
            target = $_.Target
            sha256 = (Get-FileHash -LiteralPath $_.Destination -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = [long]$item.Length
            mode = $_.Mode
        }
    })
    $deployment = [ordered]@{
        schema = 'comfy-p7-creatoros-deploy/v2'
        created_utc = (Get-Date).ToUniversalTime().ToString('o')
        archive_sha256 = $archiveHash
        controls_archive_sha256 = $controlsArchiveHash
        installer_sha256 = $remoteInstallerHash
        control_files = $controlFiles
        verification = $verification
    }
    $deploymentPath = Join-Path $tempRoot 'deployment-manifest.json'
    [IO.File]::WriteAllText(
        $deploymentPath,
        ($deployment | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $remotePrefix = "/tmp/creatoros-beta1-$releaseHash"
    $remoteArchive = "$remotePrefix-server.tar.gz"
    $remoteControls = "$remotePrefix-controls.tar.gz"
    $remoteManifest = "$remotePrefix-deployment.json"
    $remoteInstaller = "$remotePrefix-install.sh"

    & scp $archive "${SshTarget}:$remoteArchive"
    if ($LASTEXITCODE -ne 0) { throw 'CreatorOS server archive upload failed.' }
    & scp $controlsArchive "${SshTarget}:$remoteControls"
    if ($LASTEXITCODE -ne 0) { throw 'CreatorOS platform-controls upload failed.' }
    & scp $deploymentPath "${SshTarget}:$remoteManifest"
    if ($LASTEXITCODE -ne 0) { throw 'CreatorOS deployment manifest upload failed.' }
    & scp $remoteInstallerSource "${SshTarget}:$remoteInstaller"
    if ($LASTEXITCODE -ne 0) { throw 'CreatorOS remote installer upload failed.' }
    $activateText = if ($Activate) { 'true' } else { 'false' }
    $command = "sudo bash '$remoteInstaller' '$remoteArchive' '$remoteControls' '$remoteManifest' '$activateText'"
    $receiptPath = @(& ssh -o BatchMode=yes -o ConnectTimeout=8 $SshTarget $command) | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'CreatorOS P7 installation failed; inspect the retained upload and VM logs.' }
    if ([string]::IsNullOrWhiteSpace([string]$receiptPath) -or $receiptPath -notmatch '^/mnt/comfy-p7/evidence/creatoros-beta1/[A-Za-z0-9._/-]+\.json$') {
        throw 'CreatorOS P7 installer returned an unsafe receipt path.'
    }
    $receiptText = @(& ssh -o BatchMode=yes -o ConnectTimeout=8 $SshTarget "sudo cat '$receiptPath'")
    if ($LASTEXITCODE -ne 0) { throw 'CreatorOS P7 receipt could not be read.' }
    $receipt = ($receiptText -join [Environment]::NewLine) | ConvertFrom-Json
    $expectedStatus = if ($Activate) { 'active' } else { 'installed' }
    if ([string]$receipt.schema -ne 'comfy-p7-creatoros-install/v1' -or
        [string]$receipt.status -ne $expectedStatus -or
        [string]$receipt.release_manifest_sha256 -ne $releaseHash -or
        [string]$receipt.controls_archive_sha256 -ne $controlsArchiveHash -or
        [string]$receipt.installer_sha256 -ne $remoteInstallerHash -or
        [bool]$receipt.platform_controls_verified -ne $true -or
        [bool]$receipt.strict_release -ne $true -or
        [bool]$receipt.telemetry_secret_injected -ne $true -or
        ($Activate -and [bool]$receipt.telemetry_heartbeat_ready -ne $true) -or
        [string]$receipt.world_uid -ne [string]$verification.world_uid -or
        [string]$receipt.world_pair_hash -ne [string]$verification.world_pair_hash) {
        throw 'CreatorOS P7 receipt identity or status drifted.'
    }
    [pscustomobject]@{
        target = $SshTarget
        status = [string]$receipt.status
        release_manifest_sha256 = $releaseHash
        world_uid = [string]$receipt.world_uid
        world_pair_hash = [string]$receipt.world_pair_hash
        controls_archive_sha256 = [string]$receipt.controls_archive_sha256
        installer_sha256 = [string]$receipt.installer_sha256
        receipt = [string]$receiptPath
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -match '^creatoros-p7-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
