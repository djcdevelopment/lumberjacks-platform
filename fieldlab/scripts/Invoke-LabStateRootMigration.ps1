#Requires -Version 5.1
<#
.SYNOPSIS
Cut the retained Valheim Lab server state over to the Baseline-owned state root.

.DESCRIPTION
The command is deliberately narrow: it migrates the complete active server
subtree (`config` and `data`) while preserving the newer Baseline-owned client
state. The retained source is never moved or deleted and remains the rollback
copy. Apply refuses connected players, stops the server gracefully, copies with
robocopy, compares inventory and critical world hashes, recreates only the
server from the Baseline Compose file, and requires the strict PD-7 provenance
gate to pass. If the new server cannot be admitted, the retained-state bridge is
recreated automatically.

Plan is read-only. Apply requires an explicit retained root because retired
checkout paths must never become a hidden default.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Plan', 'Apply', 'Verify')]
    [string] $Action = 'Plan',

    [Parameter(Mandatory = $true)]
    [string] $RetainedStateRoot,

    [string] $ContainerName = 'comfy-valheim-lab-valheim-server-1',

    [string] $ProjectName = 'comfy-valheim-lab',

    [string] $WorldName = 'ComfyEra16',

    [string] $GatewayUrl = 'http://127.0.0.1:4000',

    [ValidateRange(60, 900)]
    [int] $StartupTimeoutSeconds = 300,

    [switch] $ResumePartialTarget,

    [string] $ReceiptPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$autonomousRoot = Join-Path $repoRoot 'fieldlab\autonomous'
$targetStateRoot = Join-Path $autonomousRoot 'state'
$composeFile = Join-Path $autonomousRoot 'valheim-lab.compose.yml'
$bridgeFile = Join-Path $autonomousRoot 'valheim-lab.retained-state.bridge.override.yml'
$provenanceVerifier = Join-Path $repoRoot 'fieldlab\scripts\Test-LabRuntimeProvenance.ps1'
$quiescenceVerifier = Join-Path $repoRoot 'tools\workbench\Test-LocalLabQuiescence.ps1'

