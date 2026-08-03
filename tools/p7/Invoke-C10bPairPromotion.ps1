#Requires -Version 5.1
<#
.SYNOPSIS
Promote the exact C10b Gateway/mod pair to P7 as one rollback unit.

.DESCRIPTION
Default mode is local-only dry run. -Execute requires an accepted P7 boot
receipt, snapshots the live environment plus both mod copies, invokes the
existing exact-image and frozen-DLL deployers, and verifies the pair.
ArtifactStage makes the retained pre-deletion candidate and post-deletion final
promotions distinct. Any failure restores the pre-promotion environment and both
mod copies before the error is returned.
#>
[CmdletBinding()]
param(
    [ValidateSet('candidate', 'final')]
    [string] $ArtifactStage = 'candidate',

    [string] $ReleaseId = 'm7-c10a-20260802-r41',

    [string] $GatewayImage = 'lumberjacks-gateway:m7-c10a-20260802-r41',

    [string] $ExpectedModSha256 =
        'c167ab061853043253f26af76774b58d969707b2a266272ac2d1e35ea9c2da11',

    [string] $DllPath = '',

    [string] $BootReceiptPath = '',

    [string] $OutputPath = '',

    [string] $SshTarget = 'comfy-p7',

    [switch] $Execute
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $repoRoot `
        'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
}
$dll = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
$expectedHash = $ExpectedModSha256.Trim().ToLowerInvariant()
if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
    throw 'ExpectedModSha256 must contain exactly 64 hex characters.'
}
if ($SshTarget -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'SshTarget must be an SSH alias or hostname token.'
}

$identityLibrary = Join-Path $repoRoot `
    'infra\gcp\p7\scripts\lib\ReleaseIdentity.ps1'
. $identityLibrary
$artifactRelease = Get-AssemblyMetadataValue `
    -DllPath $dll `
    -Key 'LumberjacksModReleaseId'
$artifactHash =
    (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($artifactRelease -ne $ReleaseId) {
    throw "Mod release mismatch: expected=$ReleaseId actual=$artifactRelease"
}
if ($artifactHash -ne $expectedHash) {
    throw "Mod hash mismatch: expected=$expectedHash actual=$artifactHash"
}

$artifactBoundaryVerifier = Join-Path $repoRoot `
    'tools\p7\Test-C10bArtifactFallbackBoundary.ps1'
$artifactBoundaryOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $artifactBoundaryVerifier `
    -Stage $ArtifactStage `
    -DllPath $dll `
    -ExpectedReleaseId $ReleaseId)
$artifactBoundaryVerified = $LASTEXITCODE -eq 0
if (-not $artifactBoundaryVerified) {
    $artifactBoundaryOutput | Write-Host
    throw "The '$ArtifactStage' artifact boundary failed before pair promotion."
}
$artifactBoundary =
    ($artifactBoundaryOutput -join [Environment]::NewLine) | ConvertFrom-Json
$artifactBoundarySummary = [ordered]@{
    receipt_type = [string]$artifactBoundary.receipt_type
    stage = [string]$artifactBoundary.stage
    result = [string]$artifactBoundary.result
    dll_sha256 = [string]$artifactBoundary.dll_sha256
    checks = $artifactBoundary.checks
}

$gatewayVerifier = Join-Path $repoRoot `
    'infra\gcp\p7\scripts\Test-GatewayImageRelease.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $gatewayVerifier `
    -Image $GatewayImage `
    -ExpectedRelease $ReleaseId
if ($LASTEXITCODE -ne 0) {
    throw 'Gateway image release verification failed.'
}
$candidateGatewayId =
    [string](& docker image inspect --format '{{.Id}}' $GatewayImage)
if ($LASTEXITCODE -ne 0 -or
    $candidateGatewayId -notmatch '^sha256:[0-9a-fA-F]{64}$') {
    throw 'Candidate Gateway image id could not be resolved.'
}
$candidateGatewayId = $candidateGatewayId.Trim().ToLowerInvariant()
$p7Project = 'lumberjacks-exp-20260711-djc'
$p7Zone = 'us-west1-b'
$p7Instance = 'comfy-lumberjacks-p7'

function Write-Receipt([object] $Receipt) {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $absolute = [IO.Path]::GetFullPath($OutputPath)
        $directory = Split-Path -Parent $absolute
        if ($directory) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        [IO.File]::WriteAllText(
            $absolute,
            ($Receipt | ConvertTo-Json -Depth 10) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    }
    $Receipt | ConvertTo-Json -Depth 10
}

function Invoke-NativeJob(
    [string] $Exe,
    [string[]] $Arguments,
    [int] $TimeoutSeconds) {
    $serializedArguments = ConvertTo-Json -InputObject @($Arguments) -Compress
    $job = Start-Job -ScriptBlock {
        param($program, $programArgumentsJson)
        $programArguments = [object[]](ConvertFrom-Json $programArgumentsJson)
        $output = & $program @programArguments 2>&1
        [pscustomobject]@{
            Output = (@($output) -join [Environment]::NewLine)
            Code = $LASTEXITCODE
        }
    } -ArgumentList @($Exe, $serializedArguments)
    $done = Wait-Job $job -Timeout $TimeoutSeconds
    if (-not $done) {
        Stop-Job $job
        Remove-Job $job -Force
        return $null
    }
    $result = Receive-Job $job
    Remove-Job $job -Force
    return $result
}

function Invoke-Remote(
    [string] $Script,
    [string] $Label,
    [int] $TimeoutSeconds = 600) {
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n")))
    $command = "echo $encoded | base64 -d | bash"
    $result = Invoke-NativeJob `
        -Exe 'ssh' `
        -Arguments @(
            '-n',
            '-o', 'BatchMode=yes',
            '-o', 'ConnectTimeout=15',
            '-o', 'ServerAliveInterval=15',
            '-o', 'ServerAliveCountMax=4',
            $SshTarget,
            $command) `
        -TimeoutSeconds $TimeoutSeconds
    if ($null -eq $result) { throw "$Label timed out." }
    if ($result.Code -ne 0) {
        throw "$Label failed with exit $($result.Code): $($result.Output)"
    }
    return [string]$result.Output
}

