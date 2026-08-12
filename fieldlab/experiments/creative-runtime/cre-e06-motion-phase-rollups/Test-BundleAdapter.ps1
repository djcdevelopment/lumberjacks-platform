<#
.SYNOPSIS
Prove the two-client motion-phase bundle adapter without live Valheim clients.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'captures/cre-e06-bundle-adapter-fixtures'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity | Out-Null
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$adapterScript = Join-Path $repoRoot 'fieldlab\scripts\Summarize-TwoClientMotionPhaseBundles.ps1'

function New-FixtureBundle {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $FixtureName
    )

    $fixturePath = Join-Path $PSScriptRoot "fixtures\$FixtureName"
    $stagingRoot = Join-Path $OutputDirectory "fixture-$Name"
    $stagedSamples = Join-Path $stagingRoot 'samples.jsonl'
    $bundlePath = Join-Path $OutputDirectory "$Name.zip"
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    Copy-Item -LiteralPath $fixturePath -Destination $stagedSamples -Force
    Compress-Archive -LiteralPath $stagedSamples -DestinationPath $bundlePath -Force
    return $bundlePath
}

$legacyBundle = New-FixtureBundle -Name 'legacy' -FixtureName 'samples.jsonl'
$applyBundle = New-FixtureBundle -Name 'apply-transition' -FixtureName 'apply-role-transition.jsonl'
$observeBundle = New-FixtureBundle -Name 'observe-transition' -FixtureName 'observe-role-transition.jsonl'
$contradictoryObserveBundle = New-FixtureBundle -Name 'observe-contradiction' -FixtureName 'observe-contradiction.jsonl'

function Invoke-Adapter {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $OmenBundle,
        [string] $I5Bundle,
        [switch] $OmitI5
    )

    $caseRoot = Join-Path $OutputDirectory $Name
    $receiptPath = Join-Path $caseRoot 'receipt.json'
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $adapterScript,
        '-OmenBundlePath', $OmenBundle,
        '-OutputDirectory', $caseRoot,
        '-OutputJson', $receiptPath
    )
    if (-not $OmitI5) {
        $arguments += @('-I5BundlePath', $I5Bundle)
    }
    $processOutput = (& powershell.exe @arguments 2>&1 | Out-String).Trim()
    $exitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "$Name did not write a receipt (exit=$exitCode): $processOutput"
    }
    [ordered]@{
        exit_code = $exitCode
        receipt_path = $receiptPath
        receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    }
}

$legacy = Invoke-Adapter -Name 'legacy-no-role' -OmenBundle $legacyBundle -I5Bundle $legacyBundle
$supported = Invoke-Adapter -Name 'apply-omen-observe-i5' -OmenBundle $applyBundle -I5Bundle $observeBundle
$supportedReversed = Invoke-Adapter -Name 'apply-i5-observe-omen' -OmenBundle $observeBundle -I5Bundle $applyBundle
$ambiguous = Invoke-Adapter -Name 'ambiguous-both-apply' -OmenBundle $applyBundle -I5Bundle $applyBundle
$contradictory = Invoke-Adapter -Name 'contradictory-observe' -OmenBundle $applyBundle -I5Bundle $contradictoryObserveBundle
$missingI5 = Invoke-Adapter -Name 'missing-i5' -OmenBundle $legacyBundle -OmitI5

$checks = [ordered]@{
    legacy_exit_zero = ($legacy.exit_code -eq 0)
    legacy_phase_ready = ($legacy.receipt.ready -eq $true)
    legacy_attribution_inconclusive = (
        $legacy.receipt.attribution.status -eq 'inconclusive' -and
        $legacy.receipt.attribution.verdict -eq 'missing_or_ambiguous_apply_roles'
    )
    legacy_fixture_received = (
        [int64]$legacy.receipt.machines.omen.summary.derived.received_samples -eq 40 -and
        [int64]$legacy.receipt.machines.i5.summary.derived.received_samples -eq 40
    )
    supported_exit_zero = ($supported.exit_code -eq 0)
    supported_attribution_ready = (
        $supported.receipt.attribution_ready -eq $true -and
        $supported.receipt.attribution.status -eq 'ready'
    )
    supported_verdict = (
        $supported.receipt.attribution.verdict -eq 'apply_only_large_interframe_displacement_observed' -and
        $supported.receipt.attribution.apply_machine -eq 'omen' -and
        $supported.receipt.attribution.observe_machine -eq 'i5'
    )
    supported_uses_final_role_segment = (
        [int64]$supported.receipt.attribution.evidence.apply_motion_applied -eq 10 -and
        [int64]$supported.receipt.attribution.evidence.apply_interframe_displacement_checks -eq 10 -and
        [int64]$supported.receipt.attribution.evidence.apply_interframe_displacement_over_50mm -eq 2 -and
        [int64]$supported.receipt.attribution.evidence.observe_motion_applied -eq 0 -and
        [int64]$supported.receipt.attribution.evidence.observe_interframe_displacement_checks -eq 0
    )
    reversed_roles_supported = (
        $supportedReversed.exit_code -eq 0 -and
        $supportedReversed.receipt.attribution.status -eq 'ready' -and
        $supportedReversed.receipt.attribution.apply_machine -eq 'i5' -and
        $supportedReversed.receipt.attribution.observe_machine -eq 'omen'
    )
    ambiguous_roles_inconclusive = (
        $ambiguous.exit_code -eq 0 -and
        $ambiguous.receipt.ready -eq $true -and
        $ambiguous.receipt.attribution.status -eq 'inconclusive' -and
        $ambiguous.receipt.attribution.verdict -eq 'missing_or_ambiguous_apply_roles'
    )
    observe_activity_contradictory = (
        $contradictory.exit_code -eq 0 -and
        $contradictory.receipt.ready -eq $true -and
        $contradictory.receipt.attribution.status -eq 'contradictory' -and
        $contradictory.receipt.attribution.verdict -eq 'observe_role_advanced_apply_counters'
    )
    missing_i5_exit_one = ($missingI5.exit_code -eq 1)
    missing_i5_not_ready = ($missingI5.receipt.ready -eq $false)
    missing_i5_reason = ($missingI5.receipt.machines.i5.error -eq 'capture_bundle_missing')
}
$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
$result = [ordered]@{
    schema_version = 1
    event_type = 'motion_phase.bundle_adapter_fixture_result'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($failed.Count -eq 0) { 'motion_phase_bundle_adapter_fixtures_passed' } else { 'motion_phase_bundle_adapter_fixtures_failed' }
    checks = $checks
    legacy_receipt = $legacy.receipt_path
    supported_receipt = $supported.receipt_path
    supported_reversed_receipt = $supportedReversed.receipt_path
    ambiguous_receipt = $ambiguous.receipt_path
    contradictory_receipt = $contradictory.receipt_path
    missing_i5_receipt = $missingI5.receipt_path
}
$resultPath = Join-Path $OutputDirectory 'summary.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
$result | ConvertTo-Json -Depth 6
if ($failed.Count -gt 0) { exit 1 }
