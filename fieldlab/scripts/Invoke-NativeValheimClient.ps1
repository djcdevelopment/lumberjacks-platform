#Requires -Version 5.1
<#
.SYNOPSIS
Run one native Windows Valheim client through a bounded unattended lifecycle.

.DESCRIPTION
This is the physical OMEN/i5 client seam. It never supplies Steam credentials, creates a
character, sends arbitrary input, or leaves auto-join enabled in the player's config.

The caller writes a short-lived native-autotest request, launches Valheim through the already
running interactive Steam client, waits for ComfyNetworkSense to report a real joined peer, and
retains the client logs plus a machine-readable receipt. `start` leaves the joined client running;
`smoke` always closes it after a bounded hold. `run-pending` is the fixed entrypoint for an
interactive scheduled task on a remote Windows client.
#>
[CmdletBinding()]
param(
    [ValidateSet('preflight', 'request-preview', 'start', 'smoke', 'poison-smoke', 'descriptor-smoke', 'cold-join-failure-smoke', 'status', 'stop', 'restore-lab-session', 'install-task', 'queue-smoke', 'task-status', 'run-pending')]
    [string] $Action = 'preflight',

    [ValidateSet('omen', 'i5')]
    [string] $Client = 'omen',

    [ValidateSet('candidate', 'final')]
    [string] $ArtifactStage = 'candidate',

    [string] $Character = '',

    [string] $Server = 'comfy-p7.duckdns.org:2456',

    [string] $GatewayUrl = '',

    # Enrollment-consumer lane credentials (ADR 0017). Both or neither; when supplied the
    # managed config gets lumberjacksEnrollmentId/lumberjacksClientAccessKey and the
    # authoritative consumer armed for the run, restored byte-exact on stop. Values are
    # never written to receipts; the pending-request file that carries them to a remote
    # client is deleted on consume.
    [string] $EnrollmentId = '',

    [string] $ClientAccessKey = '',

    # HARNESS FAULT INJECTION (never for play): paces the journal consumer to one
    # apply/ACK per interval to reproduce the candidate-8/11 redelivery wedge on
    # demand. Written into the managed config for the run, restored byte-exact.
    [ValidateRange(0, 5000)]
    [int] $ZdoJournalApplyThrottleMs = 0,

    [string] $RunId = '',

    [string] $ValheimRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',

    [string] $SteamExe = 'C:\Program Files (x86)\Steam\steam.exe',

    [string] $DllPath = '',

    [string] $ScenarioPath = '',

    [string] $EvidenceRoot = '',

    # Invoke-NativeValheimCutoverScenario.ps1 forwards its own -WaitSeconds here on
    # both the local and i5 legs, so this ceiling must stay >= the orchestrator's.
    [ValidateRange(60, 1800)]
    [int] $WaitSeconds = 600,

    [ValidateRange(0, 300)]
    [int] $HoldSeconds = 20,

    [switch] $EnableRoutedRpcCutover,

    [switch] $EnableZdoJournalCutover,

    [switch] $EnableZdoJournalCanonicalSession,

    [switch] $EnableOwnershipLeaseCutover,

    [switch] $EnableWorldZoneCutover,

    [switch] $EnableMotionAuthorityCutover,

    # Lab-only session gate.  Normal gameplay remains at-rest with the
    # canonical Lumberjacks session and motion lane disabled; a bounded smoke
    # run may opt in and restores the exact config bytes when it stops.
    [switch] $EnableLabSession,

    [switch] $EnableSocketQuarantineCutover,

    [switch] $EnableSteamFreeColdJoin,

    [ValidateSet('', 'wrong_protocol', 'wrong_release', 'wrong_world_generation')]
    [string] $WorldDescriptorFault = '',

    [ValidateSet('', 'invalid_enrollment', 'gateway_unavailable')]
    [string] $ColdJoinFailureMode = '',

    [string[]] $LaunchArguments = @(),

    [string] $PendingRequestPath = '',

    [string] $TaskName = 'BaselineNativeValheimClient'
)

$ErrorActionPreference = 'Stop'
$script:ActiveRunDirectory = $null
$script:LastReceipt = $null
$script:JoinedRow = $null
$script:PoisonRow = $null
$script:ColdJoinFailureRow = $null
$script:ScenarioTerminalRow = $null
$script:ResumeCount = 0
$script:ProfileRecoveryCount = 0
$script:LabConfigChanged = $false
$script:LabConfigBackup = $null

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repoRoot 'fieldlab\runs\native-valheim'
}
if ([string]::IsNullOrWhiteSpace($PendingRequestPath)) {
    $PendingRequestPath = Join-Path $PSScriptRoot 'pending-native-valheim.json'
}

$pluginPath = Join-Path $ValheimRoot 'BepInEx\plugins\ComfyNetworkSense.dll'
$configRoot = Join-Path $ValheimRoot 'BepInEx\config'
$autotestRoot = Join-Path $configRoot 'comfy-network-sense'
$autotestRequestPath = Join-Path $autotestRoot 'native-autotest-request.json'
$autotestReceiptsPath = Join-Path $autotestRoot 'native-autotest-receipts.jsonl'
$nativeNetworkLedgerPath = Join-Path $autotestRoot 'native-network-use.jsonl'
$cutoverScenarioPath = Join-Path $autotestRoot 'native-cutover-scenario.json'
$cutoverScenarioReceiptsPath = Join-Path $autotestRoot 'native-cutover-scenario-receipts.jsonl'
$gameSessionReceiptsPath = Join-Path $autotestRoot 'lumberjacks-game-session.jsonl'
$directControlReceiptsPath = Join-Path $autotestRoot 'direct-control-cutover.jsonl'
$routedRpcReceiptsPath = Join-Path $autotestRoot 'routed-rpc-cutover.jsonl'
$shipCutoverReceiptsPath = Join-Path $autotestRoot 'ship-cutover.jsonl'
$saddleCutoverReceiptsPath = Join-Path $autotestRoot 'saddle-cutover.jsonl'
$creatureAiCutoverReceiptsPath = Join-Path $autotestRoot 'creature-ai-cutover.jsonl'
$containerCutoverReceiptsPath = Join-Path $autotestRoot 'container-cutover.jsonl'
$zdoJournalReceiptsPath = Join-Path $autotestRoot 'zdo-journal-cutover.jsonl'
$ownershipLeaseReceiptsPath = Join-Path $autotestRoot 'ownership-lease-cutover.jsonl'
$worldZoneReceiptsPath = Join-Path $autotestRoot 'world-zone-cutover.jsonl'
$motionAuthorityReceiptsPath = Join-Path $autotestRoot 'motion-authority-cutover.jsonl'
$socketQuarantineReceiptsPath = Join-Path $autotestRoot 'socket-quarantine-cutover.jsonl'
$logicalPeerReceiptsPath = Join-Path $autotestRoot 'logical-peer-cutover.jsonl'
# The frozen build writes these; the native harness retains them in the run
# bundle so synchronous hitch attribution and asynchronous stall evidence do
# not survive only in the live client directory.
$perfHitchesPath = Join-Path $autotestRoot 'perf-hitches.jsonl'
$perfSectionsPath = Join-Path $autotestRoot 'perf-sections.jsonl'
$perfWatchdogPath = Join-Path $autotestRoot 'perf-watchdog.jsonl'
$bepInExLogPath = Join-Path $ValheimRoot 'BepInEx\LogOutput.log'
$playerLogPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim\Player.log'