function Read-KeyValues([string] $Text) {
    $values = @{}
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '^([a-z0-9_]+)=(.*)$') {
            $values[$matches[1]] = $matches[2]
        }
    }
    return $values
}

$dryRunReceipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c10b_p7_pair_promotion'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    result = 'dry_run'
    execute = [bool]$Execute
    artifact_stage = $ArtifactStage
    release_id = $ReleaseId
    gateway_image = $GatewayImage
    gateway_image_id = $candidateGatewayId
    mod_sha256 = $artifactHash
    artifact_boundary = $artifactBoundarySummary
    target = $SshTarget
    actions = @(
        'snapshot environment and both live mod copies',
        'quiesce Gateway traffic',
        'deploy frozen exact mod DLL',
        'promote exact Gateway image',
        'verify Gateway health/image and both mod hashes',
        'restore the complete pair on any failure')
}
if (-not $Execute) {
    Write-Receipt $dryRunReceipt
    return
}

if ([string]::IsNullOrWhiteSpace($BootReceiptPath)) {
    throw '-Execute requires BootReceiptPath from the accepted boot-determinism gate.'
}
$boot =
    Get-Content -LiteralPath (Resolve-Path -LiteralPath $BootReceiptPath) `
        -Raw -Encoding utf8 |
    ConvertFrom-Json
if ([string]$boot.receipt_type -ne 'p7_boot_determinism_acceptance' -or
    [string]$boot.result -ne 'passed' -or
    [string]$boot.project -ne $p7Project -or
    [string]$boot.zone -ne $p7Zone -or
    [string]$boot.instance -ne $p7Instance -or
    [string]$boot.instance_id -notmatch '^\d+$' -or
    [string]$boot.verified_boot_id -notmatch
        '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' -or
    $boot.checks.cold_stop_start_passed -ne $true -or
    $boot.checks.retry_recovery_passed -ne $true) {
    throw 'BootReceiptPath does not prove both required P7 boot gates.'
}

$vmJson = & gcloud compute instances describe $p7Instance `
    --zone $p7Zone `
    --project $p7Project `
    --format=json
