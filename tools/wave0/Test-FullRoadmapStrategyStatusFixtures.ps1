<#
.SYNOPSIS
Smoke-test the full roadmap strategy status packet generator with local fixtures.

.DESCRIPTION
Creates minimal good and bad pre-live summary receipts, runs
New-FullRoadmapStrategyStatus.ps1 against both, and verifies the conservative
verdicts. This does not contact P7, OMEN, or i5.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/full-roadmap-strategy-status-fixtures'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

function Write-FixtureSummary {
    param(
        [string]$Name,
        [string]$Verdict,
        [int]$PeerCount
    )

    $path = Join-Path $outputRoot "$Name.summary.json"
    $receipt = [ordered]@{
        schema_version = 1
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        verdict = $Verdict
        output_directory = $outputRoot
        expected_release = 'fixture-release'
        p7_peer_count = $PeerCount
        receipts = [ordered]@{
            return_packet_markdown = Join-Path $outputRoot "$Name.return.md"
            expected_result_grid_markdown = Join-Path $outputRoot "$Name.grid.md"
            stop_rule = Join-Path $outputRoot "$Name.stop-rule.json"
        }
    }
    [IO.File]::WriteAllText($path, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.return.md"), "# fixture return $Name`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.grid.md"), "# fixture grid $Name`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $outputRoot "$Name.stop-rule.json"),
        (([ordered]@{
            schema_version = 1
            verdict = 'wave0_stop_rule_holds_no_exit_artifact'
            strategy_names_rule = $true
            exit_artifact_present = $false
        } | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    return $path
}

function Invoke-Status {
    param(
        [string]$Name,
        [string]$SummaryPath
    )

    $caseRoot = Join-Path $outputRoot $Name
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    $json = Join-Path $caseRoot 'packet.json'
    $md = Join-Path $caseRoot 'packet.md'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\New-FullRoadmapStrategyStatus.ps1') `
        -PreliveSummaryJson $SummaryPath `
        -OutputJson $json `
        -OutputMarkdown $md 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Name strategy status exited $LASTEXITCODE`n$((@($output) | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)"
    }
    if (-not (Test-Path -LiteralPath $json -PathType Leaf)) { throw "$Name did not write $json" }
    return Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
}

$goodSummary = Write-FixtureSummary -Name 'good' -Verdict 'ready_for_derek_two_client_join' -PeerCount 0
$badSummary = Write-FixtureSummary -Name 'bad' -Verdict 'prelive_return_packet_not_ready' -PeerCount 0

$good = Invoke-Status -Name 'good' -SummaryPath $goodSummary
$bad = Invoke-Status -Name 'bad' -SummaryPath $badSummary

if ([string]$good.verdict -ne 'strategy_active_wave0_human_gated') {
    throw "unexpected good verdict: $($good.verdict)"
}
if ([string]$bad.verdict -ne 'strategy_status_incomplete') {
    throw "unexpected bad verdict: $($bad.verdict)"
}
if ('blocked_by_wave0_exit' -notin @($good.rows | ForEach-Object { [string]$_.status })) {
    throw 'good fixture did not retain blocked_by_wave0_exit row'
}
if ('not_ready' -notin @($bad.rows | ForEach-Object { [string]$_.status })) {
    throw 'bad fixture did not retain not_ready row'
}
if ($good.full_objective_complete -ne $false) {
    throw 'good fixture must not claim full objective completion'
}
if ('not_achieved' -notin @($good.completion_audit | ForEach-Object { [string]$_.state })) {
    throw 'good fixture did not include incomplete roadmap audit states'
}
if ('blocked_by_wave0_stop_rule' -notin @($good.completion_audit | ForEach-Object { [string]$_.state })) {
    throw 'good fixture did not include M1/M2 stop-rule block'
}
if ('explicitly_deferred' -notin @($good.completion_audit | ForEach-Object { [string]$_.state })) {
    throw 'good fixture did not mark M7 explicitly deferred'
}

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'full_roadmap_strategy_status_fixture_checks_passed'
    cases = @(
        [ordered]@{ name = 'good'; verdict = $good.verdict },
        [ordered]@{ name = 'bad'; verdict = $bad.verdict }
    )
}
$summaryPath = Join-Path $outputRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Full roadmap strategy status fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