if ($EnableSteamFreeColdJoin -and
    (-not $EnableRoutedRpcCutover -or
     -not $EnableZdoJournalCutover -or
     -not $EnableZdoJournalCanonicalSession -or
     -not $EnableOwnershipLeaseCutover -or
     -not $EnableWorldZoneCutover -or
     -not $EnableMotionAuthorityCutover -or
     $EnableSocketQuarantineCutover)) {
    throw '-EnableSteamFreeColdJoin requires a coherent C2b-C6 request and forbids socket quarantine.'
}
if ($ArtifactStage -eq 'final' -and $EnableSocketQuarantineCutover) {
    throw 'The final artifact cannot request the retired socket-quarantine migration falsifier.'
}
if (-not [string]::IsNullOrWhiteSpace($ColdJoinFailureMode) -and
    (-not $EnableSteamFreeColdJoin -or
     -not [string]::IsNullOrWhiteSpace($WorldDescriptorFault))) {
    throw 'ColdJoinFailureMode requires Steam-free cold join and cannot be combined with WorldDescriptorFault.'
}

function Write-Utf8NoBom([string] $Path, [string] $Value) {
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllText($temporary, $Value, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Write-JsonAtomic([string] $Path, [object] $Value) {
    Write-Utf8NoBom $Path (($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
}

function New-RunId() {
    return 'native-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $Client
}

function Test-SafeToken([string] $Value) {
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Length -le 80 -and
        $Value -match '^[A-Za-z0-9._-]+$'
}

# Both-or-neither, and shell-safe: these values are spliced into the managed config and
# (for a remote client) a pending-request file, so reject anything outside token shape.
if ([string]::IsNullOrWhiteSpace($EnrollmentId) -ne
    [string]::IsNullOrWhiteSpace($ClientAccessKey)) {
    throw 'EnrollmentId and ClientAccessKey must be supplied together.'
}
if (-not [string]::IsNullOrWhiteSpace($EnrollmentId)) {
    if ($EnrollmentId -notmatch '^[0-9a-fA-F]{32}$') {
        throw "EnrollmentId is not a 32-hex enrollment id: $EnrollmentId"
    }
    if ($ClientAccessKey.Length -gt 256 -or
        $ClientAccessKey -notmatch '^[A-Za-z0-9._-]{8,}$') {
        throw 'ClientAccessKey is not a safe token (base64url shape expected).'
    }
}

function Get-CharacterProfiles() {
    $files = @()
    $localCharacters = Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim\characters'
    if (Test-Path -LiteralPath $localCharacters -PathType Container) {
        $files += Get-ChildItem -LiteralPath $localCharacters -Filter '*.fch' -File -ErrorAction SilentlyContinue
        $files += Get-ChildItem -LiteralPath $localCharacters -Filter '*.fch.new' -File -ErrorAction SilentlyContinue
    }

    $steamUserdata = 'C:\Program Files (x86)\Steam\userdata'
    if (Test-Path -LiteralPath $steamUserdata -PathType Container) {
        $files += Get-ChildItem -LiteralPath $steamUserdata -Recurse -Filter '*.fch' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\892970\\remote\\characters\\' }
        $files += Get-ChildItem -LiteralPath $steamUserdata -Recurse -Filter '*.fch.new' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\892970\\remote\\characters\\' }
    }

    return @($files |
        Where-Object { $_.Name -notmatch '(?i)backup' } |
        ForEach-Object { $_.Name -replace '(?i)\.fch(?:\.new)?$', '' } |
        Sort-Object -Unique)
}

function Get-SessionFacts() {
    $currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $explorerSessions = @(Get-Process explorer -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty SessionId -Unique)
    $steamSessions = @(Get-Process steam -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty SessionId -Unique)
    [ordered]@{
        current = $currentSession
        explorer = $explorerSessions
        steam = $steamSessions
        interactive_available = @($explorerSessions | Where-Object { $_ -in $steamSessions }).Count -gt 0
        current_is_interactive = $currentSession -in $explorerSessions -and $currentSession -in $steamSessions
    }
}

function Get-Preflight([bool] $RequireCurrentInteractive = $false) {
    $sessions = Get-SessionFacts
    $profiles = @(Get-CharacterProfiles)
    $gpu = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
        ForEach-Object {
            [ordered]@{
                name = $_.Name
                driver_version = $_.DriverVersion
                status = $_.Status
            }
        })
    $characterFound = [string]::IsNullOrWhiteSpace($Character) -or
        @($profiles | Where-Object { $_ -ieq $Character }).Count -gt 0
    $checks = @(
        [ordered]@{ name = 'valheim_root'; passed = (Test-Path -LiteralPath $ValheimRoot -PathType Container); detail = $ValheimRoot },
        [ordered]@{ name = 'valheim_exe'; passed = (Test-Path -LiteralPath (Join-Path $ValheimRoot 'valheim.exe') -PathType Leaf); detail = (Join-Path $ValheimRoot 'valheim.exe') },
        [ordered]@{ name = 'steam_exe'; passed = (Test-Path -LiteralPath $SteamExe -PathType Leaf); detail = $SteamExe },
        [ordered]@{ name = 'bepinex_plugin'; passed = (Test-Path -LiteralPath $pluginPath -PathType Leaf); detail = $pluginPath },
        [ordered]@{ name = 'interactive_session'; passed = $sessions.interactive_available; detail = ($sessions | ConvertTo-Json -Compress) },
        [ordered]@{ name = 'current_session_interactive'; passed = (-not $RequireCurrentInteractive -or $sessions.current_is_interactive); detail = "required=$RequireCurrentInteractive current=$($sessions.current)" },
        [ordered]@{ name = 'character_profile'; passed = $characterFound; detail = if ($Character) { $Character } else { 'not requested' } },
        [ordered]@{ name = 'gpu_present'; passed = $gpu.Count -gt 0; detail = (@($gpu | ForEach-Object name) -join ', ') }
    )
    $blockers = @($checks | Where-Object { -not $_.passed } | ForEach-Object name)
    [ordered]@{
        schema_version = 1
        receipt_type = 'native_valheim_preflight'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        client = $Client
        server = $Server
        character = $Character
        result = if ($blockers.Count -eq 0) { 'ready' } else { 'blocked' }
        blockers = $blockers
        checks = $checks
        sessions = $sessions
        profiles = $profiles
        gpu = $gpu
        valheim_running = [bool](Get-Process valheim -ErrorAction SilentlyContinue)
        steam_running = [bool](Get-Process steam -ErrorAction SilentlyContinue)
        plugin_version = if (Test-Path -LiteralPath $pluginPath -PathType Leaf) {
            (Get-Item -LiteralPath $pluginPath).VersionInfo.FileVersion
        } else { $null }
    }
}

function Stop-Valheim([bool] $FailIfNotStopped = $true) {
    if (Test-Path -LiteralPath $autotestRequestPath -PathType Leaf) {
        Remove-Item -LiteralPath $autotestRequestPath -Force
    }

    $processes = @(Get-Process -Name valheim,UnityCrashHandler64 -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -ne 0) { [void]$process.CloseMainWindow() }
    }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and
        (Get-Process -Name valheim,UnityCrashHandler64 -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 250
    }
    @(Get-Process -Name valheim,UnityCrashHandler64 -ErrorAction SilentlyContinue) |
        Stop-Process -Force -ErrorAction SilentlyContinue
    $forcedDeadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $forcedDeadline -and
        (Get-Process -Name valheim,UnityCrashHandler64 -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(Get-Process -Name valheim,UnityCrashHandler64 -ErrorAction SilentlyContinue)
    if ($FailIfNotStopped -and $remaining.Count -gt 0) {
        throw "Valheim did not stop; remaining process ids: $(@($remaining.Id) -join ',')"
    }
    return $remaining.Count -eq 0
}

function Enable-LabSessionConfig() {
    $hasEnrollment = -not [string]::IsNullOrWhiteSpace($EnrollmentId)
    $hasThrottle = $ZdoJournalApplyThrottleMs -gt 0
    if ((-not $EnableLabSession -and -not $hasEnrollment -and -not $hasThrottle) -or
        $script:LabConfigChanged) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $configRoot -PathType Container)) {
        throw "Valheim config directory is missing: $configRoot"
    }
    $configPath = Join-Path $configRoot 'djcdevelopment.valheim.comfynetworksense.cfg'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "ComfyNetworkSense config is missing: $configPath"
    }
    $settings = [ordered]@{}
    if ($EnableLabSession) {
        $settings['lumberjacksGameSessionEnabled'] = 'true'
        $settings['lumberjacksMotionEnabled'] = 'true'
    }
    if ($hasEnrollment) {
        $settings['lumberjacksEnrollmentId'] = $EnrollmentId
        $settings['lumberjacksClientAccessKey'] = $ClientAccessKey
        $settings['zdoAuthoritativeConsumerEnabled'] = 'true'
    }
    if ($hasThrottle) {
        $settings['zdoJournalApplyThrottleMs'] = [string]$ZdoJournalApplyThrottleMs
    }
    $originalBytes = [IO.File]::ReadAllBytes($configPath)
    $content = [Text.Encoding]::UTF8.GetString($originalBytes)
    $updated = $content
    foreach ($setting in $settings.Keys) {
        $pattern = '(?m)^(' + [regex]::Escape($setting) + '\s*=\s*)\S*\s*$'
        if (-not [regex]::IsMatch($updated, $pattern)) {
            throw "Lab session setting is missing from config: $setting"
        }
        $replacement = '${1}' + $settings[$setting].Replace('$', '$$')
        $updated = [regex]::Replace($updated, $pattern, $replacement)
    }
    $script:LabConfigBackup = Join-Path $script:ActiveRunDirectory 'config-before-lab-session.cfg'
    [IO.File]::WriteAllBytes($script:LabConfigBackup, $originalBytes)
    [IO.File]::WriteAllText($configPath, $updated, [Text.UTF8Encoding]::new($false))
    $script:LabConfigChanged = $true
    return [ordered]@{
        path = $configPath
        backup = $script:LabConfigBackup
        # Setting NAMES only — the access key must never reach a receipt.
        enabled_settings = @($settings.Keys)
        enrollment_lane = $hasEnrollment
        before_sha256 = (Get-FileHash -LiteralPath $script:LabConfigBackup -Algorithm SHA256).Hash.ToLowerInvariant()
        active_sha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Restore-LabSessionConfig() {
    if (-not $script:LabConfigChanged -or [string]::IsNullOrWhiteSpace($script:LabConfigBackup)) {
        return $null
    }
    $configPath = Join-Path $configRoot 'djcdevelopment.valheim.comfynetworksense.cfg'
    if (-not (Test-Path -LiteralPath $script:LabConfigBackup -PathType Leaf)) {
        throw "Lab session config backup is missing: $script:LabConfigBackup"
    }
    [IO.File]::WriteAllBytes($configPath, [IO.File]::ReadAllBytes($script:LabConfigBackup))
    $restoredHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $backupHash = (Get-FileHash -LiteralPath $script:LabConfigBackup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($restoredHash -ne $backupHash) {
        throw "Lab session config restore hash mismatch: $configPath"
    }
    $script:LabConfigChanged = $false
    return [ordered]@{
        path = $configPath
        restored = $true
        sha256 = $restoredHash
    }
}

function Restore-LabSessionConfigFromRun() {
    if ([string]::IsNullOrWhiteSpace($RunId) -or
        -not (Test-SafeToken $RunId)) {
        throw 'restore-lab-session requires a safe RunId.'
    }
    $runDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
    $backupPath = Join-Path $runDirectory 'config-before-lab-session.cfg'
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        throw "Lab session config backup is missing: $backupPath"
    }
    $configPath = Join-Path $configRoot 'djcdevelopment.valheim.comfynetworksense.cfg'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "ComfyNetworkSense config is missing: $configPath"
    }
    $backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllBytes($configPath, [IO.File]::ReadAllBytes($backupPath))
    $restoredHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($restoredHash -ne $backupHash) {
        throw "Lab session config restore hash mismatch: $configPath"
    }
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'native_valheim_lab_session_restore'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        run_id = $RunId
        client = $Client
        artifact_stage = $ArtifactStage
        backup = $backupPath
        config = $configPath
        restored = $true
        sha256 = $restoredHash
    }
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    Write-Utf8NoBom (Join-Path $runDirectory 'lab-session-restore.json') (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
    return $receipt
}

function Deploy-Mod() {
    if ([string]::IsNullOrWhiteSpace($DllPath)) { return $null }
    $source = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
    if (Get-Process valheim -ErrorAction SilentlyContinue) {
        throw 'Refusing mod deployment while Valheim is running.'
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pluginPath) | Out-Null
    Copy-Item -LiteralPath $source -Destination $pluginPath -Force
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw 'ComfyNetworkSense DLL hash mismatch after deployment.' }
    return [ordered]@{
        source = $source
        target = $pluginPath
        sha256 = $targetHash.ToLowerInvariant()
        version = (Get-Item -LiteralPath $pluginPath).VersionInfo.FileVersion
    }
}

