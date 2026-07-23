<#
.SYNOPSIS
Generate the operator return packet for the remaining Wave 0 live gate.

.DESCRIPTION
Summarizes current non-human evidence and the exact Derek-only live steps left.
The packet references receipt paths and hashes instead of embedding raw
Companion/Gateway receipt bodies, because those may include private local
profile or diagnostic details.
#>
[CmdletBinding()]
param(
    [string]$SyntheticReceipt = 'captures/synthetic-motion.json',
    [string]$ReadinessReceipt = 'captures/readiness.json',
    [string]$LiveGateReceipt = 'captures/wave0-live-gate-full-nopeer-smoke.json',
    [string]$OutputJson = 'captures/wave0-return-packet.json',
    [string]$OutputMarkdown = 'captures/wave0-return-packet.md'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Read-Receipt {
    param([string]$Path)

    $full = Resolve-UnderRepo $Path
    if (-not (Test-Path -LiteralPath $full)) {
        return [ordered]@{
            present = $false
            path = $full
            sha256 = $null
            body = $null
        }
    }

    [ordered]@{
        present = $true
        path = $full
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        body = (Get-Content -LiteralPath $full -Raw | ConvertFrom-Json)
    }
}

function Check-State {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail,
        [string]$ReceiptPath = '',
        [string]$ReceiptSha256 = ''
    )

    [ordered]@{
        name = $Name
        ok = $Ok
        detail = $Detail
        receipt_path = $ReceiptPath
        receipt_sha256 = $ReceiptSha256
    }
}

function MdEscape {
    param([string]$Value)

    if ($null -eq $Value) { return '' }
    return $Value.Replace('|', '\|')
}

$synthetic = Read-Receipt $SyntheticReceipt
$readiness = Read-Receipt $ReadinessReceipt
$liveGate = Read-Receipt $LiveGateReceipt

$checks = @()
$checks += Check-State `
    -Name 'synthetic_motion_gate' `
    -Ok ($synthetic.present -and [bool]$synthetic.body.ok -and [string]$synthetic.body.verdict -eq 'synthetic_motion_gate_passed') `
    -Detail ($(if ($synthetic.present) { "verdict=$($synthetic.body.verdict) total=$($synthetic.body.result.total) passed=$($synthetic.body.result.passed)" } else { 'missing synthetic motion receipt' })) `
    -ReceiptPath $synthetic.path `
    -ReceiptSha256 $synthetic.sha256
$checks += Check-State `
    -Name 'runtime_readiness_gate' `
    -Ok ($readiness.present -and [bool]$readiness.body.ready_for_derek -and [string]$readiness.body.verdict -eq 'ready_for_two_client_gate') `
    -Detail ($(if ($readiness.present) { "verdict=$($readiness.body.verdict) release=$($readiness.body.expected_release)" } else { 'missing readiness receipt' })) `
    -ReceiptPath $readiness.path `
    -ReceiptSha256 $readiness.sha256
