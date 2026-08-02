#Requires -Version 5.1
<#
.SYNOPSIS
Run a bounded, evidence-producing Workbench mod install-to-rollback drill.

.DESCRIPTION
This is the engineering verifier for the browser's existing install and rollback
capabilities; the Workbench remains the operator control surface. The script first
requires a clean, exact package-to-live-payload match, a stopped Valheim process,
an identity-stamped Workbench image, and a matching admitted Gateway manifest.

Without -ApprovePlayerImpactingDrill it performs only the read-only preflight and
returns authorization_required. With approval it creates one install job and one
rollback job through the Workbench API, validates the intermediate transaction,
and proves that both the live bytes and installed-release JSON return exactly to
their starting state. If the Workbench rollback fails after an installed candidate
is observed, one direct Companion rollback is attempted as a recovery action and
the overall drill remains failed.

This script never launches Valheim, starts a transport capture, or contacts GCP.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$ExpectedRelease,

    [string]$CompanionUrl = 'http://127.0.0.1:8080',
    [string]$ContainerName = 'lumberjacks-companion-companion-1',
    [string]$ValheimRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [ValidateSet('OMEN')]
    [string]$Target = 'OMEN',
    [switch]$ApprovePlayerImpactingDrill
)

$ErrorActionPreference = 'Stop'
$CompanionUrl = $CompanionUrl.TrimEnd('/')
$verifier = (Resolve-Path (Join-Path $PSScriptRoot 'Test-ModpackPayload.ps1')).Path
$failures = [Collections.Generic.List[string]]::new()

function Get-ExactPreflight {
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $verifier,
        '-PackagePath', (Resolve-Path -LiteralPath $PackagePath).Path,
        '-ValheimRoot', $ValheimRoot,
        '-RequireExactMatch'
    )
    $output = & powershell.exe @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "exact-match preflight failed: $output" }
    try { return $output | ConvertFrom-Json }
    catch { throw "exact-match preflight returned invalid JSON: $output" }
}

function Get-CompanionStatus {
    Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/status" -TimeoutSec 15
}

function Get-TempResidueCount {
    @(Get-ChildItem -LiteralPath $ValheimRoot -Recurse -File -Filter '*.lumberjacks-*.tmp' -ErrorAction Stop).Count
}

function Invoke-WorkbenchJob {
    param([Parameter(Mandatory)][string]$CapabilityId)

    $security = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/security" -TimeoutSec 15
    if ([string]::IsNullOrWhiteSpace($security.browser_token)) { throw 'Workbench browser token is unavailable' }
    $headers = @{ 'X-Workbench-Token' = $security.browser_token }
    $body = @{ target = $Target; inputs = @{ game_closed_confirmed = $true } } |
        ConvertTo-Json -Depth 6 -Compress
    $created = Invoke-RestMethod `
        -Uri "$CompanionUrl/api/v1/workbench/capabilities/$CapabilityId/jobs" `
        -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 20
    if ([string]::IsNullOrWhiteSpace($created.job_id)) { throw "$CapabilityId did not return a job ID" }

    $job = $null
    for ($index = 0; $index -lt 240; $index++) {
        $job = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/jobs/$($created.job_id)" -TimeoutSec 15
        if ($job.state -in @('succeeded', 'failed', 'cancelled', 'interrupted')) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $job -or $job.state -notin @('succeeded', 'failed', 'cancelled', 'interrupted')) {
        throw "$CapabilityId job $($created.job_id) did not reach a terminal state"
    }
    $receipt = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/jobs/$($created.job_id)/receipt" -TimeoutSec 15
    [pscustomobject]@{
        job_id = $created.job_id
        state = $job.state
        reason_code = $receipt.reason_code
        receipt = $receipt
    }
}

function Write-ResultAndExit {
    param([Parameter(Mandatory)]$Result, [int]$ExitCode)
    $Result | ConvertTo-Json -Depth 30
    exit $ExitCode
}

$projection = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench" -TimeoutSec 15
$statusBefore = Get-CompanionStatus
$preflightBefore = Get-ExactPreflight
$manifest = Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/update/check" -TimeoutSec 30
$processesBefore = @(Get-Process valheim, valheim_server -ErrorAction SilentlyContinue).Count
$tempBefore = Get-TempResidueCount
$installedBeforeJson = $statusBefore.installed | ConvertTo-Json -Depth 30 -Compress
$packageFile = Get-Item -LiteralPath $PackagePath