function Deploy-Scenario() {
    if ([string]::IsNullOrWhiteSpace($ScenarioPath)) {
        if (Test-Path -LiteralPath $cutoverScenarioPath -PathType Leaf) {
            Remove-Item -LiteralPath $cutoverScenarioPath -Force
        }
        return $null
    }
    if (Get-Process valheim -ErrorAction SilentlyContinue) {
        throw 'Refusing scenario deployment while Valheim is running.'
    }
    $source = (Resolve-Path -LiteralPath $ScenarioPath -ErrorAction Stop).Path
    $info = Get-Item -LiteralPath $source
    if ($info.Length -le 0 -or $info.Length -gt 65536) {
        throw "Native cutover scenario must be between 1 and 65536 bytes: $source"
    }
    $manifest = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
    if ([int]$manifest.schema_version -ne 1) {
        throw 'Native cutover scenario schema_version must be 1.'
    }
    if ([string]$manifest.run_id -ne $RunId) {
        throw "Native cutover scenario run_id must equal RunId ($RunId)."
    }
    if (@($manifest.actions).Count -lt 1 -or @($manifest.actions).Count -gt 64) {
        throw 'Native cutover scenario must contain between 1 and 64 actions.'
    }
    New-Item -ItemType Directory -Path $autotestRoot -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $cutoverScenarioPath -Force
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $cutoverScenarioPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw 'Native cutover scenario hash mismatch after deployment.'
    }
    return [ordered]@{
        source = $source
        target = $cutoverScenarioPath
        sha256 = $targetHash.ToLowerInvariant()
        action_count = @($manifest.actions).Count
    }
}

