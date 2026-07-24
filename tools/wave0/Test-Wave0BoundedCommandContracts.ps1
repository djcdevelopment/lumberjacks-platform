<#
.SYNOPSIS
Static contract check for Wave 0 bounded command waits.

.DESCRIPTION
The live Wave 0 scripts must not rely on unbounded background job waits. This
test checks the command surfaces that run during live movement for explicit
Wait-Job timeouts and timeout evidence in their receipts.
#>
[CmdletBinding()]
param(
    [string]$OutputJson = 'captures/wave0-bounded-command-contracts.json'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Read-Text {
    param([string]$Path)
    Get-Content -LiteralPath (Join-Path $repoRoot $Path) -Raw
}

function New-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [ordered]@{ name = $Name; ok = $Ok; detail = $Detail }
}

$capture = Read-Text 'tools\i5\Start-TwoClientCapture.ps1'
$motion = Read-Text 'tools\i5\Start-TwoClientMotionTest.ps1'
$roles = Read-Text 'tools\i5\Set-TwoClientApplyRoles.ps1'
$live = Read-Text 'tools\wave0\Start-Wave0LiveGate.ps1'
$returnPacket = Read-Text 'tools\wave0\New-Wave0ReturnPacket.ps1'

$checks = @()
$checks += New-Check `
    -Name 'two_client_capture_wait_job_has_timeout' `
    -Ok ($capture -match 'Wait-Job\s+-Job\s+\$localJob,\s*\$remoteJob\s+-Timeout\s+\$captureTimeoutSeconds') `
    -Detail 'Start-TwoClientCapture.ps1 must bound concurrent OMEN/i5 capture waits.'
$checks += New-Check `
    -Name 'two_client_capture_receipt_records_timeout' `
    -Ok ($capture -match 'timeout_seconds\s*=\s*\$captureTimeoutSeconds') `
    -Detail 'Start-TwoClientCapture.ps1 result must record timeout_seconds.'
$checks += New-Check `
    -Name 'two_client_motion_http_calls_have_timeout' `
    -Ok (([regex]::Matches($motion, '-TimeoutSec\s+\$httpTimeoutSeconds').Count -ge 2) -and $motion -match '\$httpTimeoutSeconds\s*=\s*\[Math\]::Max') `
    -Detail 'Start-TwoClientMotionTest.ps1 must bound local and remote Companion HTTP posts.'
$checks += New-Check `
    -Name 'two_client_motion_receipt_records_timeout' `
    -Ok ($motion -match 'timeout_seconds\s*=\s*\$httpTimeoutSeconds') `
    -Detail 'Start-TwoClientMotionTest.ps1 result must record timeout_seconds.'
$checks += New-Check `
    -Name 'two_client_apply_roles_http_calls_have_timeout' `
    -Ok (([regex]::Matches($roles, '-TimeoutSec\s+\$httpTimeoutSeconds').Count -ge 2) -and $roles -match '\$httpTimeoutSeconds\s*=\s*15') `
    -Detail 'Set-TwoClientApplyRoles.ps1 must bound local and remote Companion HTTP posts.'
$checks += New-Check `
    -Name 'two_client_apply_roles_receipt_records_timeout' `
    -Ok ($roles -match 'timeout_seconds\s*=\s*\$httpTimeoutSeconds') `
    -Detail 'Set-TwoClientApplyRoles.ps1 result must record timeout_seconds.'
$checks += New-Check `
    -Name 'live_gate_capture_wait_job_has_timeout' `
    -Ok ($live -match 'Wait-Job\s+-Job\s+\$captureJob\s+-Timeout\s+\$captureTimeoutSeconds') `
    -Detail 'Start-Wave0LiveGate.ps1 must bound the capture job wait.'
$checks += New-Check `
    -Name 'live_gate_capture_receipt_records_timeout' `
    -Ok ($live -match 'timeout_seconds\s*=\s*\$captureTimeoutSeconds') `
    -Detail 'Start-Wave0LiveGate.ps1 capture block must record timeout_seconds.'
$checks += New-Check `
    -Name 'return_packet_names_timeout_contract' `
    -Ok ($returnPacket -match 'timeout' -and $returnPacket -match 'bounded') `
    -Detail 'Return packet should continue naming the bounded command/timeout prerequisite.'

$failed = @($checks | Where-Object { -not $_.ok })
$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($failed.Count -eq 0) { 'wave0_bounded_command_contracts_passed' } else { 'wave0_bounded_command_contracts_failed' }
    checks = $checks
}

$outputPath = if ([IO.Path]::IsPathRooted($OutputJson)) {
    [IO.Path]::GetFullPath($OutputJson)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputJson))
}
$outputDir = Split-Path -Parent $outputPath
if ($outputDir) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }
[IO.File]::WriteAllText($outputPath, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 bounded command contracts: {0}" -f $receipt.verdict)
Write-Host ("Receipt JSON: {0}" -f $outputPath)
if ($failed.Count -gt 0) { exit 1 }
