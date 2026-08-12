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
$startCompanion = Read-Text 'Lumberjacks\tools\companion\Start-I5Companion.ps1'
$syncCompanion = Read-Text 'Lumberjacks\tools\companion\Sync-I5Companion.ps1'
$installI5 = Read-Text 'tools\i5\Install-I5LatestModpack.ps1'
$motion = Read-Text 'tools\i5\Start-TwoClientMotionTest.ps1'
$roles = Read-Text 'tools\i5\Set-TwoClientApplyRoles.ps1'
$readiness = Read-Text 'Lumberjacks\tools\companion\Test-Wave0Readiness.ps1'
$alignment = Read-Text 'tools\i5\Test-AlphaReleaseAlignment.ps1'
$live = Read-Text 'tools\wave0\Start-Wave0LiveGate.ps1'
$wait = Read-Text 'tools\wave0\Wait-Wave0LiveGate.ps1'
$freshness = Read-Text 'tools\wave0\Test-Wave0RoadmapFreshness.ps1'
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
    -Name 'two_client_capture_http_calls_have_timeout' `
    -Ok (([regex]::Matches($capture, '-TimeoutSec').Count -ge 4) -and $capture -match 'captureTimeoutSeconds') `
    -Detail 'Start-TwoClientCapture.ps1 must bound Companion capture posts and bundle downloads.'
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
    -Name 'live_gate_peer_poll_has_timeout' `
    -Ok ($live -match 'Invoke-RestMethod[\s\S]*-TimeoutSec\s+\$httpTimeoutSeconds' -and $live -match 'http_timeout_seconds\s*=\s*\$httpTimeoutSeconds') `
    -Detail 'Start-Wave0LiveGate.ps1 must bound P7 peer polling and record the HTTP timeout.'
$checks += New-Check `
    -Name 'auto_wait_peer_poll_has_timeout' `
    -Ok ($wait -match 'Invoke-RestMethod[\s\S]*-TimeoutSec\s+\$httpTimeoutSeconds' -and $wait -match 'http_timeout_seconds\s*=\s*\$httpTimeoutSeconds') `
    -Detail 'Wait-Wave0LiveGate.ps1 must bound P7 peer polling and record the HTTP timeout.'
$checks += New-Check `
    -Name 'readiness_http_calls_have_timeout' `
    -Ok (([regex]::Matches($readiness, '-TimeoutSec\s+\$httpTimeoutSeconds').Count -ge 2) -and $readiness -match '\$httpTimeoutSeconds\s*=\s*15') `
    -Detail 'Test-Wave0Readiness.ps1 must bound P7, OMEN, and i5 Companion reads.'
$checks += New-Check `
    -Name 'roadmap_freshness_http_calls_have_timeout' `
    -Ok (([regex]::Matches($freshness, '-TimeoutSec\s+\$httpTimeoutSeconds').Count -ge 2) -and $freshness -match '\$httpTimeoutSeconds\s*=\s*15') `
    -Detail 'Test-Wave0RoadmapFreshness.ps1 must bound public manifest and roadmap reads.'
$checks += New-Check `
    -Name 'alpha_release_alignment_http_calls_have_timeout' `
    -Ok (([regex]::Matches($alignment, '-TimeoutSec\s+\$httpTimeoutSeconds').Count -ge 2) -and $alignment -match '\$httpTimeoutSeconds\s*=\s*15') `
    -Detail 'Test-AlphaReleaseAlignment.ps1 must bound Gateway, OMEN, and i5 Companion reads.'
$checks += New-Check `
    -Name 'i5_companion_start_http_calls_have_timeout' `
    -Ok (([regex]::Matches($startCompanion, 'Invoke-RestMethod[\s\S]*?-TimeoutSec').Count -ge 2)) `
    -Detail 'Start-I5Companion.ps1 must bound local health/status reads after container startup.'
$checks += New-Check `
    -Name 'i5_lab_companion_gateway_is_explicit' `
    -Ok ($startCompanion -match "Profile -eq 'Lab'" -and
        $startCompanion -match 'http://100\.124\.12\.37:4000' -and
        $startCompanion -match 'LUMBERJACKS_COMPANION_GATEWAY_URL=.*gatewayUrl') `
    -Detail 'Start-I5Companion.ps1 must persist the canonical OMEN Lab Gateway into the remote Compose environment.'
$checks += New-Check `
    -Name 'i5_companion_sync_forwards_profile_and_gateway' `
    -Ok ($syncCompanion -match '& \$StartScript -RemoteRoot \$RemoteRoot -Profile \$Profile -McpPort \$McpPort -GatewayUrl \$GatewayUrl') `
    -Detail 'Sync-I5Companion.ps1 must preserve explicit profile and Gateway selection when it restarts Companion.'
$checks += New-Check `
    -Name 'i5_modpack_install_http_call_has_timeout' `
    -Ok ($installI5 -match 'Invoke-RestMethod[\s\S]*-TimeoutSec\s+60') `
    -Detail 'Install-I5LatestModpack.ps1 must bound the i5 Companion install request.'
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
