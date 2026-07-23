<#
.SYNOPSIS
Run the non-human Wave 0 synthetic Valheim motion relay gate and write a JSON receipt.

.DESCRIPTION
Uses the canonical .NET 9 Docker SDK lane to run the focused Gateway motion relay
tests. This does not start Valheim, contact P7, mutate clients, or require the i5.
It proves the motion relay seam that can be tested without live players:

- distinct enrolled recipients in one region fan out;
- same-recipient echo is suppressed;
- sessions without a recipient are unauthorized;
- malformed frames are rejected;
- duplicate or old sequences are stale;
- the first source ZDO binding cannot be changed by a later frame.

The live Wave 0 exit gate still requires two real clients for visual apply/observe.
#>
[CmdletBinding()]
param(
    [string]$OutputJson = 'captures/wave0-synthetic-motion.json',
    [string]$DockerImage = 'mcr.microsoft.com/dotnet/sdk:9.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lumberjacksRoot = Join-Path $repoRoot 'Lumberjacks'
if (-not (Test-Path -LiteralPath (Join-Path $lumberjacksRoot 'Game.sln'))) {
    throw "Lumberjacks root not found at $lumberjacksRoot"
}

$filter = 'FullyQualifiedName~ValheimMotionRelayTests'
$dockerArgs = @(
    'run', '--rm',
    '-v', "${lumberjacksRoot}:/src",
    '-w', '/src',
    $DockerImage,
    'dotnet', 'test',
    'tests/Game.Gateway.Tests/Game.Gateway.Tests.csproj',
    '--filter', $filter
)

$started = [DateTimeOffset]::UtcNow
$output = & docker @dockerArgs 2>&1
$exitCode = $LASTEXITCODE
$finished = [DateTimeOffset]::UtcNow
$lines = @($output | ForEach-Object { [string]$_ })
$joined = $lines -join [Environment]::NewLine

$total = $null
$passed = $null
$failed = $null
$skipped = $null
if ($joined -match 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)') {
    $failed = [int]$Matches[1]
    $passed = [int]$Matches[2]
    $skipped = [int]$Matches[3]
    $total = [int]$Matches[4]
}

$ok = $exitCode -eq 0 -and $failed -eq 0 -and $passed -ge 6
$receipt = [ordered]@{
    schema_version = 1
    generated_utc = $finished.ToString('o')
    started_utc = $started.ToString('o')
    duration_ms = [math]::Round(($finished - $started).TotalMilliseconds, 0)
    verdict = if ($ok) { 'synthetic_motion_gate_passed' } else { 'synthetic_motion_gate_failed' }
    ok = $ok
    docker_image = $DockerImage
    test_project = 'tests/Game.Gateway.Tests/Game.Gateway.Tests.csproj'
    filter = $filter
    exit_code = $exitCode
    result = [ordered]@{
        total = $total
        passed = $passed
        failed = $failed
        skipped = $skipped
    }
    covered_seams = @(
        'distinct_recipient_websocket_fallback_fanout',
        'same_recipient_echo_suppression',
        'unauthorized_missing_recipient_drop',
        'duplicate_and_old_sequence_stale_drop',
        'source_zdo_binding_rejects_later_change',
        'malformed_frame_invalid_drop'
    )
    live_gate_still_required = @(
        'two_real_clients_joined',
        'client_local_apply_observe_visual_result',
        'role_reversal_visual_result'
    )
    output_tail = @($lines | Select-Object -Last 80)
}

if ($OutputJson) {
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputJson))
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $json = $receipt | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

Write-Host ("Wave 0 synthetic motion: {0}" -f $receipt.verdict)
Write-Host ("Result: total={0} passed={1} failed={2} skipped={3} exit={4}" -f $total, $passed, $failed, $skipped, $exitCode)
if ($OutputJson) { Write-Host ("Receipt JSON: {0}" -f $path) }

if (-not $ok) {
    $lines | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }
    exit 1
}

exit 0