if ($projection.source.source_dirty.ToString().ToLowerInvariant() -ne 'false') {
    $failures.Add('workbench_source_not_clean')
}
if ($projection.profile.effective -notin @('Admin', 'Dev', 'Lab', 'Production')) {
    $failures.Add("profile_not_eligible:$($projection.profile.effective)")
}
if ($processesBefore -ne 0) { $failures.Add("valheim_processes_running:$processesBefore") }
if ($tempBefore -ne 0) { $failures.Add("preexisting_temp_residue:$tempBefore") }
if ($null -eq $statusBefore.installed) { $failures.Add('installed_release_missing') }
if ($statusBefore.installed.release -eq $ExpectedRelease) { $failures.Add('candidate_already_installed') }
if (-not $preflightBefore.exact_live_match -or $preflightBefore.payload_count -le 0) {
    $failures.Add('package_not_exact_live_match')
}
if ($manifest.release -ne $ExpectedRelease) {
    $failures.Add("manifest_release_mismatch:$($manifest.release)")
}
if ($manifest.package.sha256 -ne $preflightBefore.package_sha256) {
    $failures.Add('manifest_package_hash_mismatch')
}
if ([long]$manifest.package.size_bytes -ne [long]$packageFile.Length) {
    $failures.Add('manifest_package_size_mismatch')
}

$preflightResult = [ordered]@{
    schema_version = 1
    verdict = if ($failures.Count -eq 0) { 'ready' } else { 'failed' }
    mode = 'preflight'
    mutations_executed = $false
    expected_release = $ExpectedRelease
    package_sha256 = $preflightBefore.package_sha256
    payload_count = $preflightBefore.payload_count
    matched_count = $preflightBefore.matched_count
    different_count = $preflightBefore.different_count
    missing_count = $preflightBefore.missing_count
    unsafe_count = $preflightBefore.unsafe_count
    installed_release = $statusBefore.installed.release
    valheim_processes = $processesBefore
    temp_residue_count = $tempBefore
    source_revision = $projection.source.source_revision
    source_dirty = $projection.source.source_dirty
    effective_profile = $projection.profile.effective
    failures = @($failures)
}

if ($failures.Count -gt 0) { Write-ResultAndExit -Result $preflightResult -ExitCode 1 }
if (-not $ApprovePlayerImpactingDrill) {
    $preflightResult.verdict = 'authorization_required'
    Write-ResultAndExit -Result $preflightResult -ExitCode 2
}

$install = $null
$rollback = $null
$installStatus = $null
$installPreflight = $null
$candidateBackupExists = $false
$recoveryFallbackUsed = $false

