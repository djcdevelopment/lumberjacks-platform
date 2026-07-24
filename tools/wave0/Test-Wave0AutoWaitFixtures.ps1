<#
.SYNOPSIS
Smoke-test the Wave 0 auto-wait live-gate wrapper without Valheim clients.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-auto-wait-fixtures'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

function Run-Case {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [string]$ExpectedVerdict
    )

    $caseDir = Join-Path $outRoot $Name
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $receipt = Join-Path $caseDir 'result.json'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Wait-Wave0LiveGate.ps1') @Arguments -OutputJson $receipt 2>&1
    $exitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $receipt)) { throw "$Name did not write receipt: $($output -join [Environment]::NewLine)" }
    $body = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ([string]$body.verdict -ne $ExpectedVerdict) { throw "$Name verdict expected '$ExpectedVerdict' but got '$($body.verdict)'" }
    [ordered]@{
        name = $Name
        exit_code = $exitCode
        expected_verdict = $ExpectedVerdict
        receipt_verdict = [string]$body.verdict
        receipt_path = $receipt
        output_tail = @($output | Select-Object -Last 8)
    }
}

$cases = @()
$cases += Run-Case `
    -Name 'no-peer-timeout' `
    -ExpectedVerdict 'wait_for_two_real_clients_timeout' `
    -Arguments @(
        '-WaitSeconds', '0',
        '-PollSeconds', '1',
        '-SkipSynthetic',
        '-SkipReadiness'
    )

$cases += Run-Case `
    -Name 'mock-two-peer-role-preflight' `
    -ExpectedVerdict 'role_preflight_passed_stopped_before_motion' `
    -Arguments @(
        '-MockValheimTelemetryJson', 'tools\wave0\fixtures\valheim-two-peers.json',
        '-MockRolePreflightJson', 'tools\wave0\fixtures\role-preflight-omen-apply.json',
        '-StopAfterRolePreflight',
        '-DesiredApplyClient', 'preserve',
        '-SkipSynthetic',
        '-SkipReadiness'
    )

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_auto_wait_fixture_checks_passed'
    cases = $cases
}

$summaryPath = Join-Path $outRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host ("Wave 0 auto-wait fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