function Read-AutotestRows([string] $RequestedRunId) {
    if (-not (Test-Path -LiteralPath $autotestReceiptsPath -PathType Leaf)) { return @() }
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $autotestReceiptsPath -Tail 100 -ErrorAction SilentlyContinue) {
        try {
            $row = $line | ConvertFrom-Json
            if ($row.run_id -eq $RequestedRunId) { $rows += $row }
        } catch { }
    }
    return $rows
}

function Read-RunRows(
    [string] $Path,
    [string] $RequestedRunId,
    [int] $Tail = 256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $Path -Tail $Tail -ErrorAction SilentlyContinue) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ([string]$row.run_id -eq $RequestedRunId) { $rows += $row }
        } catch { }
    }
    return $rows
}

function Wait-ForJoined(
    [string] $RequestedRunId,
    [int] $Seconds,
    [DateTimeOffset] $AfterUtc = [DateTimeOffset]::MinValue) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $rows = @(Read-AutotestRows $RequestedRunId | Where-Object {
            try { [DateTimeOffset]::Parse([string]$_.timestamp_utc) -gt $AfterUtc } catch { $false }
        })
        $failed = $rows | Where-Object state -eq 'failed' | Select-Object -Last 1
        if ($failed) { throw "Native autotest failed: $($failed.detail)" }
        $bootstrapFailed = $rows |
            Where-Object state -eq 'bootstrap_failed' |
            Select-Object -Last 1
        if ($bootstrapFailed) {
            throw "Native bootstrap failed: $($bootstrapFailed.detail)"
        }
        $descriptorRejected = @(
            Read-RunRows $worldZoneReceiptsPath $RequestedRunId 256 |
                Where-Object {
                    $_.state -eq 'descriptor_rejected_before_scene' -and
                    (try {
                        [DateTimeOffset]::Parse([string]$_.timestamp_utc) -gt
                            $AfterUtc
                    } catch { $false })
                } |
                Select-Object -Last 1)[0]
        if ($descriptorRejected) {
            throw "World descriptor rejected before scene: $($descriptorRejected.detail)"
        }
        $coldJoinFailed = @(
            Read-RunRows $logicalPeerReceiptsPath $RequestedRunId 256 |
                Where-Object {
                    $_.state -eq 'cold_join_failed' -and
                    (try {
                        [DateTimeOffset]::Parse([string]$_.timestamp_utc) -gt
                            $AfterUtc
                    } catch { $false })
                } |
                Select-Object -Last 1)[0]
        if ($coldJoinFailed) {
            throw "Steam-free cold join failed: $($coldJoinFailed.detail)"
        }
        $recovered = $rows | Where-Object state -eq 'profile_recovered' | Select-Object -Last 1
        if ($recovered) { return $recovered }
        $joined = $rows | Where-Object state -eq 'joined' | Select-Object -Last 1
        if ($joined) { return $joined }
        if (-not (Get-Process valheim -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 2
            if (-not (Get-Process valheim -ErrorAction SilentlyContinue)) {
                throw 'Valheim exited before the joined marker arrived.'
            }
        }
        Start-Sleep -Seconds 2
    }
    throw "AUTOTEST_JOINED was not observed within $Seconds seconds."
}

function Read-ScenarioRows([string] $RequestedRunId) {
    if (-not (Test-Path -LiteralPath $cutoverScenarioReceiptsPath -PathType Leaf)) {
        return @()
    }
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $cutoverScenarioReceiptsPath -Tail 512 -ErrorAction SilentlyContinue) {
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ($row.run_id -eq $RequestedRunId -and $row.client -eq $Client) {
                $rows += $row
            }
        } catch { }
    }
    return $rows
}

function Wait-ForScenarioTerminal(
    [string] $RequestedRunId,
    [int] $Seconds,
    [DateTimeOffset] $AfterUtc) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $rows = @(Read-ScenarioRows $RequestedRunId | Where-Object {
            try { [DateTimeOffset]::Parse([string]$_.timestamp_utc) -gt $AfterUtc } catch { $false }
        })
        $failed = $rows | Where-Object {
            $_.state -in @('failed', 'manifest_rejected')
        } | Select-Object -Last 1
        if ($failed) {
            throw "Native cutover scenario failed: state=$($failed.state) action=$($failed.action_id) detail=$($failed.detail)"
        }
        $terminal = $rows | Where-Object {
            $_.state -in @('resume_requested', 'scenario_complete')
        } | Select-Object -Last 1
        if ($terminal) { return $terminal }
        if (-not (Get-Process valheim -ErrorAction SilentlyContinue)) {
            throw 'Valheim exited before the cutover scenario reached a terminal marker.'
        }
        Start-Sleep -Seconds 1
    }
    throw "Native cutover scenario did not reach a terminal marker within $Seconds seconds."
}

function Wait-ForPoison([string] $RequestedRunId, [int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $nativeNetworkLedgerPath -PathType Leaf) {
            foreach ($line in Get-Content -LiteralPath $nativeNetworkLedgerPath -Tail 256 -ErrorAction SilentlyContinue) {
                try {
                    $row = $line | ConvertFrom-Json -ErrorAction Stop
                    if ($row.run_id -eq $RequestedRunId -and
                        $row.event -eq 'native_use' -and
                        $row.blocked -eq $true) {
                        return $row
                    }
                } catch { }
            }
        }
        if (-not (Get-Process valheim -ErrorAction SilentlyContinue)) {
            throw 'Valheim exited before a native-poison receipt arrived.'
        }
        Start-Sleep -Seconds 1
    }
    throw "A blocked native-poison receipt was not observed within $Seconds seconds."
}