try {
    $install = Invoke-WorkbenchJob -CapabilityId 'operate.mod.install'
    if ($install.state -ne 'succeeded') {
        $failures.Add("install_job_$($install.state):$($install.reason_code)")
    }

    $installStatus = Get-CompanionStatus
    $installPreflight = Get-ExactPreflight
    if ($installStatus.installed.release -ne $ExpectedRelease) {
        $failures.Add("installed_release_mismatch:$($installStatus.installed.release)")
    }
    if ($installStatus.installed.transaction_schema_version -ne 1) {
        $failures.Add('transaction_schema_version_not_1')
    }
    if (@($installStatus.installed.created_files).Count -ne 0) {
        $failures.Add('exact_match_install_recorded_created_files')
    }
    $previousJson = $installStatus.installed.previous | ConvertTo-Json -Depth 30 -Compress
    if ($previousJson -cne $installedBeforeJson) {
        $failures.Add('previous_installed_state_not_exact')
    }
    if (-not $installPreflight.exact_live_match -or
        $installPreflight.matched_count -ne $preflightBefore.payload_count) {
        $failures.Add('post_install_bytes_not_exact')
    }
    & docker exec $ContainerName test -d $installStatus.installed.backup_path
    $candidateBackupExists = $LASTEXITCODE -eq 0
    if (-not $candidateBackupExists) { $failures.Add('candidate_backup_missing') }
}
catch {
    $failures.Add("install_or_verification_exception:$($_.Exception.Message)")
}
finally {
    $current = $null
    try { $current = Get-CompanionStatus }
    catch { $failures.Add("pre_rollback_status_unavailable:$($_.Exception.Message)") }
    $mustRollback = ($null -ne $install -and $install.state -eq 'succeeded') -or
        ($null -ne $current -and $current.installed.release -eq $ExpectedRelease)
    if ($mustRollback) {
        try { $rollback = Invoke-WorkbenchJob -CapabilityId 'operate.mod.rollback' }
        catch { $failures.Add("rollback_job_exception:$($_.Exception.Message)") }

        try { $current = Get-CompanionStatus }
        catch { $current = $null }
        if ($null -ne $current -and $current.installed.release -eq $ExpectedRelease) {
            $recoveryFallbackUsed = $true
            try {
                Invoke-RestMethod `
                    -Uri "$CompanionUrl/api/v0/companion/update/rollback" `
                    -Method Post -ContentType 'application/json' `
                    -Body '{"game_closed_confirmed":true}' -TimeoutSec 60 | Out-Null
            }
            catch { $failures.Add("recovery_rollback_failed:$($_.Exception.Message)") }
        }
    }
    else {
        $failures.Add('install_never_reached_rollback_eligible_state')
    }
}

$statusAfter = Get-CompanionStatus
$preflightAfter = Get-ExactPreflight
$installedAfterJson = $statusAfter.installed | ConvertTo-Json -Depth 30 -Compress
$processesAfter = @(Get-Process valheim, valheim_server -ErrorAction SilentlyContinue).Count
$tempAfter = Get-TempResidueCount

if ($null -eq $rollback -or $rollback.state -ne 'succeeded') {
    $state = if ($null -eq $rollback) { 'missing' } else { $rollback.state }
    $reason = if ($null -eq $rollback) { 'missing' } else { $rollback.reason_code }
    $failures.Add("rollback_job_not_succeeded:$state`:$reason")
}
if ($recoveryFallbackUsed) { $failures.Add('recovery_fallback_was_required') }
if ($installedAfterJson -cne $installedBeforeJson) { $failures.Add('installed_state_not_exactly_restored') }
if (-not $preflightAfter.exact_live_match -or
    $preflightAfter.matched_count -ne $preflightBefore.payload_count) {
    $failures.Add('post_rollback_bytes_not_exact')
}
if ($processesAfter -ne 0) { $failures.Add("valheim_process_count_changed:$processesAfter") }
if ($tempAfter -ne 0) { $failures.Add("temporary_residue_remains:$tempAfter") }

$result = [ordered]@{
    schema_version = 1
    verdict = if ($failures.Count -eq 0) { 'passed' } else { 'failed' }
    mode = 'install_to_rollback'
    mutations_executed = $true
    observed_utc = [DateTimeOffset]::UtcNow.ToString('O')
    candidate_release = $ExpectedRelease
    package_sha256 = $preflightBefore.package_sha256
    source_revision = $projection.source.source_revision
    before = [ordered]@{
        installed_release = $statusBefore.installed.release
        installed_package_sha256 = $statusBefore.installed.package_sha256
        matched = $preflightBefore.matched_count
        different = $preflightBefore.different_count
        missing = $preflightBefore.missing_count
        unsafe = $preflightBefore.unsafe_count
        valheim_processes = $processesBefore
        temp_residue = $tempBefore
    }
    install = [ordered]@{
        job_id = if ($null -eq $install) { $null } else { $install.job_id }
        state = if ($null -eq $install) { $null } else { $install.state }
        reason_code = if ($null -eq $install) { $null } else { $install.reason_code }
        installed_release = if ($null -eq $installStatus) { $null } else { $installStatus.installed.release }
        transaction_schema_version = if ($null -eq $installStatus) { $null } else { $installStatus.installed.transaction_schema_version }
        previous_release = if ($null -eq $installStatus) { $null } else { $installStatus.installed.previous.release }
        created_files = if ($null -eq $installStatus) { $null } else { @($installStatus.installed.created_files).Count }
        backup_path = if ($null -eq $installStatus) { $null } else { $installStatus.installed.backup_path }
        backup_exists = $candidateBackupExists
        matched = if ($null -eq $installPreflight) { $null } else { $installPreflight.matched_count }
        different = if ($null -eq $installPreflight) { $null } else { $installPreflight.different_count }
        missing = if ($null -eq $installPreflight) { $null } else { $installPreflight.missing_count }
        unsafe = if ($null -eq $installPreflight) { $null } else { $installPreflight.unsafe_count }
    }
    rollback = [ordered]@{
        job_id = if ($null -eq $rollback) { $null } else { $rollback.job_id }
        state = if ($null -eq $rollback) { $null } else { $rollback.state }
        reason_code = if ($null -eq $rollback) { $null } else { $rollback.reason_code }
        recovery_fallback_used = $recoveryFallbackUsed
    }
    after = [ordered]@{
        installed_release = $statusAfter.installed.release
        installed_package_sha256 = $statusAfter.installed.package_sha256
        installed_state_exactly_restored = $installedAfterJson -ceq $installedBeforeJson
        matched = $preflightAfter.matched_count
        different = $preflightAfter.different_count
        missing = $preflightAfter.missing_count
        unsafe = $preflightAfter.unsafe_count
        valheim_processes = $processesAfter
        temp_residue = $tempAfter
    }
    failures = @($failures)
}

Write-ResultAndExit -Result $result -ExitCode $(if ($failures.Count -eq 0) { 0 } else { 1 })
