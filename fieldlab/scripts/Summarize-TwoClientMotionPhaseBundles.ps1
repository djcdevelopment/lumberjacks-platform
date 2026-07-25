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

$machines = [ordered]@{
    omen = Invoke-MachineBundleAnalysis -Machine 'omen' -BundlePath $OmenBundlePath
    i5 = Invoke-MachineBundleAnalysis -Machine 'i5' -BundlePath $I5BundlePath
}
$readyMachines = @($machines.Values | Where-Object { $_.ok -eq $true })
$result = [ordered]@{
    schema_version = 1
    event_type = 'motion_phase.two_client_bundle_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    requested = $true
    ready = ($readyMachines.Count -eq 2)
    machines = $machines
}

$outputParent = Split-Path -Parent $OutputJson
if ($outputParent) { New-Item -ItemType Directory -Force -Path $outputParent | Out-Null }
$json = $result | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($OutputJson, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$json
if (-not $result.ready) { exit 1 }
