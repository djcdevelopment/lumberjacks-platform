<#
.SYNOPSIS
Run mock-only Wave 0 live-gate fixture checks.

.DESCRIPTION
Exercises the live-gate branches that can be proven without running Valheim:

- fewer than two peers waits and does not move characters;
- two peers with both clients apply-enabled blocks as ambiguous;
- two peers with exactly one apply-enabled passes role preflight and stops
  before capture/motion.

This is intentionally fixture-only. The actual Wave 0 exit gate still requires
two real clients and Derek's visual observation.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-live-gate-fixtures'
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

function Invoke-Gate {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    $out = Join-Path $outputRoot "$Name.json"
    $args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $repoRoot 'tools\wave0\Start-Wave0LiveGate.ps1'),
        '-SkipSynthetic',
        '-SkipReadiness',
        '-OutputJson', $out
    ) + $Arguments
    $stdout = & powershell.exe @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Name exited $LASTEXITCODE`n$((@($stdout) | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)"
    }
    if (-not (Test-Path -LiteralPath $out)) {
        throw "$Name did not write receipt: $out"
    }
    [ordered]@{
        name = $Name
        path = $out
        stdout = (@($stdout) | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        receipt = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    }
}

function Assert-Equal {
    param(
        [string]$Name,
        $Actual,
        $Expected
    )

    if ($Actual -ne $Expected) {
        throw "$Name expected '$Expected' but got '$Actual'"
    }
}

$twoPeers = 'tools\wave0\fixtures\valheim-two-peers.json'
$bothApply = 'tools\wave0\fixtures\role-preflight-both-apply.json'
$omenApply = 'tools\wave0\fixtures\role-preflight-omen-apply.json'

$cases = @()
$cases += Invoke-Gate -Name 'no-peers' -Arguments @()
$cases += Invoke-Gate -Name 'ambiguous-roles' -Arguments @(
    '-MockValheimTelemetryJson', $twoPeers,
    '-MockRolePreflightJson', $bothApply
)
$cases += Invoke-Gate -Name 'valid-roles' -Arguments @(
    '-StopAfterRolePreflight',
    '-MockValheimTelemetryJson', $twoPeers,
    '-MockRolePreflightJson', $omenApply
)

Assert-Equal 'no-peers verdict' $cases[0].receipt.verdict 'wait_for_two_real_clients'
Assert-Equal 'ambiguous verdict' $cases[1].receipt.verdict 'blocked_by_ambiguous_apply_roles'
Assert-Equal 'ambiguous exactly-one' $cases[1].receipt.role_preflight.summary.exactly_one_apply_enabled $false
Assert-Equal 'valid verdict' $cases[2].receipt.verdict 'role_preflight_passed_stopped_before_motion'
Assert-Equal 'valid exactly-one' $cases[2].receipt.role_preflight.summary.exactly_one_apply_enabled $true
Assert-Equal 'valid apply client' $cases[2].receipt.role_preflight.summary.apply_client 'omen'
Assert-Equal 'valid observe client' $cases[2].receipt.role_preflight.summary.observe_client 'i5'

$result = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_live_gate_fixture_checks_passed'
    output_directory = $outputRoot
    cases = @($cases | ForEach-Object {
        [ordered]@{
            name = $_.name
            receipt_path = $_.path
            verdict = $_.receipt.verdict
            role_summary = $_.receipt.role_preflight.summary
            observation_markdown = $_.receipt.observation_markdown
        }
    })
}

$resultPath = Join-Path $outputRoot 'summary.json'
[IO.File]::WriteAllText($resultPath, (($result | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 live-gate fixtures: {0}" -f $result.verdict)
Write-Host ("Summary JSON: {0}" -f $resultPath)
