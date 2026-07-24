<#
.SYNOPSIS
Run the Wave 0 two-client live gate with minimum operator touch.

.DESCRIPTION
This is the command to run after Derek has joined OMEN and i5 to P7. It first
runs the non-human gates, then checks the live P7 peer count. If fewer than two
peers are present, it writes a WAIT receipt and exits without moving players.

When at least two peers are present, it starts the concurrent OMEN+i5 capture,
waits a short warmup, sends one bounded Companion motion command to both clients,
and writes a combined receipt. The receipt deliberately keeps the visual
apply/observe result as a human field; the script can collect transport evidence
but cannot see the two monitors.
#>
[CmdletBinding()]
param(
    [ValidateSet('straight_north','straight_east','stutter_north','circle')]
    [string]$Pattern = 'straight_north',

    [ValidateRange(1,60)]
    [int]$MotionDurationSeconds = 10,

    [ValidateRange(10,300)]
    [int]$CaptureDurationSeconds = 30,

    [ValidateRange(0,30)]
    [int]$WarmupSeconds = 5,

    [ValidateRange(1,60)]
    [int]$IntervalSeconds = 1,

    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',

    [string]$Label = 'wave0-live-gate',

    [string]$OutputJson,

    [string]$ObservationMarkdown,

    [string]$BundleDirectory,

    [ValidateSet('omen','i5','preserve')]
    [string]$DesiredApplyClient = 'omen',

    [string]$MockValheimTelemetryJson,

    [string]$MockRolePreflightJson,

    [switch]$StopAfterRolePreflight,

    [switch]$SkipSynthetic,

    [switch]$SkipReadiness,

    [switch]$SkipRolePreflight
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$httpTimeoutSeconds = 15

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'wave0-live-gate' }
$runId = "$stamp-$safeLabel"

if (-not $OutputJson) {
    $OutputJson = "captures/$runId/result.json"
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputJson)) {
    [IO.Path]::GetFullPath($OutputJson)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputJson))
}
$outputDirectory = Split-Path -Parent $outputPath
if ($outputDirectory) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }

if (-not $BundleDirectory) {
    $BundleDirectory = Join-Path $outputDirectory 'bundles'
}
$bundlePath = if ([IO.Path]::IsPathRooted($BundleDirectory)) {
    [IO.Path]::GetFullPath($BundleDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $BundleDirectory))
}

if (-not $ObservationMarkdown) {
    $ObservationMarkdown = [IO.Path]::ChangeExtension($outputPath, '.observation.md')
}
$observationPath = if ([IO.Path]::IsPathRooted($ObservationMarkdown)) {
    [IO.Path]::GetFullPath($ObservationMarkdown)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $ObservationMarkdown))
}
$observationDirectory = Split-Path -Parent $observationPath
if ($observationDirectory) { New-Item -ItemType Directory -Force -Path $observationDirectory | Out-Null }

