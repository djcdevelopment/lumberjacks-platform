#Requires -Version 5.1
<#
.SYNOPSIS
Read-only provenance and route gate for the local Valheim Lab.

.DESCRIPTION
Inspects the named Valheim server container and emits a machine-readable
receipt. The gate accepts either a fully migrated Baseline state root or an
explicitly admitted retained-state bridge. It never starts, stops, recreates,
or writes to a container. Use it before a rendered Lab window so a healthy
container cannot be mistaken for a Baseline-owned runtime.
#>
[CmdletBinding()]
param(
    [string] $ContainerName = 'comfy-valheim-lab-valheim-server-1',

    [string] $ExpectedComposeFile = '',

    [string] $ExpectedWorkingDirectory = '',

    [string] $RetainedStateRoot = '',

    [string] $OmenGatewayUrl = 'http://127.0.0.1:4000',

    [string] $I5GatewayUrl = 'http://100.124.12.37:4000',

    [switch] $AllowRetainedStateBridge,

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($ExpectedComposeFile)) {
    $ExpectedComposeFile = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.compose.yml'
}
if ([string]::IsNullOrWhiteSpace($ExpectedWorkingDirectory)) {
    $ExpectedWorkingDirectory = Join-Path $repoRoot 'fieldlab\autonomous'
}

