#Requires -Version 5.1
<#
.SYNOPSIS
Preflight or run an exact C10b poison-armed P7 candidate/final proof.

.DESCRIPTION
This is the one-command P7 wrapper around the native Valheim cutover harness. It
never starts P7 and never promotes artifacts. ArtifactStage makes the retained
pre-deletion candidate and post-deletion final proofs distinct. The rollback-aware
P7 promotion scripts must first install the exact Gateway image and frozen mod
DLL; this tool then verifies their identities and semantic boundary before either
rendered client is launched.
#>
[CmdletBinding()]
param(
    [ValidateSet('preflight', 'run')]
    [string] $Action = 'preflight',

    [ValidateSet('candidate', 'final')]
    [string] $ArtifactStage = 'candidate',

    [string] $RunId = '',

    [string] $ReleaseId = 'm7-c10a-20260802-r41',

    [string] $GatewayImage = 'lumberjacks-gateway:m7-c10a-20260802-r41',

    [string] $ExpectedModSha256 =
        'c167ab061853043253f26af76774b58d969707b2a266272ac2d1e35ea9c2da11',

    [string] $DllPath = '',

    [string] $BootReceiptPath = '',

    [string] $OutputPath = '',

    [ValidateRange(300, 1800)]
    [int] $WaitSeconds = 1200
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($Action -eq 'run' -and [string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = 'native-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') +
        "-c10b-p7-$ArtifactStage"
}
if (-not [string]::IsNullOrWhiteSpace($RunId) -and
    ($RunId.Length -gt 80 -or $RunId -notmatch '^[A-Za-z0-9._-]+$')) {
    throw "RunId must be an 80-character-or-shorter safe token: $RunId"
}
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $repoRoot `
        'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
}
$dll = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
$expectedHash = $ExpectedModSha256.Trim().ToLowerInvariant()
if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
    throw 'ExpectedModSha256 must contain exactly 64 lowercase or uppercase hex characters.'
}

$releaseIdentityLibrary = Join-Path $repoRoot `
    'infra\gcp\p7\scripts\lib\ReleaseIdentity.ps1'
. $releaseIdentityLibrary
$artifactRelease = Get-AssemblyMetadataValue `
    -DllPath $dll `
    -Key 'LumberjacksModReleaseId'
$actualHash =
    (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
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
    throw "The '$ArtifactStage' artifact boundary failed before any external P7 preflight."
}
$artifactBoundary =
    ($artifactBoundaryOutput -join [Environment]::NewLine) | ConvertFrom-Json
$gatewayVerifier = Join-Path $repoRoot `
    'infra\gcp\p7\scripts\Test-GatewayImageRelease.ps1'

$gatewayOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $gatewayVerifier `
    -Image $GatewayImage `
    -ExpectedRelease $ReleaseId)
$gatewayVerified = $LASTEXITCODE -eq 0
$localGatewayImageId = if ($gatewayVerified) {
    [string](& docker image inspect --format '{{.Id}}' $GatewayImage)
} else {
    ''
}
$localGatewayImageId = $localGatewayImageId.Trim().ToLowerInvariant()

$vmJson = & gcloud compute instances describe comfy-lumberjacks-p7 `
    --zone us-west1-b `
    --project lumberjacks-exp-20260711-djc `
    --format=json
if ($LASTEXITCODE -ne 0) { throw 'P7 instance-state lookup failed.' }
$vm = ($vmJson -join [Environment]::NewLine) | ConvertFrom-Json
$vmRunning = [string]$vm.status -eq 'RUNNING'
$p7Project = 'lumberjacks-exp-20260711-djc'
$p7Zone = 'us-west1-b'
$p7Instance = 'comfy-lumberjacks-p7'
$gatewayHost = [string]$vm.networkInterfaces[0].accessConfigs[0].natIP
$p7GatewayUrl = if ($gatewayHost -match '^\d{1,3}(?:\.\d{1,3}){3}$') {
    "http://${gatewayHost}:42317"
} else {
    ''
}

$bootReceiptClaimsValid = $false
$bootReceiptValid = $false
$bootReceipt = $null
if (-not [string]::IsNullOrWhiteSpace($BootReceiptPath)) {
    $resolvedBootReceipt =
        (Resolve-Path -LiteralPath $BootReceiptPath -ErrorAction Stop).Path
    $bootReceipt =
        Get-Content -LiteralPath $resolvedBootReceipt -Raw -Encoding utf8 |
        ConvertFrom-Json
    $bootReceiptClaimsValid =
        [string]$bootReceipt.receipt_type -eq 'p7_boot_determinism_acceptance' -and
        [string]$bootReceipt.result -eq 'passed' -and
        [string]$bootReceipt.project -eq $p7Project -and
        [string]$bootReceipt.zone -eq $p7Zone -and
        [string]$bootReceipt.instance -eq $p7Instance -and
        [string]$bootReceipt.instance_id -eq [string]$vm.id -and
        [string]$bootReceipt.verified_boot_id -match
            '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' -and
        $bootReceipt.checks.cold_stop_start_passed -eq $true -and
        $bootReceipt.checks.retry_recovery_passed -eq $true
}

$i5Output = @(& (Join-Path $repoRoot 'tools\i5\Test-I5Link.ps1'))
$i5Ready = $LASTEXITCODE -eq 0

$remote = [ordered]@{
    inspected = $false
    ssh = $false
    unit_active = $false
    gateway_health = $false
    boot_id = ''
    gateway_image_id = ''
    gateway_exact = $false
    mod_host_sha256 = ''
    mod_container_sha256 = ''
    mod_exact = $false
    server_ready = $false
}
if ($vmRunning) {
    & ssh -o BatchMode=yes -o ConnectTimeout=15 comfy-p7 true
    $remote.ssh = $LASTEXITCODE -eq 0
    if ($remote.ssh) {
        $remote.inspected = $true
        $bootIdOutput = @(& ssh -o BatchMode=yes comfy-p7 `
            'cat /proc/sys/kernel/random/boot_id')
        if ($LASTEXITCODE -eq 0) {
            $remote.boot_id = ($bootIdOutput -join '').Trim().ToLowerInvariant()
        }
        $unitState = @(& ssh -o BatchMode=yes comfy-p7 `
            'systemctl is-active comfy-lumberjacks-p7')
        $remote.unit_active =
            $LASTEXITCODE -eq 0 -and ($unitState -join '').Trim() -eq 'active'

        $gatewayInspect = @(& ssh -o BatchMode=yes comfy-p7 `
            "sudo docker inspect --format '{{.Image}}' 'comfy-lumberjacks-p7-gateway-1'")
        if ($LASTEXITCODE -eq 0) {
            $remote.gateway_image_id =
                [regex]::Match(($gatewayInspect -join "`n"),
                    'sha256:[0-9a-fA-F]{64}').Value.ToLowerInvariant()
        }
        $remote.gateway_exact =
            -not [string]::IsNullOrWhiteSpace($localGatewayImageId) -and
            $remote.gateway_image_id -eq $localGatewayImageId

        $hostHash = @(& ssh -o BatchMode=yes comfy-p7 `
            "sudo sha256sum '/mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll'")
        if ($LASTEXITCODE -eq 0) {
            $remote.mod_host_sha256 =
                (($hostHash -join "`n") -split '\s+')[0].ToLowerInvariant()
        }
        $containerHash = @(& ssh -o BatchMode=yes comfy-p7 `
            "sudo docker exec 'comfy-lumberjacks-p7-valheim-server-1' sha256sum '/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll'")
        if ($LASTEXITCODE -eq 0) {
            $remote.mod_container_sha256 =
                (($containerHash -join "`n") -split '\s+')[0].ToLowerInvariant()
        }
        $remote.mod_exact =
            $remote.mod_host_sha256 -eq $expectedHash -and
            $remote.mod_container_sha256 -eq $expectedHash

        $serverLogs = @(& ssh -o BatchMode=yes comfy-p7 `
            "sudo docker logs --since 24h 'comfy-lumberjacks-p7-valheim-server-1' 2>&1")
        $remote.server_ready =
            $LASTEXITCODE -eq 0 -and
            ($serverLogs -join "`n") -match 'Game server connected'
        try {
            $health = Invoke-RestMethod `
                -Uri "$p7GatewayUrl/health" `
                -TimeoutSec 15
            $remote.gateway_health = [string]$health.status -eq 'ok'
        } catch {
            $remote.gateway_health = $false
        }
    }
}
$bootReceiptValid =
    $bootReceiptClaimsValid -and
    $vmRunning -and
    [string]$remote.boot_id -eq [string]$bootReceipt.verified_boot_id

$checks = [ordered]@{
    release_id_matches_dll = $artifactRelease -eq $ReleaseId
    local_mod_hash_exact = $actualHash -eq $expectedHash
    artifact_fallback_boundary_exact = $artifactBoundaryVerified
    local_gateway_release_verified = $gatewayVerified
    local_gateway_image_present =
        $localGatewayImageId -match '^sha256:[0-9a-f]{64}$'
    i5_lane_ready = $i5Ready
    p7_boot_gate_receipt = $bootReceiptValid
    p7_running = $vmRunning
    p7_ssh_ready = [bool]$remote.ssh
    p7_unit_active = [bool]$remote.unit_active
    p7_gateway_health = [bool]$remote.gateway_health
    p7_gateway_image_exact = [bool]$remote.gateway_exact
    p7_mod_artifact_exact = [bool]$remote.mod_exact
    p7_valheim_ready = [bool]$remote.server_ready
}
$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = "c10b_p7_${ArtifactStage}_preflight"
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    action = $Action
    artifact_stage = $ArtifactStage
    run_id = $RunId
    release_id = $ReleaseId
    gateway_image = $GatewayImage
    gateway_image_id = $localGatewayImageId
    mod_sha256 = $actualHash
    artifact_boundary = [ordered]@{
        receipt_type = [string]$artifactBoundary.receipt_type
        stage = [string]$artifactBoundary.stage
        result = [string]$artifactBoundary.result
        dll_sha256 = [string]$artifactBoundary.dll_sha256
        checks = $artifactBoundary.checks
    }
    p7_status = [string]$vm.status
    p7_gateway_url = $p7GatewayUrl
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    remote = $remote
    boot_gate = [ordered]@{
        runbook = 'infra/gcp/p7/RUNBOOK-boot-determinism.md'
        receipt_path = $BootReceiptPath
        receipt_boot_id = if ($bootReceipt) {
            [string]$bootReceipt.verified_boot_id
        } else { '' }
        current_boot_id = [string]$remote.boot_id
        valid = $bootReceiptValid
    }
    result = if ($failed.Count -eq 0) { 'passed' } else { 'not_ready' }
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $absoluteOutput
    if ($outputDirectory) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $absoluteOutput,
        ($receipt | ConvertTo-Json -Depth 10) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}