function Wait-ForColdJoinFailure(
    [string] $RequestedRunId,
    [string] $FailureMode,
    [int] $Seconds,
    [DateTimeOffset] $AfterUtc) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $gameSessionReceiptsPath -PathType Leaf) {
            foreach ($line in Get-Content -LiteralPath $gameSessionReceiptsPath -Tail 512 -ErrorAction SilentlyContinue) {
                try {
                    $row = $line | ConvertFrom-Json -ErrorAction Stop
                    $timestamp = [DateTimeOffset]::Parse([string]$row.timestamp_utc)
                    if ($row.run_id -eq $RequestedRunId -and
                        $timestamp -gt $AfterUtc -and
                        $row.state -eq 'connection_error' -and
                        [string]$row.detail -match (
                            '(?:^|\s)fault_mode=' + [regex]::Escape($FailureMode) +
                            '(?:\s|$)')) {
                        $joined = @(Read-AutotestRows $RequestedRunId | Where-Object {
                            $_.state -eq 'joined' -and
                            ([DateTimeOffset]::Parse([string]$_.timestamp_utc) -gt $AfterUtc)
                        })
                        if ($joined.Count -gt 0) {
                            throw 'The client joined despite the requested cold-join failure.'
                        }
                        return $row
                    }
                } catch {
                    if ($_.Exception.Message -eq
                        'The client joined despite the requested cold-join failure.') {
                        throw
                    }
                }
            }
        }
        if (-not (Get-Process valheim -ErrorAction SilentlyContinue)) {
            throw 'Valheim exited before the cold-join failure marker arrived.'
        }
        Start-Sleep -Seconds 1
    }
    throw "A deterministic $FailureMode cold-join failure was not observed within $Seconds seconds."
}

function Wait-ForDescriptorRejected(
    [string] $RequestedRunId,
    [int] $Seconds,
    [DateTimeOffset] $AfterUtc) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $worldZoneReceiptsPath -PathType Leaf) {
            foreach ($line in Get-Content -LiteralPath $worldZoneReceiptsPath -Tail 256 -ErrorAction SilentlyContinue) {
                try {
                    $row = $line | ConvertFrom-Json -ErrorAction Stop
                    $timestamp = [DateTimeOffset]::Parse([string]$row.timestamp_utc)
                    if ($row.run_id -eq $RequestedRunId -and
                        $timestamp -gt $AfterUtc -and
                        $row.state -eq 'descriptor_rejected_before_scene') {
                        $joined = @(Read-AutotestRows $RequestedRunId | Where-Object {
                            $_.state -eq 'joined' -and
                            ([DateTimeOffset]::Parse([string]$_.timestamp_utc) -gt $AfterUtc)
                        })
                        if ($joined.Count -gt 0) {
                            throw 'The client joined despite the rejected world descriptor.'
                        }
                        return $row
                    }
                } catch {
                    if ($_.Exception.Message -eq
                        'The client joined despite the rejected world descriptor.') {
                        throw
                    }
                }
            }
        }
        if (-not (Get-Process valheim -ErrorAction SilentlyContinue)) {
            throw 'Valheim exited before the descriptor rejection marker arrived.'
        }
        Start-Sleep -Seconds 1
    }
    throw "A pre-scene world descriptor rejection was not observed within $Seconds seconds."
}

function Copy-EvidenceFile([string] $Source, [string] $DestinationName) {
    if (-not $script:ActiveRunDirectory -or
        -not (Test-Path -LiteralPath $Source -PathType Leaf)) { return $null }
    $destination = Join-Path $script:ActiveRunDirectory $DestinationName
    try {
        Copy-Item -LiteralPath $Source -Destination $destination -Force
        return $destination
    } catch {
        return $null
    }
}

function Write-RunReceipt([string] $Result, [object] $Preflight, [object] $Deployment, [string] $Error = '') {
    if (-not $script:ActiveRunDirectory) { return }
    New-Item -ItemType Directory -Force -Path $script:ActiveRunDirectory | Out-Null
    $process = Get-Process valheim -ErrorAction SilentlyContinue | Select-Object -First 1
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'native_valheim_lifecycle'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        run_id = $RunId
        client = $Client
        action = $Action
        artifact_stage = $ArtifactStage
        result = $Result
        error = $Error
        server = $Server
        character = $Character
        process_id = if ($process) { $process.Id } else { $null }
        process_session = if ($process) { $process.SessionId } else { $null }
        joined = $script:JoinedRow
        native_poison = $script:PoisonRow
        cold_join_failure = $script:ColdJoinFailureRow
        scenario_terminal = $script:ScenarioTerminalRow
        resume_count = $script:ResumeCount
        profile_recovery_count = $script:ProfileRecoveryCount
        lab_session_requested = [bool]$EnableLabSession
        lab_session_config_changed = [bool]$script:LabConfigChanged
        lab_session_config_backup = $script:LabConfigBackup
        migration_request_fields_emitted = $ArtifactStage -eq 'candidate'
        native_network_poison_requested =
            $ArtifactStage -eq 'candidate' -and
            ($Action -eq 'poison-smoke' -or [bool]$EnableSteamFreeColdJoin)
        routed_rpc_cutover_requested =
            $ArtifactStage -eq 'candidate' -and [bool]$EnableRoutedRpcCutover
        zdo_journal_cutover_requested =
            $ArtifactStage -eq 'candidate' -and [bool]$EnableZdoJournalCutover
        zdo_journal_canonical_session_requested =
            $ArtifactStage -eq 'candidate' -and
            [bool]$EnableZdoJournalCanonicalSession
        ownership_lease_cutover_requested =
            $ArtifactStage -eq 'candidate' -and
            [bool]$EnableOwnershipLeaseCutover
        world_zone_cutover_requested =
            $ArtifactStage -eq 'candidate' -and [bool]$EnableWorldZoneCutover
        motion_authority_cutover_requested =
            $ArtifactStage -eq 'candidate' -and
            [bool]$EnableMotionAuthorityCutover
        socket_quarantine_cutover_requested =
            $ArtifactStage -eq 'candidate' -and
            [bool]$EnableSocketQuarantineCutover
        steam_free_cold_join_requested = [bool]$EnableSteamFreeColdJoin
        world_descriptor_fault = $WorldDescriptorFault
        cold_join_failure_mode = $ColdJoinFailureMode
        preflight = $Preflight
        deployment = $Deployment
        plugin_sha256 = if (Test-Path -LiteralPath $pluginPath -PathType Leaf) {
            (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $null }
        request_consumed = -not (Test-Path -LiteralPath $autotestRequestPath -PathType Leaf)
        valheim_running = [bool]$process
        evidence = [ordered]@{
            bep_inex_log = Copy-EvidenceFile $bepInExLogPath 'bepinex.log'
            player_log = Copy-EvidenceFile $playerLogPath 'player.log'
            autotest_receipts = Copy-EvidenceFile $autotestReceiptsPath 'native-autotest-receipts.jsonl'
            native_network_use = Copy-EvidenceFile $nativeNetworkLedgerPath 'native-network-use.jsonl'
            cutover_scenario_receipts =
                Copy-EvidenceFile $cutoverScenarioReceiptsPath 'native-cutover-scenario-receipts.jsonl'
            lumberjacks_game_session =
                Copy-EvidenceFile $gameSessionReceiptsPath 'lumberjacks-game-session.jsonl'
            direct_control_cutover =
                Copy-EvidenceFile $directControlReceiptsPath 'direct-control-cutover.jsonl'
            routed_rpc_cutover =
                Copy-EvidenceFile $routedRpcReceiptsPath 'routed-rpc-cutover.jsonl'
            ship_cutover =
                Copy-EvidenceFile $shipCutoverReceiptsPath 'ship-cutover.jsonl'
            saddle_cutover =
                Copy-EvidenceFile $saddleCutoverReceiptsPath 'saddle-cutover.jsonl'
            creature_ai_cutover =
                Copy-EvidenceFile $creatureAiCutoverReceiptsPath 'creature-ai-cutover.jsonl'
            container_cutover =
                Copy-EvidenceFile $containerCutoverReceiptsPath 'container-cutover.jsonl'
            zdo_journal_cutover =
                Copy-EvidenceFile $zdoJournalReceiptsPath 'zdo-journal-cutover.jsonl'
            ownership_lease_cutover =
                Copy-EvidenceFile $ownershipLeaseReceiptsPath 'ownership-lease-cutover.jsonl'
            world_zone_cutover =
                Copy-EvidenceFile $worldZoneReceiptsPath 'world-zone-cutover.jsonl'
            motion_authority_cutover =
                Copy-EvidenceFile $motionAuthorityReceiptsPath 'motion-authority-cutover.jsonl'
            socket_quarantine_cutover =
                Copy-EvidenceFile $socketQuarantineReceiptsPath 'socket-quarantine-cutover.jsonl'
            logical_peer_cutover =
                Copy-EvidenceFile $logicalPeerReceiptsPath 'logical-peer-cutover.jsonl'
            perf_hitches = Copy-EvidenceFile $perfHitchesPath 'perf-hitches.jsonl'
            perf_sections = Copy-EvidenceFile $perfSectionsPath 'perf-sections.jsonl'
            perf_watchdog = Copy-EvidenceFile $perfWatchdogPath 'perf-watchdog.jsonl'
        }
    }
    $path = Join-Path $script:ActiveRunDirectory 'lifecycle.json'
    Write-JsonAtomic $path $receipt
    $script:LastReceipt = $receipt
}

