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
    [ValidateSet('preflight', 'start', 'smoke', 'poison-smoke', 'status', 'stop', 'install-task', 'queue-smoke', 'task-status', 'run-pending')]
    [string] $Action = 'preflight',

    [ValidateSet('omen', 'i5')]
    [string] $Client = 'omen',

    [string] $Character = '',

    [string] $Server = 'comfy-p7.duckdns.org:2456',

    [string] $GatewayUrl = '',

    [string] $RunId = '',

    [string] $ValheimRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',

    [string] $SteamExe = 'C:\Program Files (x86)\Steam\steam.exe',

    [string] $DllPath = '',

    [string] $ScenarioPath = '',

    [string] $EvidenceRoot = '',

    [ValidateRange(60, 1200)]
    [int] $WaitSeconds = 600,

    [ValidateRange(0, 300)]
    [int] $HoldSeconds = 20,

    [switch] $EnableRoutedRpcCutover,

    [string[]] $LaunchArguments = @(),

    [string] $PendingRequestPath = '',

    [string] $TaskName = 'BaselineNativeValheimClient'
)

$ErrorActionPreference = 'Stop'
$script:ActiveRunDirectory = $null
$script:LastReceipt = $null
$script:JoinedRow = $null
$script:PoisonRow = $null
$script:ScenarioTerminalRow = $null
$script:ResumeCount = 0
$script:ProfileRecoveryCount = 0

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
$bepInExLogPath = Join-Path $ValheimRoot 'BepInEx\LogOutput.log'
$playerLogPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim\Player.log'

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
        result = $Result
        error = $Error
        server = $Server
        character = $Character
        process_id = if ($process) { $process.Id } else { $null }
        process_session = if ($process) { $process.SessionId } else { $null }
        joined = $script:JoinedRow
        native_poison = $script:PoisonRow
        scenario_terminal = $script:ScenarioTerminalRow
        resume_count = $script:ResumeCount
        profile_recovery_count = $script:ProfileRecoveryCount
        native_network_poison_requested = $Action -eq 'poison-smoke'
        routed_rpc_cutover_requested = [bool]$EnableRoutedRpcCutover
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
        }
    }
    $path = Join-Path $script:ActiveRunDirectory 'lifecycle.json'
    Write-JsonAtomic $path $receipt
    $script:LastReceipt = $receipt
}

function Write-NativeAutotestRequest([bool] $ExpectPoison) {
    $now = [DateTimeOffset]::UtcNow
    $request = [ordered]@{
        schema_version = 1
        run_id = $RunId
        client = $Client
        character = $Character
        server = $Server
        lumberjacks_gateway_url = $GatewayUrl
        created_utc = $now.ToString('o')
        expires_utc = $now.AddMinutes(15).ToString('o')
        native_network_poison = $ExpectPoison
        routed_rpc_cutover = [bool]$EnableRoutedRpcCutover
    }
    Write-JsonAtomic $autotestRequestPath $request
    return $now
}

function Start-NativeProcess([bool] $ExpectPoison) {
    $launchedUtc = Write-NativeAutotestRequest $ExpectPoison
    $arguments = @('-applaunch', '892970', '-console') + @($LaunchArguments) + @('+connect', $Server)
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
    $attempt = Start-NativeProcess $ExpectPoison
    if ($ExpectPoison) {
        $script:PoisonRow = Wait-ForPoison $RunId $WaitSeconds
        [void](Stop-Valheim)
        Write-RunReceipt 'native_poison_blocked_and_stopped' $preflight $deployment
        Write-Host ("native poison blocked {0} -> {1}" -f $script:PoisonRow.funnel, $script:ActiveRunDirectory)
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
        Character = [string]$pending.character
        Server = [string]$pending.server
        GatewayUrl = [string]$pending.gateway_url
        RunId = [string]$pending.run_id
        ValheimRoot = [string]$pending.valheim_root
        SteamExe = [string]$pending.steam_exe
        DllPath = [string]$pending.dll_path
        ScenarioPath = [string]$pending.scenario_path
        EvidenceRoot = [string]$pending.evidence_root
        WaitSeconds = [int]$pending.wait_seconds
        HoldSeconds = [int]$pending.hold_seconds
        EnableRoutedRpcCutover = [bool]$pending.enable_routed_rpc_cutover
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
        action = 'smoke'
        client = $Client
        character = $Character
        server = $Server
        gateway_url = $GatewayUrl
        run_id = $RunId
        valheim_root = $ValheimRoot
        steam_exe = $SteamExe
        dll_path = $DllPath
        scenario_path = $ScenarioPath
        evidence_root = $EvidenceRoot
        wait_seconds = $WaitSeconds
        hold_seconds = $HoldSeconds
        enable_routed_rpc_cutover = [bool]$EnableRoutedRpcCutover
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

if ([string]::IsNullOrWhiteSpace($RunId) -and $Action -in @('start', 'smoke', 'poison-smoke', 'queue-smoke')) {
    $RunId = New-RunId
}

switch ($Action) {
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
            Write-RunReceipt 'failed_and_stopped' $null $null $_.Exception.Message
            throw
        }
    }
}