function Write-ObservationTemplate {
    param($Receipt)

    $peerCount = if ($Receipt.p7_peer_check) { [string]$Receipt.p7_peer_check.peer_count } else { 'not_checked' }
    $players = @()
    if ($Receipt.p7_peer_check -and $Receipt.p7_peer_check.players) {
        $players = @($Receipt.p7_peer_check.players | ForEach-Object { [string]$_ })
    }
    $playerText = if ($players.Count -gt 0) { $players -join ', ' } else { 'none_recorded' }
    $applyClient = if ($Receipt.role_preflight -and $Receipt.role_preflight.summary) { [string]$Receipt.role_preflight.summary.apply_client } else { 'unknown' }
    $observeClient = if ($Receipt.role_preflight -and $Receipt.role_preflight.summary) { [string]$Receipt.role_preflight.summary.observe_client } else { 'unknown' }
    $omenApply = if ($Receipt.role_preflight -and $Receipt.role_preflight.summary) { [string]$Receipt.role_preflight.summary.omen_apply_enabled } else { 'unknown' }
    $i5Apply = if ($Receipt.role_preflight -and $Receipt.role_preflight.summary) { [string]$Receipt.role_preflight.summary.i5_apply_enabled } else { 'unknown' }

    $lines = @()
    $lines += '# Wave 0 live visual observation'
    $lines += ''
    $lines += "- Run ID: $($Receipt.run_id)"
    $lines += "- Machine verdict: $($Receipt.verdict)"
    $lines += "- Pattern: $($Receipt.pattern)"
    $lines += "- Desired apply client: $($Receipt.desired_apply_client)"
    $lines += "- Motion duration seconds: $($Receipt.motion_duration_seconds)"
    $lines += "- Capture duration seconds: $($Receipt.capture_duration_seconds)"
    $lines += "- P7 peer count at gate: $peerCount"
    $lines += "- P7 players at gate: $playerText"
    $lines += "- Role preflight: apply=$applyClient observe=$observeClient omen_apply=$omenApply i5_apply=$i5Apply"
    $lines += "- Machine receipt: $outputPath"
    $lines += ''
    $lines += '## Fill during the live pass'
    $lines += ''
    $lines += '| Field | Allowed values | Observed value |'
    $lines += '|---|---|---|'
    $lines += '| Apply client | omen / i5 / unknown |  |'
    $lines += '| Observe client | omen / i5 / unknown |  |'
    $lines += '| Visual result | followed_role / did_not_follow_role / inconclusive / not_observed |  |'
    $lines += '| Straight movement | smooth / glidey / teleporting / mixed / not_tested |  |'
    $lines += '| Stutter movement | smooth / glidey / teleporting / mixed / not_tested |  |'
    $lines += '| Role reversal run | yes / no / not_run |  |'
    $lines += '| Notes | free text |  |'
    $lines += ''
    $lines += '## Annotation commands'
    $lines += ''
    $lines += 'First pass example:'
    $lines += ''
    $lines += '```powershell'
    $lines += "powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson $outputPath -ApplyClient omen -ObserveClient i5 -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun no"
    $lines += '```'
    $lines += ''
    $lines += 'Role reversal example:'
    $lines += ''
    $lines += '```powershell'
    $lines += "powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson $outputPath -ApplyClient i5 -ObserveClient omen -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun yes"
    $lines += '```'
    $lines += ''
    $lines += 'After both directions are annotated, seal the visual evidence:'
    $lines += ''
    $lines += '```powershell'
    $lines += 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Seal-Wave0VisualEvidence.ps1 -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -OutputJson captures\wave0-live-seal\visual-seal.json'
    $lines += '```'
    $lines += ''
    $lines += 'Rules: do not edit the machine receipt. Use the annotation command to write immutable sidecars; use the seal command to write the derived two-direction index.'

    [IO.File]::WriteAllText($observationPath, (($lines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

function Write-Receipt {
    param($Receipt, [int]$ExitCode)

    $Receipt['observation_markdown'] = $observationPath
    $json = $Receipt | ConvertTo-Json -Depth 14
    [IO.File]::WriteAllText($outputPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-ObservationTemplate $Receipt
    Write-Host ("Wave 0 live gate: {0}" -f $Receipt.verdict)
    Write-Host ("Receipt JSON: {0}" -f $outputPath)
    Write-Host ("Observation Markdown: {0}" -f $observationPath)
    if ($Receipt.next_action) { Write-Host ("Next: {0}" -f $Receipt.next_action) }
    exit $ExitCode
}

function Invoke-JsonScript {
    param([string]$ScriptPath, [string[]]$Arguments)

    $command = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments
    $output = & powershell.exe @command 2>&1
    return [ordered]@{
        exit_code = $LASTEXITCODE
        stdout = (@($output) | Where-Object { $_ } | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        stderr = ''
    }
}

function Get-JsonFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

$syntheticPath = Join-Path $outputDirectory 'synthetic-motion.json'
$readinessPath = Join-Path $outputDirectory 'readiness.json'
$applyRolePath = Join-Path $outputDirectory 'apply-role-command.json'
$rolePreflightPath = Join-Path $outputDirectory 'role-preflight.json'
$capturePath = Join-Path $outputDirectory 'capture.json'
$motionPath = Join-Path $outputDirectory 'motion-command.json'

$receipt = [ordered]@{
    schema_version = 1
    run_id = $runId
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    pattern = $Pattern
    desired_apply_client = $DesiredApplyClient
    motion_duration_seconds = $MotionDurationSeconds
    capture_duration_seconds = $CaptureDurationSeconds
    interval_seconds = $IntervalSeconds
    warmup_seconds = $WarmupSeconds
    verdict = 'not_started'
    next_action = $null
    non_human_gates = [ordered]@{}
    p7_peer_check = $null
    apply_role_command = $null
    role_preflight = $null
    capture = $null
    motion_command = $null
    human_observation_required = [ordered]@{
        apply_client = 'record which client had Lumberjacks motion apply enabled'
        observe_client = 'record which client was observe-only'
        visual_result = 'record whether applied motion followed the selected role and whether straight/stutter movement looked smooth, glidey, or teleporting'
    }
}

if (-not $SkipSynthetic) {
    $synthetic = Invoke-JsonScript `
        -ScriptPath (Join-Path $repoRoot 'tools\wave0\Test-Wave0SyntheticMotion.ps1') `
        -Arguments @('-OutputJson', $syntheticPath)
    $receipt.non_human_gates.synthetic_motion = [ordered]@{
        exit_code = $synthetic.exit_code
        receipt_path = $syntheticPath
        receipt = Get-JsonFile $syntheticPath
    }
    if ($synthetic.exit_code -ne 0) {
        $receipt.verdict = 'blocked_by_synthetic_motion_gate'
        $receipt.next_action = 'Fix the Gateway motion relay seam before asking for another live two-client course.'
        Write-Receipt $receipt 1
    }
}

if (-not $SkipReadiness) {
    $readiness = Invoke-JsonScript `
        -ScriptPath (Join-Path $repoRoot 'tools\i5\Test-Wave0Readiness.ps1') `
        -Arguments @('-SummaryOnly', '-OutputJson', $readinessPath)
    $receipt.non_human_gates.readiness = [ordered]@{
        exit_code = $readiness.exit_code
        receipt_path = $readinessPath
        receipt = Get-JsonFile $readinessPath
    }
    if ($readiness.exit_code -ne 0 -or -not [bool]$receipt.non_human_gates.readiness.receipt.ready_for_derek) {
        $receipt.verdict = 'blocked_by_readiness_gate'
        $receipt.next_action = 'Repair release/client readiness before asking for a live movement course.'
        Write-Receipt $receipt 1
    }
}

$gatewayRoot = $GatewayUrl.TrimEnd('/')
$valheim = if ($MockValheimTelemetryJson) {
    $mockPath = Resolve-UnderRepo $MockValheimTelemetryJson
    $mock = Get-JsonFile $mockPath
    if (-not $mock) { throw "mock Valheim telemetry not found or invalid: $mockPath" }
    $mock
} else {
    Invoke-RestMethod -Uri "$gatewayRoot/api/v0/telemetry/valheim" -Method Get -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $httpTimeoutSeconds
}
$players = @($valheim.heartbeat.players)
$peerCount = [int]$valheim.heartbeat.peer_count
$receipt.p7_peer_check = [ordered]@{
    stale = [bool]$valheim.stale
    server_state = [string]$valheim.heartbeat.server_state
    peer_count = $peerCount
    players = $players
    http_timeout_seconds = $httpTimeoutSeconds
}

if ([bool]$valheim.stale -or [string]$valheim.heartbeat.server_state -ne 'ready') {
    $receipt.verdict = 'wait_p7_not_ready'
    $receipt.next_action = 'Wait for P7 Valheim heartbeat to be fresh and ready, then rerun.'
    Write-Receipt $receipt 0
}

if ($peerCount -lt 2) {
    $receipt.verdict = 'wait_for_two_real_clients'
    $receipt.next_action = 'Join both OMEN and i5 clients, wait for peer_count >= 2, then rerun this command.'
    Write-Receipt $receipt 0
}

if ($DesiredApplyClient -ne 'preserve' -and -not $MockRolePreflightJson) {
    $applyRole = Invoke-JsonScript `
        -ScriptPath (Join-Path $repoRoot 'tools\i5\Set-TwoClientApplyRoles.ps1') `
        -Arguments @('-ApplyClient', $DesiredApplyClient, '-Id', $runId, '-OutputJson', $applyRolePath)
    $receipt.apply_role_command = [ordered]@{
        exit_code = $applyRole.exit_code
        receipt_path = $applyRolePath
        receipt = Get-JsonFile $applyRolePath
    }
    if ($applyRole.exit_code -ne 0) {
        $receipt.verdict = 'blocked_by_apply_role_command'
        $receipt.next_action = 'Fix the Companion apply-role command lane before running a live movement course.'
        Write-Receipt $receipt 1
    }
}

if (-not $SkipRolePreflight) {
    if ($MockRolePreflightJson) {
        $mockRolePath = Resolve-UnderRepo $MockRolePreflightJson
        $rolePreflightReceipt = Get-JsonFile $mockRolePath
        if (-not $rolePreflightReceipt) { throw "mock role preflight receipt not found or invalid: $mockRolePath" }
        Copy-Item -LiteralPath $mockRolePath -Destination $rolePreflightPath -Force
        $rolePreflight = [ordered]@{
            exit_code = 0
            stdout = "mock role preflight: $mockRolePath"
            stderr = ''
        }
    } else {
        $rolePreflightArgs = @(
            '-DurationSeconds', '5',
            '-IntervalSeconds', '1',
            '-Label', "$runId-role-preflight",
            '-SummaryOnly',
            '-OutputJson', $rolePreflightPath
        )
        $rolePreflight = Invoke-JsonScript `
            -ScriptPath (Join-Path $repoRoot 'tools\i5\Start-TwoClientCapture.ps1') `
            -Arguments $rolePreflightArgs
        $rolePreflightReceipt = Get-JsonFile $rolePreflightPath
    }
    $receipt.role_preflight = [ordered]@{
        exit_code = $rolePreflight.exit_code
        receipt_path = $rolePreflightPath
        source = if ($MockRolePreflightJson) { 'mock' } else { 'live_capture' }
        receipt = $rolePreflightReceipt
    }

    if ($rolePreflight.exit_code -ne 0 -or -not $rolePreflightReceipt -or -not $rolePreflightReceipt.comparison) {
        $receipt.verdict = 'blocked_by_role_preflight_capture'
        $receipt.next_action = 'Fix the two-client capture lane before running a live movement course.'
        Write-Receipt $receipt 1
    }

    $omenApply = $rolePreflightReceipt.comparison.omen.final_local_motion.apply_enabled
    $i5Apply = $rolePreflightReceipt.comparison.i5.final_local_motion.apply_enabled
    $receipt.role_preflight['summary'] = [ordered]@{
        omen_apply_enabled = $omenApply
        i5_apply_enabled = $i5Apply
        exactly_one_apply_enabled = ($null -ne $omenApply -and $null -ne $i5Apply -and [bool]$omenApply -ne [bool]$i5Apply)
        apply_client = if ($omenApply -eq $true -and $i5Apply -eq $false) { 'omen' } elseif ($omenApply -eq $false -and $i5Apply -eq $true) { 'i5' } else { 'ambiguous' }
        observe_client = if ($omenApply -eq $true -and $i5Apply -eq $false) { 'i5' } elseif ($omenApply -eq $false -and $i5Apply -eq $true) { 'omen' } else { 'ambiguous' }
    }

    if (-not $receipt.role_preflight.summary.exactly_one_apply_enabled) {
        $receipt.verdict = 'blocked_by_ambiguous_apply_roles'
        $receipt.next_action = 'Set exactly one client APPLY on and the other OBSERVE ONLY, then rerun. Do not run the movement course while both clients have the same apply state.'
        Write-Receipt $receipt 0
    }

    $receipt.human_observation_required['apply_client'] = $receipt.role_preflight.summary.apply_client
    $receipt.human_observation_required['observe_client'] = $receipt.role_preflight.summary.observe_client

    if ($StopAfterRolePreflight) {
        $receipt.verdict = 'role_preflight_passed_stopped_before_motion'
        $receipt.next_action = 'Role preflight passed. Rerun without -StopAfterRolePreflight to start capture and bounded motion.'
        Write-Receipt $receipt 0
    }
}

$captureArgs = @(
    '-DurationSeconds', [string]$CaptureDurationSeconds,
    '-IntervalSeconds', [string]$IntervalSeconds,
    '-Label', $runId,
    '-SummaryOnly',
    '-OutputJson', $capturePath,
    '-BundleDirectory', $bundlePath
)

$captureJob = Start-Job -ScriptBlock {
    param($RepoRoot, $Args)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'tools\i5\Start-TwoClientCapture.ps1') @Args
    if ($LASTEXITCODE -ne 0) { throw "capture command failed with exit code $LASTEXITCODE" }
} -ArgumentList $repoRoot, $captureArgs

if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }

$motion = Invoke-JsonScript `
    -ScriptPath (Join-Path $repoRoot 'tools\i5\Start-TwoClientMotionTest.ps1') `
    -Arguments @('-Pattern', $Pattern, '-DurationSeconds', [string]$MotionDurationSeconds, '-Id', $runId, '-OutputJson', $motionPath)
$receipt.motion_command = [ordered]@{
    exit_code = $motion.exit_code
    receipt_path = $motionPath
    receipt = Get-JsonFile $motionPath
}

$captureTimeoutSeconds = [Math]::Max(60, $CaptureDurationSeconds + $WarmupSeconds + 60)
$captureCompleted = Wait-Job -Job $captureJob -Timeout $captureTimeoutSeconds
$captureStdout = ''
$captureError = $null
if (-not $captureCompleted) {
    Stop-Job -Job $captureJob -ErrorAction SilentlyContinue
    $captureError = "capture command timed out after ${captureTimeoutSeconds}s"
} else {
try {
    $captureStdout = (Receive-Job $captureJob -ErrorAction Stop | Out-String)
} catch {
    $captureError = $_.Exception.Message
}
}
Remove-Job $captureJob -Force -ErrorAction SilentlyContinue

$receipt.capture = [ordered]@{
    receipt_path = $capturePath
    stdout = $captureStdout
    error = $captureError
    timeout_seconds = $captureTimeoutSeconds
    receipt = Get-JsonFile $capturePath
}

if ($motion.exit_code -ne 0) {
    $receipt.verdict = 'motion_command_failed'
    $receipt.next_action = 'Inspect the Companion motion command receipt before rerunning a live course.'
    Write-Receipt $receipt 1
}

if ($captureError) {
    $receipt.verdict = 'capture_failed'
    $receipt.next_action = 'Fix the failed capture lane before using this as live evidence.'
    Write-Receipt $receipt 1
}

$comparison = $receipt.capture.receipt.comparison
if ($comparison -and $comparison.level -eq 'ok') {
    $receipt.verdict = 'transport_evidence_collected_human_visual_pending'
    $receipt.next_action = 'Add Derek visual observation, then run role reversal.'
    Write-Receipt $receipt 0
}

$receipt.verdict = 'transport_evidence_inconclusive_human_review_needed'
$receipt.next_action = if ($comparison) { $comparison.next_action } else { 'Inspect capture receipt before rerunning.' }
Write-Receipt $receipt 0