function Normalize-FullPath([string] $Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Test-PathWithin([string] $Path, [string] $Root) {
    $normalizedPath = Normalize-FullPath $Path
    $normalizedRoot = Normalize-FullPath $Root
    return $normalizedPath.Equals($normalizedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.StartsWith($normalizedRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-TreeInventory([string] $Path) {
    # The server image leaves one zero-byte Linux reparse marker in the Windows
    # bind mount. Robocopy intentionally excludes junction/reparse targets, so
    # the comparable inventory must exclude that marker too.
    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction Stop |
        Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    return [pscustomobject]@{
        path = Normalize-FullPath $Path
        file_count = $files.Count
        bytes = [long]$bytes
    }
}

function Move-TargetOnlyFilesToQuarantine([string] $SourceRoot, [string] $TargetRoot) {
    if (-not $ResumePartialTarget) { return @() }
    $sourceFiles = @(Get-ChildItem -LiteralPath $SourceRoot -File -Recurse -Force -ErrorAction Stop |
        Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })
    $targetFiles = @(Get-ChildItem -LiteralPath $TargetRoot -File -Recurse -Force -ErrorAction Stop |
        Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })
    $sourceRelative = @{}
    foreach ($file in $sourceFiles) {
        $sourceRelative[$file.FullName.Substring($SourceRoot.Length + 1)] = $true
    }
    $targetOnly = @($targetFiles | Where-Object {
        -not $sourceRelative.ContainsKey($_.FullName.Substring($TargetRoot.Length + 1))
    })
    if ($targetOnly.Count -eq 0) { return @() }

    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $quarantineRoot = Join-Path $targetStateRoot ("migration-quarantine\$stamp")
    $moved = @()
    foreach ($file in $targetOnly) {
        $relativePath = $file.FullName.Substring($TargetRoot.Length + 1)
        $destination = Join-Path $quarantineRoot $relativePath
        if (-not (Test-PathWithin $file.FullName $TargetRoot)) { throw "Target-only file escaped server root: $($file.FullName)" }
        if (-not (Test-PathWithin $destination $quarantineRoot)) { throw "Quarantine destination escaped its root: $destination" }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Move-Item -LiteralPath $file.FullName -Destination $destination
        $moved += [pscustomobject]@{
            relative_path = $relativePath.Replace('\', '/')
            bytes = [long]$file.Length
            quarantine_path = $destination
        }
    }
    return @($moved)
}

function Get-CriticalHashes([string] $ServerRoot) {
    $relativePaths = @(
        "config\worlds_local\$WorldName.db",
        "config\worlds_local\$WorldName.fwl",
        "config\worlds_local\$WorldName.db.old",
        "config\worlds_local\$WorldName.fwl.old"
    )
    $rows = @()
    foreach ($relativePath in $relativePaths) {
        $path = Join-Path $ServerRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            if ($relativePath -match '\.old$') { continue }
            throw "Required world file is missing: $path"
        }
        $file = Get-Item -LiteralPath $path
        $rows += [pscustomobject]@{
            relative_path = $relativePath.Replace('\', '/')
            bytes = [long]$file.Length
            last_write_utc = $file.LastWriteTimeUtc.ToString('o')
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return @($rows)
}

function Compare-CriticalHashes([object[]] $Source, [object[]] $Target) {
    foreach ($sourceRow in $Source) {
        $targetRow = @($Target | Where-Object { $_.relative_path -eq $sourceRow.relative_path })
        if ($targetRow.Count -ne 1 -or $targetRow[0].bytes -ne $sourceRow.bytes -or
            $targetRow[0].sha256 -ne $sourceRow.sha256) {
            throw "Critical file verification failed: $($sourceRow.relative_path)"
        }
    }
}

function Invoke-Compose([string] $StateRoot, [bool] $UseBridge, [string[]] $Arguments) {
    $priorAutonomous = [Environment]::GetEnvironmentVariable('AUTONOMOUS_ROOT', 'Process')
    $priorComfy = [Environment]::GetEnvironmentVariable('COMFY_ROOT', 'Process')
    $priorPassword = [Environment]::GetEnvironmentVariable('SERVER_PASS', 'Process')
    try {
        $env:AUTONOMOUS_ROOT = (Normalize-FullPath $StateRoot).Replace('\', '/')
        $env:COMFY_ROOT = (Normalize-FullPath $repoRoot).Replace('\', '/')
        $env:SERVER_PASS = ''
        $composeArguments = @('compose', '-p', $ProjectName, '-f', $composeFile)
        if ($UseBridge) { $composeArguments += @('-f', $bridgeFile) }
        $composeArguments += $Arguments
        & docker @composeArguments
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose exited ${LASTEXITCODE}: $($Arguments -join ' ')"
        }
    } finally {
        [Environment]::SetEnvironmentVariable('AUTONOMOUS_ROOT', $priorAutonomous, 'Process')
        [Environment]::SetEnvironmentVariable('COMFY_ROOT', $priorComfy, 'Process')
        [Environment]::SetEnvironmentVariable('SERVER_PASS', $priorPassword, 'Process')
    }
}

function Invoke-QuiescenceGate {
    $serverConfigRoot = Join-Path $sourceServer 'config'
    $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $quiescenceVerifier -GatewayUrl $GatewayUrl -ServerConfigRoot $serverConfigRoot 2>&1)
    $code = $LASTEXITCODE
    $text = ($output | Out-String).Trim()
    if ($code -ne 0) { throw "Local Lab quiescence gate failed: $text" }
    return ($text | ConvertFrom-Json)
}

function Wait-ServerReady {
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        $running = (& docker inspect $ContainerName --format '{{.State.Running}}' 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and $running -eq 'true') {
            $logs = @(& docker logs --tail 250 $ContainerName 2>&1)
            if ($LASTEXITCODE -eq 0 -and @($logs | Select-String -Pattern 'Connections\s+0|Game server connected').Count -gt 0) {
                return
            }
        }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    throw "Baseline server did not become ready within $StartupTimeoutSeconds seconds"
}

function Invoke-StrictProvenance {
    $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $provenanceVerifier -ContainerName $ContainerName 2>&1)
    $code = $LASTEXITCODE
    $text = ($output | Out-String).Trim()
    if ($code -ne 0) { throw "Strict Lab provenance failed: $text" }
    return ($text | ConvertFrom-Json)
}

function Write-MigrationReceipt([object] $Receipt) {
    if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
        $receiptRoot = Join-Path $targetStateRoot 'migration-receipts'
        $script:ReceiptPath = Join-Path $receiptRoot ("lab-state-migration-{0}.json" -f (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
    }
    $parent = Split-Path -Parent $ReceiptPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $temporary = "$ReceiptPath.tmp"
    [IO.File]::WriteAllText($temporary, ($Receipt | ConvertTo-Json -Depth 12) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $ReceiptPath -Force
}

$retainedRoot = Normalize-FullPath $RetainedStateRoot
$baselineRoot = Normalize-FullPath $targetStateRoot
$retainedAutonomousRoot = Split-Path -Parent $retainedRoot
$sourceServer = Join-Path $retainedRoot 'server'
$targetServer = Join-Path $baselineRoot 'server'

if (-not (Test-Path -LiteralPath $retainedRoot -PathType Container)) { throw "Retained state root not found: $retainedRoot" }
if (-not (Test-Path -LiteralPath (Join-Path $sourceServer 'config') -PathType Container)) { throw "Retained server config not found: $sourceServer" }
if (-not (Test-Path -LiteralPath (Join-Path $sourceServer 'data') -PathType Container)) { throw "Retained server data not found: $sourceServer" }
if (-not (Test-PathWithin $baselineRoot $autonomousRoot)) { throw "Target state root escaped the Baseline autonomous root: $baselineRoot" }
if (Test-PathWithin $baselineRoot $retainedRoot -or Test-PathWithin $retainedRoot $baselineRoot) { throw 'Retained and Baseline state roots overlap' }
if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) { throw "Compose file not found: $composeFile" }

$sourceInventory = Get-TreeInventory $sourceServer
$driveName = [IO.Path]::GetPathRoot($baselineRoot).TrimEnd('\').TrimEnd(':')
$drive = Get-PSDrive -Name $driveName
$requiredFree = [long]$sourceInventory.bytes + 5GB
$targetExists = Test-Path -LiteralPath $targetServer -PathType Container

$plan = [pscustomobject]@{
    schema_version = 1
    receipt_type = 'lab_state_root_migration_plan'
    action = $Action.ToLowerInvariant()
    source = $sourceInventory
    target_state_root = $baselineRoot
    target_server_exists = $targetExists
    free_bytes = [long]$drive.Free
    required_free_bytes = $requiredFree
    gateway_url = $GatewayUrl
    rollback_source_preserved = $true
    migration_scope = 'complete active server subtree; preserve Baseline-owned client state'
}

if ($Action -eq 'Plan') {
    $plan | ConvertTo-Json -Depth 8
    if (($targetExists -and -not $ResumePartialTarget) -or $drive.Free -lt $requiredFree) { exit 1 }
    exit 0
}

if ($Action -eq 'Verify') {
    if (-not $targetExists) { throw "Baseline server state is missing: $targetServer" }
    $targetInventory = Get-TreeInventory $targetServer
    if ($sourceInventory.file_count -ne $targetInventory.file_count -or $sourceInventory.bytes -ne $targetInventory.bytes) {
        throw 'Retained and Baseline server inventories differ'
    }
    $sourceHashes = Get-CriticalHashes $sourceServer
    $targetHashes = Get-CriticalHashes $targetServer
    Compare-CriticalHashes $sourceHashes $targetHashes
    $provenance = Invoke-StrictProvenance
    $receipt = [pscustomobject]@{
        schema_version = 1
        receipt_type = 'lab_state_root_migration'
        verdict = 'passed'
        verified_utc = (Get-Date).ToUniversalTime().ToString('o')
        source = $sourceInventory
        target = $targetInventory
        critical_hashes = $targetHashes
        provenance = $provenance
        rollback_source_preserved = $true
    }
    Write-MigrationReceipt $receipt
    $receipt | ConvertTo-Json -Depth 12
    exit 0
}

if ($targetExists -and -not $ResumePartialTarget) {
    throw "Baseline server state already exists; use -ResumePartialTarget only for a failed-closed copy from this command: $targetServer"
}
if ($drive.Free -lt $requiredFree) { throw "Insufficient free space: need $requiredFree bytes, have $($drive.Free)" }
$quiescence = Invoke-QuiescenceGate
$connectedPlayers = [int]$quiescence.heartbeat.peer_count
if (-not $PSCmdlet.ShouldProcess($targetServer, "Stop $ContainerName, copy retained server state, and recreate from Baseline Compose")) {
    exit 0
}

$sourceHashes = $null
$targetInventory = $null
$targetHashes = $null
$quarantinedFiles = @()
$baselineStarted = $false
try {
    & docker stop --timeout 120 $ContainerName
    if ($LASTEXITCODE -ne 0) { throw "docker stop exited $LASTEXITCODE" }
    # A graceful Valheim shutdown may write a final world save. Re-inventory
    # only after the container is stopped so the copy receipt describes the
    # immutable source snapshot actually migrated.
    $sourceInventory = Get-TreeInventory $sourceServer
    $sourceHashes = Get-CriticalHashes $sourceServer
    $quarantinedFiles = Move-TargetOnlyFilesToQuarantine $sourceServer $targetServer

    & robocopy $sourceServer $targetServer /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /MT:16 /XJ /NP /NFL /NDL
    $copyCode = $LASTEXITCODE
    if ($copyCode -ge 8) { throw "robocopy failed with exit code $copyCode" }

    $targetInventory = Get-TreeInventory $targetServer
    if ($sourceInventory.file_count -ne $targetInventory.file_count -or $sourceInventory.bytes -ne $targetInventory.bytes) {
        throw 'Post-copy inventory does not match the stopped retained server state'
    }
    $targetHashes = Get-CriticalHashes $targetServer
    Compare-CriticalHashes $sourceHashes $targetHashes

    Invoke-Compose $autonomousRoot $false @('up', '-d', '--no-deps', '--force-recreate', 'valheim-server')
    $baselineStarted = $true
    Wait-ServerReady
    $provenance = Invoke-StrictProvenance

    $receipt = [pscustomobject]@{
        schema_version = 1
        receipt_type = 'lab_state_root_migration'
        verdict = 'passed'
        migrated_utc = (Get-Date).ToUniversalTime().ToString('o')
        source = $sourceInventory
        target = $targetInventory
        critical_hashes = $targetHashes
        provenance = $provenance
        quiescence = $quiescence
        connected_players_before_stop = $connectedPlayers
        resumed_partial_target = [bool]$ResumePartialTarget
        quarantined_target_only_files = @($quarantinedFiles)
        rollback_source_preserved = $true
    }
    Write-MigrationReceipt $receipt
    $receipt | ConvertTo-Json -Depth 12
} catch {
    $failure = $_
    Write-Warning "Baseline migration failed; recreating the retained-state bridge: $($failure.Exception.Message)"
    try {
        Invoke-Compose $retainedAutonomousRoot $true @('up', '-d', '--no-deps', '--force-recreate', 'valheim-server')
    } catch {
        Write-Error "Automatic bridge restoration also failed: $($_.Exception.Message)"
    }
    throw $failure
}