function New-NativeAutotestRequest(
    [bool] $ExpectPoisonTrip,
    [DateTimeOffset] $Now) {
    # C7 is a zero-use proof, so poison must stay armed without requiring a
    # trip. poison-smoke remains the separate negative test that expects one.
    $requestPoison =
        $ExpectPoisonTrip -or [bool]$EnableSteamFreeColdJoin
    $request = [ordered]@{
        schema_version = 1
        artifact_stage = $ArtifactStage
        run_id = $RunId
        client = $Client
        character = $Character
        server = $Server
        lumberjacks_gateway_url = $GatewayUrl
        created_utc = $Now.ToString('o')
        expires_utc = $Now.AddMinutes(15).ToString('o')
        steam_free_cold_join = [bool]$EnableSteamFreeColdJoin
        world_descriptor_fault = $WorldDescriptorFault
        cold_join_fault = $ColdJoinFailureMode
    }
    if ($ArtifactStage -eq 'candidate') {
        $request.native_network_poison = $requestPoison
        $request.routed_rpc_cutover = [bool]$EnableRoutedRpcCutover
        $request.zdo_journal_cutover = [bool]$EnableZdoJournalCutover
        $request.zdo_journal_canonical_session =
            [bool]$EnableZdoJournalCanonicalSession
        $request.ownership_lease_cutover = [bool]$EnableOwnershipLeaseCutover
        $request.world_zone_cutover = [bool]$EnableWorldZoneCutover
        $request.motion_authority_cutover = [bool]$EnableMotionAuthorityCutover
        $request.socket_quarantine_cutover =
            [bool]$EnableSocketQuarantineCutover
    }
    return $request
}

function Write-NativeAutotestRequest([bool] $ExpectPoisonTrip) {
    $now = [DateTimeOffset]::UtcNow
    $request = New-NativeAutotestRequest $ExpectPoisonTrip $now
    Write-JsonAtomic $autotestRequestPath $request
    return $now
}

function Start-NativeProcess([bool] $ExpectPoison) {
    $launchedUtc = Write-NativeAutotestRequest $ExpectPoison
    $arguments = @('-applaunch', '892970', '-console') + @($LaunchArguments)
    if (-not $EnableSteamFreeColdJoin) {
        $arguments += @('+connect', $Server)
    }
    Start-Process -FilePath $SteamExe -ArgumentList $arguments

    $processDeadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 500
        $process = Get-Process valheim -ErrorAction SilentlyContinue | Select-Object -First 1
    } until ($process -or (Get-Date) -ge $processDeadline)
    if (-not $process) { throw 'Valheim did not start within 90 seconds.' }
    if ($process.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) {
        throw "Valheim started in session $($process.SessionId), expected current interactive session $([Diagnostics.Process]::GetCurrentProcess().SessionId)."
    }
    return [ordered]@{
        process = $process
        launched_utc = $launchedUtc
    }
}