$receipt | ConvertTo-Json -Depth 10

if ($Action -eq 'preflight') {
    if ($failed.Count -gt 0) { exit 3 }
    return
}
if ($failed.Count -gt 0) {
    throw "C10b P7 '$ArtifactStage' proof refused because the fail-closed preflight is not green."
}
$runRoot = Join-Path $repoRoot 'fieldlab\runs\native-valheim'
$scenarioPath = Join-Path (Join-Path $runRoot $RunId) 'scenario-input.json'
& (Join-Path $repoRoot 'fieldlab\scripts\New-NativeValheimCutoverScenario.ps1') `
    -RunId $RunId `
    -Profile c8 `
    -OutputPath $scenarioPath | Write-Host

& (Join-Path $repoRoot 'fieldlab\scripts\Invoke-NativeValheimCutoverScenario.ps1') `
    -RunId $RunId `
    -ScenarioPath $scenarioPath `
    -ArtifactStage $ArtifactStage `
    -Server 'comfy-p7.duckdns.org:2456' `
    -ServerSshTarget 'comfy-p7' `
    -ServerBepInExConfigRoot '/mnt/comfy-p7/valheim/config/bepinex' `
    -ServerContainerPluginPath '/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll' `
    -ServerDockerRequiresSudo `
    -UseRemoteGateway `
    -RemoteGatewayContainer 'comfy-lumberjacks-p7-gateway-1' `
    -SkipServerDeploy `
    -OmenGatewayUrl $p7GatewayUrl `
    -I5GatewayUrl $p7GatewayUrl `
    -DllPath $dll `
    -EvidenceRoot $runRoot `
    -WaitSeconds $WaitSeconds `
    -EnableC8Composition `
    -GatewayImage $GatewayImage `
    -ServerGatewayUrl 'http://gateway:4000' `
    -ServerContainer 'comfy-lumberjacks-p7-valheim-server-1' `
    -ServerWorldDb '/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.db' `
    -ServerWorldFwl '/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.fwl'
if ($LASTEXITCODE -ne 0) {
    throw "C10b P7 '$ArtifactStage' proof failed with exit $LASTEXITCODE."
}
