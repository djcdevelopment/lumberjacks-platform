<#
.SYNOPSIS
Validate the Wave 0 return-packet handoff contract.

.DESCRIPTION
Checks the generated return packet, not just the generator script. The packet is
the handoff surface before the live two-client window, so it must list every
non-human evidence gate and the exact live/annotation/seal/defect commands.
#>
[CmdletBinding()]
param(
    [string]$PacketJson = 'captures/wave0-prelive-current/return-packet/packet.json',
    [string]$PacketMarkdown = 'captures/wave0-prelive-current/return-packet/packet.md',
    [string]$OutputJson = 'captures/wave0-return-packet-contract.json'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function New-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [ordered]@{ name = $Name; ok = $Ok; detail = $Detail }
}

$packetJsonPath = Resolve-UnderRepo $PacketJson
$packetMarkdownPath = Resolve-UnderRepo $PacketMarkdown
if (-not (Test-Path -LiteralPath $packetJsonPath -PathType Leaf)) { throw "packet JSON not found: $packetJsonPath" }
if (-not (Test-Path -LiteralPath $packetMarkdownPath -PathType Leaf)) { throw "packet Markdown not found: $packetMarkdownPath" }

$packet = Get-Content -LiteralPath $packetJsonPath -Raw | ConvertFrom-Json
$markdown = Get-Content -LiteralPath $packetMarkdownPath -Raw
$checkNames = @($packet.checks | ForEach-Object { [string]$_.name })

$requiredChecks = @(
    'synthetic_motion_gate',
    'runtime_readiness_gate',
    'roadmap_freshness_gate',
    'live_gate_wait_state',
    'live_gate_fixture_roles',
    'auto_wait_fixture_gate',
    'visual_seal_fixture_gate',
    'defect_packet_fixture_gate',
    'visual_observation_fixture_gate',
    'human_test_register_gate',
    'expected_result_grid_gate',
    'expected_result_grid_fixture_gate',
    'bounded_command_contract_gate',
    'companion_rollback_contract_gate',
    'wave0_stop_rule_gate',
    'wave0_stop_rule_fixture_gate',
    'two_machine_bundle_smoke'
)
$requiredMarkdown = @(
    '## Live prerequisites',
    '## Non-human evidence',
    '## Remaining human tests',
    '## Run when back',
    '## Stop conditions',
    '## Commands',
    'Wait-Wave0LiveGate.ps1',
    'Add-Wave0VisualObservation.ps1',
    'Seal-Wave0VisualEvidence.ps1',
    'Suggest-Wave0DefectPacket.ps1',
    'New-Wave0DefectPacket.ps1',
    'The original machine receipts remain immutable.'
)
$requiredHumanTests = @('H0-1', 'H0-2', 'H0-3', 'H0-4')
$forbiddenPublicText = @(
    'access_key',
    'steam_id',
    'steamid',
    'password',
    'secret',
    'bearer '
)

$checks = @()
$checks += New-Check `
    -Name 'packet_verdict_ready' `
    -Ok ([string]$packet.verdict -eq 'ready_for_derek_two_client_join') `
    -Detail ("verdict={0}" -f $packet.verdict)
$checks += New-Check `
    -Name 'all_checks_ok' `
    -Ok (@($packet.checks | Where-Object { -not [bool]$_.ok }).Count -eq 0) `
    -Detail ("check_count={0}" -f @($packet.checks).Count)

foreach ($name in $requiredChecks) {
    $checks += New-Check `
        -Name ("check_present_" + $name) `
        -Ok ($name -in $checkNames -and $markdown -match [regex]::Escape($name)) `
        -Detail 'required non-human gate appears in JSON checks and Markdown table'
}
foreach ($text in $requiredMarkdown) {
    $checks += New-Check `
        -Name ("markdown_contains_" + (($text -replace '[^A-Za-z0-9]+', '_').Trim('_').ToLowerInvariant())) `
        -Ok ($markdown -match [regex]::Escape($text)) `
        -Detail "required handoff text: $text"
}
foreach ($id in $requiredHumanTests) {
    $packetHasId = $false
    foreach ($row in @($packet.remaining_human_tests)) {
        if ([string]$row.id -eq $id) { $packetHasId = $true; break }
    }
    $checks += New-Check `
        -Name ("remaining_human_test_present_" + ($id -replace '[^A-Za-z0-9]+', '_')) `
        -Ok ($packetHasId -and $markdown -match [regex]::Escape("| $id |")) `
        -Detail "required current human test appears in JSON and Markdown: $id"
}
foreach ($text in $forbiddenPublicText) {
    $checks += New-Check `
        -Name ("markdown_omits_" + (($text -replace '[^A-Za-z0-9]+', '_').Trim('_').ToLowerInvariant())) `
        -Ok ($markdown.ToLowerInvariant() -notmatch [regex]::Escape($text.ToLowerInvariant())) `
        -Detail "forbidden sensitive marker should not appear: $text"
}

$failed = @($checks | Where-Object { -not $_.ok })
$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($failed.Count -eq 0) { 'wave0_return_packet_contract_passed' } else { 'wave0_return_packet_contract_failed' }
    packet_json = $packetJsonPath
    packet_markdown = $packetMarkdownPath
    checks = $checks
    failed_checks = @($failed | ForEach-Object { $_.name })
}

$outputPath = Resolve-UnderRepo $OutputJson
$outputDir = Split-Path -Parent $outputPath
if ($outputDir) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }
[IO.File]::WriteAllText($outputPath, (($receipt | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 return packet contract: {0}" -f $receipt.verdict)
Write-Host ("Receipt JSON: {0}" -f $outputPath)
if ($failed.Count -gt 0) { exit 1 }
