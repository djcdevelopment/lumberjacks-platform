<#
.SYNOPSIS
Analyze OMEN and i5 Companion capture bundles with one fail-closed contract.

.DESCRIPTION
Extracts each supplied Companion evidence bundle, runs the bounded motion-phase
analyzer against samples.jsonl, and writes one machine-readable receipt. This
adapter is intentionally independent of live Companion and Valheim processes so
the success and rejection paths can be tested from retained or synthetic bundles.
#>
[CmdletBinding()]
param(
    [string] $OmenBundlePath,

    [string] $I5BundlePath,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $OutputJson
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    $OutputJson = Join-Path $OutputDirectory 'motion-phase-receipt.json'
} else {
    $OutputJson = [IO.Path]::GetFullPath($OutputJson)
}
$summaryScript = Join-Path $PSScriptRoot 'Summarize-MotionPhaseCapture.ps1'

function Invoke-MachineBundleAnalysis {
    param(
        [Parameter(Mandatory = $true)][string] $Machine,
        [string] $BundlePath
    )

    if ([string]::IsNullOrWhiteSpace($BundlePath)) {
        return [ordered]@{
            ok = $false
            error = 'capture_bundle_missing'
        }
    }

    try {
        $resolvedBundle = (Resolve-Path -LiteralPath $BundlePath -ErrorAction Stop).Path
        $bundleHash = (Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256).Hash.ToLowerInvariant()
        $extractRoot = Join-Path $OutputDirectory "$Machine-$($bundleHash.Substring(0, 12))"
        $samplesPath = Join-Path $extractRoot 'samples.jsonl'
        $summaryPath = Join-Path $OutputDirectory "$Machine-motion-phase-summary.json"

        Expand-Archive -LiteralPath $resolvedBundle -DestinationPath $extractRoot -Force
        if (-not (Test-Path -LiteralPath $samplesPath -PathType Leaf)) {
            throw "samples.jsonl missing from $resolvedBundle"
        }

        & $summaryScript -SamplesPath $samplesPath -OutputPath $summaryPath | Out-Null
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        return [ordered]@{
            ok = $true
            bundle_path = $resolvedBundle
            bundle_sha256 = $bundleHash
            samples_path = $samplesPath
            summary_path = $summaryPath
            summary = $summary
        }
    } catch {
        return [ordered]@{
            ok = $false
            bundle_path = $BundlePath
            error = $_.Exception.Message
        }
    }
}

function Read-Int64Value {
    param(
        [object] $Value,
        [string] $Name
    )

    if ($null -eq $Value) { return [long]0 }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return [long]0 }
    try { return [long]$property.Value } catch { return [long]0 }
}