function Normalize-PathForCompare([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    try {
        return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    } catch {
        return $Path.TrimEnd('\', '/')
    }
}

function Test-PathUnderRoot([string] $Path, [string] $Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }
    $normalizedPath = Normalize-PathForCompare $Path
    $normalizedRoot = Normalize-PathForCompare $Root
    return $normalizedPath.Equals($normalizedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.StartsWith($normalizedRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

function New-Check([string] $Name, [bool] $Passed, [string] $Detail) {
    [pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    }
}

function Test-HttpRoute([string] $Name, [string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return New-Check $Name $false 'route is empty'
    }
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'http' -or
        [string]::IsNullOrWhiteSpace($uri.Host) -or
        $uri.UserInfo.Length -gt 0) {
        return New-Check $Name $false 'route must be an absolute credential-free http URL'
    }
    return New-Check $Name $true ("$($uri.Scheme)://$($uri.Authority)$($uri.AbsolutePath)")
}

function Write-Receipt([object] $Receipt) {
    $json = $Receipt | ConvertTo-Json -Depth 12
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $parent = Split-Path -Parent $OutputPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        $temporary = "$OutputPath.tmp"
        [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $OutputPath -Force
    }
    Write-Output $json
}

if ($ContainerName -notmatch '^[A-Za-z0-9_.-]+$') {
    throw "ContainerName contains unsafe characters: $ContainerName"
}

$started = (Get-Date).ToUniversalTime().ToString('o')
$checks = @()
$container = $null
$inspectError = $null
try {
    $inspectJson = (& docker inspect $ContainerName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "docker inspect exited $LASTEXITCODE"
    }
    $container = @($inspectJson | ConvertFrom-Json)[0]
} catch {
    $inspectError = $_.Exception.Message
}

if ($null -eq $container) {
    $inspectDetail = 'docker inspect returned no container'
    if ($inspectError) { $inspectDetail = $inspectError }
    $checks += New-Check 'container_inspect' $false $inspectDetail
    $receipt = [pscustomobject]@{
        schema_version = 1
        receipt_type = 'lab_runtime_provenance'
        started_utc = $started
        ended_utc = (Get-Date).ToUniversalTime().ToString('o')
        verdict = 'failed'
        container = $ContainerName
        state_root_disposition = 'unknown'
        routes = [pscustomobject]@{ omen = $OmenGatewayUrl; i5 = $I5GatewayUrl }
        checks = @($checks)
    }
    Write-Receipt $receipt
    exit 1
}

$labels = $container.Config.Labels
$configFiles = @()
if ($labels.'com.docker.compose.project.config_files') {
    $configFiles = @([string]$labels.'com.docker.compose.project.config_files' -split ',')
}
$expectedCompose = Normalize-PathForCompare $ExpectedComposeFile
$expectedWorking = Normalize-PathForCompare $ExpectedWorkingDirectory
$labelCompose = @($configFiles | ForEach-Object { Normalize-PathForCompare $_ })
$labelWorking = Normalize-PathForCompare ([string]$labels.'com.docker.compose.project.working_dir')

$composeMatch = $labelCompose -contains $expectedCompose
$workingMatch = $labelWorking -eq $expectedWorking
$checks += New-Check 'compose_source_baseline' $composeMatch ("expected=$expectedCompose; observed=$($labelCompose -join ', ')")
$checks += New-Check 'compose_working_directory_baseline' $workingMatch ("expected=$expectedWorking; observed=$labelWorking")

$retiredConfig = @($labelCompose | Where-Object {
    $_ -match '(?i)\\work\\(comfy|lumberjacks)(\\|$)'
})
$retiredDetail = if ($retiredConfig.Count -eq 0) { 'no retired checkout in compose labels' } else { $retiredConfig -join ', ' }
$checks += New-Check 'compose_source_not_retired' ($retiredConfig.Count -eq 0) $retiredDetail

$mounts = @($container.Mounts)
$stateMounts = @($mounts | Where-Object { $_.Destination -in @('/config', '/opt/valheim') })
$stateSources = @($stateMounts | ForEach-Object { Normalize-PathForCompare ([string]$_.Source) })
$baselineStateRoot = Normalize-PathForCompare (Join-Path $expectedWorking 'state')
$retainedRoot = Normalize-PathForCompare $RetainedStateRoot
$stateDisposition = 'unknown'
$stateDetail = "observed=$($stateSources -join ', '); expected_baseline=$baselineStateRoot"
if ($stateMounts.Count -ne 2) {
    $stateDetail = "expected /config and /opt/valheim mounts; observed=$($stateMounts.Count); $stateDetail"
} elseif (($stateSources | Where-Object { -not (Test-PathUnderRoot $_ $baselineStateRoot) }).Count -eq 0) {
    $stateDisposition = 'baseline_migrated'
} elseif ($AllowRetainedStateBridge -and
          -not [string]::IsNullOrWhiteSpace($retainedRoot) -and
          ($stateSources | Where-Object { -not (Test-PathUnderRoot $_ $retainedRoot) }).Count -eq 0 -and
          @($labelCompose | Where-Object { $_ -match '(?i)retained-state\.bridge\.override\.yml$' }).Count -eq 1) {
    $stateDisposition = 'retained_legacy_bridge'
    $stateDetail = "$stateDetail; explicit bridge override is present"
}
$stateAccepted = $stateDisposition -in @('baseline_migrated', 'retained_legacy_bridge')
$checks += New-Check 'state_root_disposition' $stateAccepted ("disposition=$stateDisposition; $stateDetail")

$image = [string]$container.Config.Image
$imageId = [string]$container.Image
$imageCheck = (-not [string]::IsNullOrWhiteSpace($image)) -and ($imageId -match '^sha256:[0-9a-fA-F]{64}$')
$checks += New-Check 'server_image_digest' $imageCheck ("image=$image; image_id=$imageId")

$checks += Test-HttpRoute 'omen_gateway_route' $OmenGatewayUrl
$checks += Test-HttpRoute 'i5_gateway_route' $I5GatewayUrl

$passed = (@($checks | Where-Object { -not $_.passed }).Count -eq 0)
$verdict = 'failed'
if ($passed) { $verdict = 'passed' }
$receipt = [pscustomobject]@{
    schema_version = 1
    receipt_type = 'lab_runtime_provenance'
    started_utc = $started
    ended_utc = (Get-Date).ToUniversalTime().ToString('o')
    verdict = $verdict
    container = $ContainerName
    compose = [pscustomobject]@{
        expected_file = $ExpectedComposeFile
        config_files = $configFiles
        expected_working_directory = $ExpectedWorkingDirectory
        working_directory = [string]$labels.'com.docker.compose.project.working_dir'
    }
    server = [pscustomobject]@{
        image = $image
        image_id = $imageId
        state_root_disposition = $stateDisposition
        state_mounts = @($stateMounts | ForEach-Object {
            [pscustomobject]@{ source = [string]$_.Source; destination = [string]$_.Destination; read_write = [bool]$_.RW }
        })
    }
    routes = [pscustomobject]@{
        omen = $OmenGatewayUrl
        i5 = $I5GatewayUrl
        credentials_included = $false
    }
    checks = @($checks)
}
Write-Receipt $receipt
if (-not $passed) { exit 1 }