$checks += Check-State `
    -Name 'live_gate_wait_state' `
    -Ok ($liveGate.present -and [string]$liveGate.body.verdict -eq 'wait_for_two_real_clients') `
    -Detail ($(if ($liveGate.present) { "verdict=$($liveGate.body.verdict) peer_count=$($liveGate.body.p7_peer_check.peer_count)" } else { 'missing live gate smoke receipt' })) `
    -ReceiptPath $liveGate.path `
    -ReceiptSha256 $liveGate.sha256

$failed = @($checks | Where-Object { -not $_.ok })
$expectedRelease = if ($readiness.present) { [string]$readiness.body.expected_release } else { '' }
$currentPeerCount = if ($liveGate.present -and $liveGate.body.p7_peer_check) { [int]$liveGate.body.p7_peer_check.peer_count } else { $null }
$observationMarkdown = if ($liveGate.present -and $liveGate.body.observation_markdown) { [string]$liveGate.body.observation_markdown } else { '' }

$packet = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    objective = 'Wave 0 return packet: non-human evidence plus Derek-only live steps.'
    expected_release = $expectedRelease
    verdict = if ($failed.Count -eq 0) { 'ready_for_derek_two_client_join' } else { 'prelive_packet_incomplete' }
    checks = $checks
    current_state = [ordered]@{
        p7_peer_count = $currentPeerCount
        live_gate_waiting_for = 'two_real_clients_joined'
        machine_commands_ready = $true
        original_receipts_left_immutable = $true
        observation_markdown = $observationMarkdown
    }
    run_when_back = @(
        [ordered]@{
            step = 1
            actor = 'derek'
            action = 'Join OMEN and i5 to P7 with the two player accounts.'
            expected = 'P7 peer_count reaches at least 2.'
        },
        [ordered]@{
            step = 2
            actor = 'agent'
            action = 'Run tools\wave0\Start-Wave0LiveGate.ps1 -OutputJson captures\wave0-live-gate\result.json'
            expected = 'Machine receipt records non-human gates, peer count, capture, and bounded motion command.'
        },
        [ordered]@{
            step = 3
            actor = 'derek'
            action = 'Observe both screens during the bounded movement.'
            expected = 'Record whether Lumberjacks-applied movement followed the selected apply/observe role and movement quality.'
        },
        [ordered]@{
            step = 4
            actor = 'agent'
            action = 'Run tools\wave0\Add-Wave0VisualObservation.ps1 against the live-gate receipt.'
            expected = 'Sidecar visual observation and annotated projection are written without editing the original receipt.'
        },
        [ordered]@{
            step = 5
            actor = 'derek-plus-agent'
            action = 'Reverse apply/observe roles and repeat steps 2-4.'
            expected = 'The visual result follows role selection rather than machine/account.'
        }
    )
    stop_conditions = @(
        'Any non-human gate fails.',
        'P7 peer_count stays below 2.',
        'Capture comparison has incomplete telemetry.',
        'Motion command fails on either Companion.',
        'Visual result does not follow the selected apply/observe role.',
        'Role reversal contradicts the first run.'
    )
    commands = [ordered]@{
        precheck = 'tools\wave0\Start-Wave0LiveGate.ps1 -OutputJson captures\wave0-live-gate\result.json'
        annotate_first_pass = 'tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson captures\wave0-live-gate\result.json -ApplyClient omen -ObserveClient i5 -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun no'
        annotate_role_reversal = 'tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson captures\wave0-live-gate-reversal\result.json -ApplyClient i5 -ObserveClient omen -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun yes'
    }
}

$markdownLines = @()
$markdownLines += '# Wave 0 return packet'
$markdownLines += ''
$markdownLines += "- Generated UTC: $($packet.generated_utc)"
$markdownLines += "- Verdict: $($packet.verdict)"
if ($expectedRelease) { $markdownLines += "- Expected release: $expectedRelease" }
if ($null -ne $currentPeerCount) { $markdownLines += "- Current P7 peer count: $currentPeerCount" }
if ($observationMarkdown) { $markdownLines += "- Observation worksheet: $observationMarkdown" }
$markdownLines += ''
$markdownLines += '## Non-human evidence'
$markdownLines += ''
$markdownLines += '| Check | OK | Detail | Receipt SHA-256 |'
$markdownLines += '|---|---:|---|---|'
foreach ($check in $checks) {
    $markdownLines += "| $(MdEscape $check.name) | $($check.ok) | $(MdEscape $check.detail) | $($check.receipt_sha256) |"
}
$markdownLines += ''
$markdownLines += '## Run when back'
$markdownLines += ''
if ($observationMarkdown) {
    $markdownLines += "Use the observation worksheet during the live pass: $observationMarkdown"
    $markdownLines += ''
}
foreach ($step in $packet.run_when_back) {
    $markdownLines += "$($step.step). **$($step.actor)** - $($step.action)"
    $markdownLines += "   - Expected: $($step.expected)"
}
$markdownLines += ''
$markdownLines += '## Stop conditions'
$markdownLines += ''
foreach ($condition in $packet.stop_conditions) {
    $markdownLines += "- $condition"
}
$markdownLines += ''
$markdownLines += '## Commands'
$markdownLines += ''
$markdownLines += '```powershell'
$markdownLines += $packet.commands.precheck
$markdownLines += ''
$markdownLines += '# After observing the first pass, fill these values with what actually happened:'
$markdownLines += $packet.commands.annotate_first_pass
$markdownLines += ''
$markdownLines += '# After role reversal:'
$markdownLines += $packet.commands.annotate_role_reversal
$markdownLines += '```'
$markdownLines += ''
$markdownLines += 'The original machine receipts remain immutable. Visual observations are sidecars and derived projections.'

$jsonPath = Resolve-UnderRepo $OutputJson
$mdPath = Resolve-UnderRepo $OutputMarkdown
foreach ($path in @($jsonPath, $mdPath)) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

[IO.File]::WriteAllText($jsonPath, (($packet | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($mdPath, (($markdownLines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 return packet: {0}" -f $packet.verdict)
Write-Host ("JSON: {0}" -f $jsonPath)
Write-Host ("Markdown: {0}" -f $mdPath)
if ($failed.Count -gt 0) {
    Write-Host 'Incomplete checks:'
    foreach ($check in $failed) { Write-Host ("- {0}: {1}" -f $check.name, $check.detail) }
}