function New-RoleAttribution {
    param([object] $Machines)

    if (-not $Machines.omen.ok -or -not $Machines.i5.ok) {
        return [ordered]@{
            status = 'unavailable'
            verdict = 'bundle_analysis_incomplete'
            apply_machine = $null
            observe_machine = $null
            interpretation_limit = 'Both machine summaries are required before APPLY/OBSERVE attribution.'
        }
    }

    $omenRole = $Machines.omen.summary.apply_role.last
    $i5Role = $Machines.i5.summary.apply_role.last
    if ($null -eq $omenRole -or $null -eq $i5Role -or [bool]$omenRole -eq [bool]$i5Role) {
        return [ordered]@{
            status = 'inconclusive'
            verdict = 'missing_or_ambiguous_apply_roles'
            apply_machine = $null
            observe_machine = $null
            evidence = [ordered]@{
                omen_apply_enabled = $omenRole
                i5_apply_enabled = $i5Role
            }
            interpretation_limit = 'Exactly one final APPLY role and one final OBSERVE role are required.'
        }
    }

    $applyMachine = if ([bool]$omenRole) { 'omen' } else { 'i5' }
    $observeMachine = if ([bool]$omenRole) { 'i5' } else { 'omen' }
    $applyRole = $Machines.$applyMachine.summary.apply_role
    $observeRole = $Machines.$observeMachine.summary.apply_role
    $applyDeltas = $applyRole.last_segment_deltas
    $observeDeltas = $observeRole.last_segment_deltas

    $applyApplied = Read-Int64Value $applyDeltas 'motion_applied'
    $applyChecks = Read-Int64Value $applyDeltas 'motion_interframe_displacement_checks'
    $applyOver50 = Read-Int64Value $applyDeltas 'motion_interframe_displacement_over_50mm'
    $observeApplied = Read-Int64Value $observeDeltas 'motion_applied'
    $observeChecks = Read-Int64Value $observeDeltas 'motion_interframe_displacement_checks'
    $observeOver50 = Read-Int64Value $observeDeltas 'motion_interframe_displacement_over_50mm'

    $evidence = [ordered]@{
        omen_apply_enabled = [bool]$omenRole
        i5_apply_enabled = [bool]$i5Role
        apply_last_segment_samples = [int]$applyRole.last_segment_samples
        observe_last_segment_samples = [int]$observeRole.last_segment_samples
        apply_motion_applied = $applyApplied
        apply_interframe_displacement_checks = $applyChecks
        apply_interframe_displacement_over_50mm = $applyOver50
        observe_motion_applied = $observeApplied
        observe_interframe_displacement_checks = $observeChecks
        observe_interframe_displacement_over_50mm = $observeOver50
    }

    if (-not $applyRole.last_segment_ready -or -not $observeRole.last_segment_ready) {
        return [ordered]@{
            status = 'inconclusive'
            verdict = 'insufficient_final_role_segment'
            apply_machine = $applyMachine
            observe_machine = $observeMachine
            evidence = $evidence
            interpretation_limit = 'Each final role segment needs at least two samples.'
        }
    }

    if ($observeApplied -gt 0 -or $observeChecks -gt 0 -or $observeOver50 -gt 0) {
        return [ordered]@{
            status = 'contradictory'
            verdict = 'observe_role_advanced_apply_counters'
            apply_machine = $applyMachine
            observe_machine = $observeMachine
            evidence = $evidence
            interpretation_limit = 'The OBSERVE negative control advanced APPLY-path counters; do not attribute displacement from this window.'
        }
    }

    if ($applyApplied -le 0 -or $applyChecks -le 0) {
        return [ordered]@{
            status = 'inconclusive'
            verdict = 'apply_role_has_no_measurable_apply_window'
            apply_machine = $applyMachine
            observe_machine = $observeMachine
            evidence = $evidence
            interpretation_limit = 'The APPLY role did not advance both apply and interframe-check counters.'
        }
    }

    return [ordered]@{
        status = 'ready'
        verdict = if ($applyOver50 -gt 0) {
            'apply_only_large_interframe_displacement_observed'
        } else {
            'apply_only_no_large_interframe_displacement_observed'
        }
        apply_machine = $applyMachine
        observe_machine = $observeMachine
        evidence = $evidence
        interpretation_limit = 'This attributes measured displacement to the active APPLY path, but it cannot identify the competing transform writer.'
    }
}

$machines = [ordered]@{
    omen = Invoke-MachineBundleAnalysis -Machine 'omen' -BundlePath $OmenBundlePath
    i5 = Invoke-MachineBundleAnalysis -Machine 'i5' -BundlePath $I5BundlePath
}
$readyMachines = @($machines.Values | Where-Object { $_.ok -eq $true })
$attribution = New-RoleAttribution -Machines $machines
$result = [ordered]@{
    schema_version = 2
    event_type = 'motion_phase.two_client_bundle_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    requested = $true
    ready = ($readyMachines.Count -eq 2)
    attribution_ready = ($attribution.status -eq 'ready')
    attribution = $attribution
    machines = $machines
}

$outputParent = Split-Path -Parent $OutputJson
if ($outputParent) { New-Item -ItemType Directory -Force -Path $outputParent | Out-Null }
$json = $result | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($OutputJson, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$json
if (-not $result.ready) { exit 1 }