if ($LASTEXITCODE -ne 0) { throw 'P7 instance-state lookup failed.' }
$vm = ($vmJson -join [Environment]::NewLine) | ConvertFrom-Json
if ([string]$vm.status -ne 'RUNNING') {
    throw "Pair promotion requires P7 RUNNING; actual=$($vm.status)."
}
if ([string]$vm.id -ne [string]$boot.instance_id) {
    throw 'Boot receipt belongs to a different P7 VM instance id.'
}
$currentBootId = (Invoke-Remote `
    'cat /proc/sys/kernel/random/boot_id' `
    'P7 boot identity preflight' `
    60).Trim().ToLowerInvariant()
if ($currentBootId -ne [string]$boot.verified_boot_id) {
    throw "Boot receipt is stale for the current P7 boot: receipt=$($boot.verified_boot_id) current=$currentBootId"
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupRoot = "/mnt/comfy-p7/backups/c10b-pair/$stamp"
$environmentFile = '/etc/comfy-p7/environment'
$composeRoot = '/opt/comfy/infra/gcp/p7'
$gatewayContainer = 'comfy-lumberjacks-p7-gateway-1'
$valheimContainer = 'comfy-lumberjacks-p7-valheim-server-1'
$runtimeDll = '/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll'
$fallbackDll = '/mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll'
$hostConfig = '/mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg'

$snapshotScript = @"
set -euo pipefail
backup='$backupRoot'
sudo install -d -m 0750 "`$backup"
sudo cp -a '$environmentFile' "`$backup/environment"
sudo docker cp '${valheimContainer}:$runtimeDll' "`$backup/runtime.dll"
sudo cp -a '$fallbackDll' "`$backup/fallback.dll"
if sudo test -f '$hostConfig'; then sudo cp -a '$hostConfig' "`$backup/config.cfg"; fi
printf 'old_gateway_ref=%s\n' "`$(sudo docker inspect '$gatewayContainer' --format '{{.Config.Image}}')"
printf 'old_gateway_id=%s\n' "`$(sudo docker inspect '$gatewayContainer' --format '{{.Image}}')"
printf 'old_runtime_sha256=%s\n' "`$(sudo sha256sum "`$backup/runtime.dll" | awk '{print `$1}')"
printf 'old_fallback_sha256=%s\n' "`$(sudo sha256sum "`$backup/fallback.dll" | awk '{print `$1}')"
printf 'backup_root=%s\n' "`$backup"
"@
$snapshotText = Invoke-Remote $snapshotScript 'pair snapshot' 300
$snapshot = Read-KeyValues $snapshotText
foreach ($required in @(
        'old_gateway_ref',
        'old_gateway_id',
        'old_runtime_sha256',
        'old_fallback_sha256',
        'backup_root')) {
    if ([string]::IsNullOrWhiteSpace([string]$snapshot[$required])) {
        throw "Pair snapshot omitted $required."
    }
}