function Start-NativeRun([bool] $StopAfterHold, [bool] $ExpectPoison) {
    if ([string]::IsNullOrWhiteSpace($Character)) { throw 'Character is required for start/smoke.' }
    if (-not (Test-SafeToken $RunId)) { throw "RunId is not a safe token: $RunId" }
    if ($Server -notmatch '^[^\s:]+:\d{2,5}$') { throw "Server must be host:port: $Server" }
    if (Get-Process valheim -ErrorAction SilentlyContinue) {
        throw 'Valheim is already running; use status or stop before starting a new run.'
    }

    $preflight = Get-Preflight -RequireCurrentInteractive $true
    $script:ActiveRunDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
    New-Item -ItemType Directory -Force -Path $script:ActiveRunDirectory | Out-Null
    if ($preflight.result -ne 'ready') {
        Write-RunReceipt 'blocked' $preflight $null (($preflight.blockers -join ','))
        throw "Native client preflight blocked: $($preflight.blockers -join ', ')"
    }

    $deployment = [ordered]@{
        mod = Deploy-Mod
        scenario = Deploy-Scenario
    }
    $deployment.lab_session = Enable-LabSessionConfig
    $attempt = Start-NativeProcess $ExpectPoison
    if ($ExpectPoison) {
        $script:PoisonRow = Wait-ForPoison $RunId $WaitSeconds
        [void](Stop-Valheim)
        $null = Restore-LabSessionConfig
        Write-RunReceipt 'native_poison_blocked_and_stopped' $preflight $deployment
        Write-Host ("native poison blocked {0} -> {1}" -f $script:PoisonRow.funnel, $script:ActiveRunDirectory)
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($ColdJoinFailureMode)) {
        $script:ColdJoinFailureRow =
            Wait-ForColdJoinFailure `
                $RunId $ColdJoinFailureMode $WaitSeconds `
                ([DateTimeOffset]$attempt.launched_utc)
        [void](Stop-Valheim)
        $null = Restore-LabSessionConfig
        Write-RunReceipt 'cold_join_failed_closed_and_stopped' $preflight $deployment
        Write-Host ("cold join failed closed ({0}) -> {1}" -f
            $ColdJoinFailureMode, $script:ActiveRunDirectory)
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($WorldDescriptorFault)) {
        if (-not $EnableWorldZoneCutover) {
            throw 'WorldDescriptorFault requires EnableWorldZoneCutover.'
        }
        $script:ScenarioTerminalRow =
            Wait-ForDescriptorRejected $RunId $WaitSeconds ([DateTimeOffset]$attempt.launched_utc)
        [void](Stop-Valheim)
        $null = Restore-LabSessionConfig
        Write-RunReceipt 'world_descriptor_rejected_before_scene_and_stopped' $preflight $deployment
        Write-Host ("descriptor rejected before scene ({0}) -> {1}" -f
            $WorldDescriptorFault, $script:ActiveRunDirectory)
        return
    }

    while ($true) {
        $joinOutcome =
            Wait-ForJoined $RunId $WaitSeconds ([DateTimeOffset]$attempt.launched_utc)
        if ($joinOutcome.state -eq 'profile_recovered') {
            $script:ProfileRecoveryCount++
            if ($script:ProfileRecoveryCount -gt 1) {
                throw 'Native autotest profile recovery requested more than one relaunch.'
            }
            [void](Stop-Valheim)
            Start-Sleep -Seconds 5
            $attempt = Start-NativeProcess $false
            continue
        }
        $script:JoinedRow = $joinOutcome
        Write-RunReceipt 'joined' $preflight $deployment
        Write-Host ("joined {0} as {1} -> {2} (pid {3})" -f
            $Client, $Character, $Server, $attempt.process.Id)

        if ([string]::IsNullOrWhiteSpace($ScenarioPath)) { break }
        $script:ScenarioTerminalRow =
            Wait-ForScenarioTerminal $RunId $WaitSeconds ([DateTimeOffset]$attempt.launched_utc)
        if ($script:ScenarioTerminalRow.state -eq 'scenario_complete') { break }

        $script:ResumeCount++
        if ($script:ResumeCount -gt 4) {
            throw 'Native cutover scenario exceeded four bounded resume cycles.'
        }
        [void](Stop-Valheim)
        # A graceful logout can briefly expose an incomplete Steam Cloud profile list.
        # Keep resume bounded, but let that list settle before the fresh launch.
        Start-Sleep -Seconds 5
        $attempt = Start-NativeProcess $false
    }

    if ($StopAfterHold) {
        if ($HoldSeconds -gt 0) { Start-Sleep -Seconds $HoldSeconds }
        [void](Stop-Valheim)
        $restore = Restore-LabSessionConfig
        Write-RunReceipt 'joined_held_and_stopped' $preflight $deployment
        Write-Host ("smoke completed and stopped -> {0}" -f $script:ActiveRunDirectory)
    } else {
        Write-Host ("evidence -> {0}" -f $script:ActiveRunDirectory)
    }
}

function Invoke-PendingRun() {
    if ([string]::IsNullOrWhiteSpace($PendingRequestPath) -or
        -not (Test-Path -LiteralPath $PendingRequestPath -PathType Leaf)) {
        throw "Pending request is missing: $PendingRequestPath"
    }
    $pending = Get-Content -LiteralPath $PendingRequestPath -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $PendingRequestPath -Force
    $invoke = @{
        Action = [string]$pending.action
        Client = [string]$pending.client
        ArtifactStage = if ([string]::IsNullOrWhiteSpace(
                [string]$pending.artifact_stage)) {
            'candidate'
        } else { [string]$pending.artifact_stage }
        Character = [string]$pending.character
        Server = [string]$pending.server
        GatewayUrl = [string]$pending.gateway_url
        EnrollmentId = [string]$pending.enrollment_id
        ClientAccessKey = [string]$pending.client_access_key
        ZdoJournalApplyThrottleMs = [int]$pending.zdo_journal_apply_throttle_ms
        RunId = [string]$pending.run_id
        ValheimRoot = [string]$pending.valheim_root
        SteamExe = [string]$pending.steam_exe
        DllPath = [string]$pending.dll_path
        ScenarioPath = [string]$pending.scenario_path
        EvidenceRoot = [string]$pending.evidence_root
        WaitSeconds = [int]$pending.wait_seconds
        HoldSeconds = [int]$pending.hold_seconds
        EnableRoutedRpcCutover = [bool]$pending.enable_routed_rpc_cutover
        EnableZdoJournalCutover = [bool]$pending.enable_zdo_journal_cutover
        EnableZdoJournalCanonicalSession =
            [bool]$pending.enable_zdo_journal_canonical_session
        EnableOwnershipLeaseCutover =
            [bool]$pending.enable_ownership_lease_cutover
        EnableWorldZoneCutover = [bool]$pending.enable_world_zone_cutover
        EnableMotionAuthorityCutover =
            [bool]$pending.enable_motion_authority_cutover
        EnableLabSession = [bool]$pending.enable_lab_session
        EnableSocketQuarantineCutover =
            [bool]$pending.enable_socket_quarantine_cutover
        EnableSteamFreeColdJoin =
            [bool]$pending.enable_steam_free_cold_join
        WorldDescriptorFault = [string]$pending.world_descriptor_fault
        ColdJoinFailureMode = [string]$pending.cold_join_failure_mode
        LaunchArguments = @($pending.launch_arguments)
    }
    & $PSCommandPath @invoke
}

function Install-InteractiveTask() {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $powerShellExe = Join-Path $PSHOME 'powershell.exe'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Action run-pending -PendingRequestPath "{1}"' -f
        $PSCommandPath, $PendingRequestPath
    $taskAction = New-ScheduledTaskAction -Execute $powerShellExe -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $taskAction `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    $task = Get-ScheduledTask -TaskName $TaskName
    [ordered]@{
        schema_version = 1
        receipt_type = 'native_valheim_task_install'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        task_name = $TaskName
        state = [string]$task.State
        principal = $identity
        logon_type = 'Interactive'
        entrypoint = $PSCommandPath
        pending_request = $PendingRequestPath
    }
}

function Queue-InteractiveSmoke() {
    if ([string]::IsNullOrWhiteSpace($Character)) { throw 'Character is required for queue-smoke.' }
    if (-not (Test-SafeToken $RunId)) { throw "RunId is not a safe token: $RunId" }
    if ($Server -notmatch '^[^\s:]+:\d{2,5}$') { throw "Server must be host:port: $Server" }
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    if ($task.State -eq 'Running') { throw "Scheduled task is already running: $TaskName" }
    if (Get-Process valheim -ErrorAction SilentlyContinue) {
        throw 'Valheim is already running; use status or stop before queueing a smoke run.'
    }
    if (Test-Path -LiteralPath $PendingRequestPath -PathType Leaf) {
        throw "A pending request already exists: $PendingRequestPath"
    }

    $preflight = Get-Preflight
    if ($preflight.result -ne 'ready') {
        throw "Native client preflight blocked: $($preflight.blockers -join ', ')"
    }
    $pending = [ordered]@{
        schema_version = 1
        artifact_stage = $ArtifactStage
        action = 'smoke'
        client = $Client
        character = $Character
        server = $Server
        gateway_url = $GatewayUrl
        enrollment_id = $EnrollmentId
        client_access_key = $ClientAccessKey
        zdo_journal_apply_throttle_ms = $ZdoJournalApplyThrottleMs
        run_id = $RunId
        valheim_root = $ValheimRoot
        steam_exe = $SteamExe
        dll_path = $DllPath
        scenario_path = $ScenarioPath
        evidence_root = $EvidenceRoot
        wait_seconds = $WaitSeconds
        hold_seconds = $HoldSeconds
        enable_routed_rpc_cutover = [bool]$EnableRoutedRpcCutover
        enable_zdo_journal_cutover = [bool]$EnableZdoJournalCutover
        enable_zdo_journal_canonical_session =
            [bool]$EnableZdoJournalCanonicalSession
        enable_ownership_lease_cutover =
            [bool]$EnableOwnershipLeaseCutover
        enable_world_zone_cutover = [bool]$EnableWorldZoneCutover
        enable_motion_authority_cutover =
            [bool]$EnableMotionAuthorityCutover
        enable_lab_session = [bool]$EnableLabSession
        enable_socket_quarantine_cutover =
            [bool]$EnableSocketQuarantineCutover
        enable_steam_free_cold_join = [bool]$EnableSteamFreeColdJoin
        world_descriptor_fault = $WorldDescriptorFault
        cold_join_failure_mode = $ColdJoinFailureMode
        launch_arguments = @($LaunchArguments)
    }
    Write-JsonAtomic $PendingRequestPath $pending
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 1
    $task = Get-ScheduledTask -TaskName $TaskName
    [ordered]@{
        schema_version = 1
        receipt_type = 'native_valheim_task_queue'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        artifact_stage = $ArtifactStage
        task_name = $TaskName
        task_state = [string]$task.State
        run_id = $RunId
        client = $Client
        server = $Server
        character = $Character
        evidence_directory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
        pending_request = $PendingRequestPath
    }
}

if ($Action -eq 'run-pending') {
    Invoke-PendingRun
    exit 0
}

if ([string]::IsNullOrWhiteSpace($RunId) -and $Action -in @('request-preview', 'start', 'smoke', 'poison-smoke', 'descriptor-smoke', 'cold-join-failure-smoke', 'queue-smoke')) {
    $RunId = New-RunId
}

switch ($Action) {
    'request-preview' {
        New-NativeAutotestRequest $false ([DateTimeOffset]::UtcNow) |
            ConvertTo-Json -Depth 8
    }
    'preflight' {
        $result = Get-Preflight
        $result | ConvertTo-Json -Depth 12
        if ($result.result -ne 'ready') { exit 3 }
    }
    'status' {
        [ordered]@{
            schema_version = 1
            generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
            client = $Client
            valheim = @(Get-Process valheim -ErrorAction SilentlyContinue |
                Select-Object Id,SessionId,StartTime,Responding)
            steam = @(Get-Process steam -ErrorAction SilentlyContinue |
                Select-Object Id,SessionId,StartTime,Responding)
            pending_autotest_request = Test-Path -LiteralPath $autotestRequestPath -PathType Leaf
            recent_autotest_rows = if ($RunId) { @(Read-AutotestRows $RunId) } else { @() }
        } | ConvertTo-Json -Depth 8
    }
    'stop' {
        $stopped = Stop-Valheim
        [ordered]@{
            schema_version = 1
            generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
            client = $Client
            result = if ($stopped) { 'stopped' } else { 'stop_failed' }
        } | ConvertTo-Json -Depth 4
        if (-not $stopped) { exit 1 }
    }
    'restore-lab-session' {
        Restore-LabSessionConfigFromRun | ConvertTo-Json -Depth 8
    }
    'install-task' {
        Install-InteractiveTask | ConvertTo-Json -Depth 6
    }
    'queue-smoke' {
        Queue-InteractiveSmoke | ConvertTo-Json -Depth 8
    }
    'task-status' {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        [ordered]@{
            schema_version = 1
            generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
            task_name = $TaskName
            state = [string]$task.State
            last_run_time = $info.LastRunTime
            last_task_result = $info.LastTaskResult
            next_run_time = $info.NextRunTime
            pending_request = Test-Path -LiteralPath $PendingRequestPath -PathType Leaf
            valheim = @(Get-Process valheim -ErrorAction SilentlyContinue |
                Select-Object Id,SessionId,StartTime,Responding)
        } | ConvertTo-Json -Depth 6
    }
    'start' {
        try { Start-NativeRun $false $false }
        catch {
            if (-not $script:ActiveRunDirectory -and $RunId) {
                $script:ActiveRunDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
            }
            [void](Stop-Valheim $false)
            try { Restore-LabSessionConfig } catch { Write-Warning $_.Exception.Message }
            Write-RunReceipt 'failed_and_stopped' $null $null $_.Exception.Message
            throw
        }
    }
    'smoke' {
        try { Start-NativeRun $true $false }
        catch {
            if (-not $script:ActiveRunDirectory -and $RunId) {
                $script:ActiveRunDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
            }
            [void](Stop-Valheim $false)
            try { Restore-LabSessionConfig } catch { Write-Warning $_.Exception.Message }
            Write-RunReceipt 'failed_and_stopped' $null $null $_.Exception.Message
            throw
        }
    }
    'poison-smoke' {
        try { Start-NativeRun $true $true }
        catch {
            if (-not $script:ActiveRunDirectory -and $RunId) {
                $script:ActiveRunDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
            }
            [void](Stop-Valheim $false)
            try { Restore-LabSessionConfig } catch { Write-Warning $_.Exception.Message }
            Write-RunReceipt 'failed_and_stopped' $null $null $_.Exception.Message
            throw
        }
    }
    'descriptor-smoke' {
        try { Start-NativeRun $true $false }
        catch {
            if (-not $script:ActiveRunDirectory -and $RunId) {
                $script:ActiveRunDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
            }
            [void](Stop-Valheim $false)
            try { Restore-LabSessionConfig } catch { Write-Warning $_.Exception.Message }
            Write-RunReceipt 'failed_and_stopped' $null $null $_.Exception.Message
            throw
        }
    }
    'cold-join-failure-smoke' {
        try { Start-NativeRun $true $false }
        catch {
            if (-not $script:ActiveRunDirectory -and $RunId) {
                $script:ActiveRunDirectory = Join-Path (Join-Path $EvidenceRoot $RunId) $Client
            }
            [void](Stop-Valheim $false)
            try { Restore-LabSessionConfig } catch { Write-Warning $_.Exception.Message }
            Write-RunReceipt 'failed_and_stopped' $null $null $_.Exception.Message
            throw
        }
    }
}