$promotionStarted = [DateTimeOffset]::UtcNow
try {
    [void](Invoke-Remote `
        "sudo docker stop '$gatewayContainer'" `
        'Gateway quiesce before pair promotion' `
        120)

    $modManifest = if ($OutputPath) {
        [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($OutputPath), '.mod.json')
    } else {
        Join-Path ([IO.Path]::GetTempPath()) "c10b-p7-mod-$stamp.json"
    }
    & (Join-Path $repoRoot 'infra\gcp\p7\scripts\deploy-network-sense.ps1') `
        -SshTarget $SshTarget `
        -Container $valheimContainer `
        -ArtifactPath $dll `
        -ExpectedSha256 $expectedHash `
        -ExpectedRelease $ReleaseId `
        -ManifestPath $modManifest | Write-Host

    & (Join-Path $repoRoot 'infra\gcp\p7\scripts\Promote-GatewayImage.ps1') `
        -Image $GatewayImage `
        -AdmittedModRelease $ReleaseId `
        -SshTarget $SshTarget `
        -DeferFailureRecoveryToPair | Write-Host

    $verifyScript = @"
set -euo pipefail
gateway_id="`$(sudo docker inspect '$gatewayContainer' --format '{{.Image}}')"
host_hash="`$(sudo sha256sum '$fallbackDll' | awk '{print `$1}')"
runtime_hash="`$(sudo docker exec '$valheimContainer' sha256sum '$runtimeDll' | awk '{print `$1}')"
curl --fail --silent http://127.0.0.1:4000/health | grep -q '"status":"ok"'
test "`$gateway_id" = '$candidateGatewayId'
test "`$host_hash" = '$expectedHash'
test "`$runtime_hash" = '$expectedHash'
printf 'gateway_id=%s\n' "`$gateway_id"
printf 'host_sha256=%s\n' "`$host_hash"
printf 'runtime_sha256=%s\n' "`$runtime_hash"
"@
    $verifyText = Invoke-Remote `
        $verifyScript `
        "$ArtifactStage pair verification" `
        300
    $verified = Read-KeyValues $verifyText
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'c10b_p7_pair_promotion'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        result = 'promoted'
        execute = $true
        artifact_stage = $ArtifactStage
        release_id = $ReleaseId
        gateway_image = $GatewayImage
        gateway_image_id = $verified.gateway_id
        mod_sha256 = $expectedHash
        artifact_boundary = $artifactBoundarySummary
        target = $SshTarget
        instance_id = [string]$vm.id
        verified_boot_id = $currentBootId
        promotion_sequence = @(
            'gateway_quiesced',
            'mod_promoted_and_ready',
            'gateway_promoted_and_healthy')
        backup_root = $backupRoot
        rollback_gateway_image_id = $snapshot.old_gateway_id
        rollback_mod_sha256 = [ordered]@{
            runtime = $snapshot.old_runtime_sha256
            fallback = $snapshot.old_fallback_sha256
        }
        promotion_started_utc = $promotionStarted.ToString('o')
        pair_verified = $true
    }
    Write-Receipt $receipt
} catch {
    $failure = $_.Exception.Message
    $rollbackScript = @"
set -euo pipefail
backup='$backupRoot'
sudo docker stop '$gatewayContainer' >/dev/null 2>&1 || true
sudo docker exec '$valheimContainer' supervisorctl stop valheim-server >/dev/null 2>&1 || true
sudo cp -a "`$backup/environment" '$environmentFile'
sudo docker cp "`$backup/runtime.dll" '${valheimContainer}:$runtimeDll'
sudo install -o 1000 -g 1000 -m 0644 "`$backup/fallback.dll" '$fallbackDll'
if sudo test -f "`$backup/config.cfg"; then sudo install -o 1000 -g 1000 -m 0664 "`$backup/config.cfg" '$hostConfig'; fi
started="`$(date -u +%Y-%m-%dT%H:%M:%SZ)"
sudo docker exec '$valheimContainer' supervisorctl start valheim-server
cd '$composeRoot'
sudo docker compose --env-file '$environmentFile' up -d --no-build --no-deps gateway
attempt=0
until curl --fail --silent http://127.0.0.1:4000/health | grep -q '"status":"ok"'; do
  attempt="`$((attempt + 1))"; test "`$attempt" -lt 90; sleep 2
done
attempt=0
until sudo docker logs --since "`$started" '$valheimContainer' 2>&1 | grep -q 'Game server connected'; do
  attempt="`$((attempt + 1))"; test "`$attempt" -lt 180; sleep 2
done
gateway_id="`$(sudo docker inspect '$gatewayContainer' --format '{{.Image}}')"
runtime_hash="`$(sudo docker exec '$valheimContainer' sha256sum '$runtimeDll' | awk '{print `$1}')"
fallback_hash="`$(sudo sha256sum '$fallbackDll' | awk '{print `$1}')"
test "`$gateway_id" = '$($snapshot.old_gateway_id)'
test "`$runtime_hash" = '$($snapshot.old_runtime_sha256)'
test "`$fallback_hash" = '$($snapshot.old_fallback_sha256)'
printf 'gateway_id=%s\n' "`$gateway_id"
printf 'runtime_sha256=%s\n' "`$runtime_hash"
printf 'fallback_sha256=%s\n' "`$fallback_hash"
"@
    try {
        $rollbackText = Invoke-Remote $rollbackScript 'complete pair rollback' 900
        $rollback = Read-KeyValues $rollbackText
        $rollbackPassed = $true
        $rollbackError = ''
    } catch {
        $rollback = @{}
        $rollbackPassed = $false
        $rollbackError = $_.Exception.Message
    }
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'c10b_p7_pair_promotion'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        result = if ($rollbackPassed) {
            'failed_rolled_back'
        } else {
            'failed_rollback_failed'
        }
        execute = $true
        artifact_stage = $ArtifactStage
        release_id = $ReleaseId
        artifact_boundary = $artifactBoundarySummary
        target = $SshTarget
        backup_root = $backupRoot
        failure = $failure
        rollback = [ordered]@{
            passed = $rollbackPassed
            error = $rollbackError
            gateway_image_id = $rollback.gateway_id
            runtime_sha256 = $rollback.runtime_sha256
            fallback_sha256 = $rollback.fallback_sha256
        }
    }
    Write-Receipt $receipt
    if (-not $rollbackPassed) {
        throw "The '$ArtifactStage' pair promotion failed and complete rollback also failed: $failure; rollback: $rollbackError"
    }
    throw "The '$ArtifactStage' pair promotion failed; the complete prior pair was restored: $failure"
}
