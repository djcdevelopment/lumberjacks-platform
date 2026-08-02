#Requires -Version 5.1
<#
.SYNOPSIS
Run one two-client native-cutover manifest on OMEN and i5 without an operator in the game loop.

.DESCRIPTION
The i5 work enters its existing interactive scheduled task; OMEN runs in the current interactive
session. Both clients receive the same fixed, allow-listed manifest, self-join, execute their own
actions, perform any requested bounded relaunch, retain evidence, and stop.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunId,

    [Parameter(Mandatory)]
    [string] $ScenarioPath,

    [string] $Server = '100.116.82.60:2456',

    [string] $OmenGatewayUrl = 'http://127.0.0.1:4000',

    [string] $I5GatewayUrl = 'http://127.0.0.1:4400',

    [ValidateRange(1024, 65535)]
    [int] $I5GatewayTunnelPort = 4400,

    [string] $OmenCharacter = 'Tugcorp',

    [string] $I5Character = 'durracktu',

    [string] $DllPath = '',

    [string] $EvidenceRoot = '',

    [ValidateRange(60, 1800)]
    [int] $WaitSeconds = 900,

    [ValidateRange(0, 120)]
    [int] $HoldSeconds = 5,

    [switch] $EnableDirectControlCutover,

    [switch] $EnableRoutedRpcCutover,

    [switch] $EnableZdoJournalCutover,

    [switch] $EnableZdoJournalCanonicalSession,

    [switch] $EnableOwnershipLeaseCutover,

    [switch] $EnableWorldZoneCutover,

    [switch] $EnablePortalTraversal,

    [switch] $EnableMotionAuthorityCutover,

    [switch] $EnableSocketQuarantineCutover,

    [switch] $EnableSteamFreeColdJoin,

    [switch] $EnableGatewayJournalRestartProof,

    [switch] $EnableServerNativePoison,

    # Enter the retained C2-C6 Steam-free state and arm native poison on all
    # three participants without requiring C8's 49-action profile/reducer.
    [switch] $EnableNativeZeroComposition,

    [switch] $EnableC8Composition,

    [switch] $SkipGatewayBuild,

    # Physical acceptance runs the exact paired image built by
    # New-ReleaseCut, never a second compose-local `dev` build. When omitted,
    # the tag is derived from the mod DLL's baked release metadata.
    [string] $GatewayImage = '',

    [string] $ServerGatewayUrl = 'http://100.124.12.37:4000',

    [string] $ServerContainer = 'comfy-valheim-server-am4-valheim-server-1',

    [string] $ServerWorldDb =
        '/home/derek/comfy-valheim-lab/server-state/config/worlds_local/ComfyEra16.db',

    [string] $ServerWorldFwl =
        '/home/derek/comfy-valheim-lab/server-state/config/worlds_local/ComfyEra16.fwl'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$clientHarness = Join-Path $PSScriptRoot 'Invoke-NativeValheimClient.ps1'
$i5Tools = Join-Path $repoRoot 'tools\i5'

if ($EnableNativeZeroComposition -or $EnableC8Composition) {
    $EnableDirectControlCutover = $true
    $EnableRoutedRpcCutover = $true
    $EnableZdoJournalCutover = $true
    $EnableZdoJournalCanonicalSession = $true
    $EnableOwnershipLeaseCutover = $true
    $EnableWorldZoneCutover = $true
    $EnableMotionAuthorityCutover = $true
    $EnableSteamFreeColdJoin = $true
    $EnableServerNativePoison = $true
}
if ($EnableC8Composition) {
    $EnablePortalTraversal = $true
    $EnableGatewayJournalRestartProof = $true
}

if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $repoRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repoRoot 'fieldlab\runs\native-valheim'
}
if ($RunId.Length -gt 80 -or $RunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw "RunId must be an 80-character-or-shorter safe token: $RunId"
}
if ($Server -notmatch '^[^\s:]+:\d{2,5}$') {
    throw "Server must be host:port: $Server"
}
if ($ServerContainer -notmatch '^[A-Za-z0-9_.-]+$') {
    throw "ServerContainer must be a safe Docker container name: $ServerContainer"
}
foreach ($worldPath in @($ServerWorldDb, $ServerWorldFwl)) {
    if ($worldPath -notmatch '^/[A-Za-z0-9._/-]+$') {
        throw "Server world paths must be safe absolute POSIX paths: $worldPath"
    }
}
if ($EnableZdoJournalCanonicalSession -and -not $EnableZdoJournalCutover) {
    throw '-EnableZdoJournalCanonicalSession requires -EnableZdoJournalCutover.'
}
if ($EnableOwnershipLeaseCutover -and
    (-not $EnableZdoJournalCutover -or -not $EnableZdoJournalCanonicalSession)) {
    throw '-EnableOwnershipLeaseCutover requires canonical ZDO journal cutover.'
}
if ($EnableWorldZoneCutover -and
    (-not $EnableZdoJournalCutover -or -not $EnableZdoJournalCanonicalSession)) {
    throw '-EnableWorldZoneCutover requires canonical ZDO journal cutover.'
}
if ($EnableGatewayJournalRestartProof -and -not $EnableZdoJournalCutover) {
    throw '-EnableGatewayJournalRestartProof requires ZDO journal cutover.'
}
if ($EnableSteamFreeColdJoin -and
    (-not $EnableDirectControlCutover -or
     -not $EnableRoutedRpcCutover -or
     -not $EnableZdoJournalCutover -or
     -not $EnableZdoJournalCanonicalSession -or
     -not $EnableOwnershipLeaseCutover -or
     -not $EnableWorldZoneCutover -or
     -not $EnableMotionAuthorityCutover)) {
    throw '-EnableSteamFreeColdJoin requires every accepted C2a-C6 cutover gate.'
}
if ($EnableSteamFreeColdJoin -and $EnableSocketQuarantineCutover) {
    throw 'Steam-free cold join and the earlier native-socket quarantine falsifier are mutually exclusive.'
}

$scenario = (Resolve-Path -LiteralPath $ScenarioPath -ErrorAction Stop).Path
$dll = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
$releaseIdentityLibrary = Join-Path $repoRoot `
    'infra\gcp\p7\scripts\lib\ReleaseIdentity.ps1'
. $releaseIdentityLibrary
$modRelease = Get-AssemblyMetadataValue `
    -DllPath $dll `
    -Key 'LumberjacksModReleaseId'
if ([string]::IsNullOrWhiteSpace($modRelease) -or
    $modRelease -notmatch '^m\d+-[a-z0-9]+-\d{8}-r\d+$') {
    throw "The physical cutover DLL is not a cut release: '$modRelease'."
}
if ([string]::IsNullOrWhiteSpace($GatewayImage)) {
    $GatewayImage = "lumberjacks-gateway:$modRelease"
}
if ($GatewayImage -notmatch '^[A-Za-z0-9._/-]+:[A-Za-z0-9._-]+$') {
    throw "GatewayImage must be a tagged local image reference: $GatewayImage"
}
$scenarioDocument =
    Get-Content -LiteralPath $scenario -Raw -Encoding utf8 |
    ConvertFrom-Json
if ($EnableC8Composition -and $scenarioDocument.profile -ne 'c8') {
    throw '-EnableC8Composition requires a profile=c8 scenario manifest.'
}
$scenarioName = Split-Path -Leaf $scenario
$remoteScenarioDirectory = 'C:/deploy/baseline/fieldlab/scenarios'
$remoteScenarioPath = "$remoteScenarioDirectory/$scenarioName"
$remoteEvidenceRoot = 'C:/deploy/baseline/fieldlab/runs/native-valheim'
$runDirectory = Join-Path $EvidenceRoot $RunId
$completed = $false
$gatewayTunnel = $null
$serverDirectArmed = $false
$serverControlReceipts = @()
$serverDisarmError = $null
$serverRoutedArmed = $false
$serverGatewayChanged = $false
$oldServerGatewayUrl = $null
$serverRoutedReceipts = @()
$serverRoutedDisarmError = $null
$serverJournalArmed = $false
$serverJournalCanonicalArmed = $false
$serverJournalReceipts = @()
$serverJournalDisarmError = $null
$serverOwnershipArmed = $false
$serverOwnershipReceipts = @()
$serverOwnershipDisarmError = $null
$serverWorldZoneArmed = $false
$serverWorldZoneReceipts = @()
$serverWorldZoneDisarmError = $null
$serverPortalTraversalChanged = $false
$serverPortalTraversalPrevious = $null
$serverPortalTraversalReceipts = @()
$serverPortalTraversalRestoreError = $null
$serverMotionArmed = $false
$serverMotionReceipts = @()
$serverMotionDisarmError = $null
$serverLogicalPeerArmed = $false
$serverLogicalPeerReceipts = @()
$serverLogicalPeerDisarmError = $null
$serverPoisonArmed = $false
$serverPoisonReceipts = @()
$serverPoisonDisarmError = $null
$residueCleanupReceipt = $null
$residueCleanupError = $null
$vehicleSummaryError = $null
$mountSummaryError = $null
$gatewayRestartReceipt = $null
$gatewayImageReceipt = $null
$saveIntegrityBefore = $null
$saveIntegrityAfter = $null
$i5Queued = $false
$omenHarnessProcess = $null
$useRoutedRpc =
    [bool]$EnableRoutedRpcCutover -or [bool]$EnableZdoJournalCutover
$useConcurrentHarness =
    [bool]$EnableZdoJournalCutover -or
    [bool]$EnableMotionAuthorityCutover -or
    [bool]$EnableSocketQuarantineCutover -or
    [bool]$EnableSteamFreeColdJoin

function Write-JsonAtomic([string] $Path, [object] $Value) {
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllText(
        $temporary,
        ($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Copy-ServerFailureEvidenceBestEffort {
    $serverDirectory = Join-Path $runDirectory 'server'
    New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
    $remoteRoot =
        '/home/derek/comfy-valheim-lab/server-state/config/bepinex/' +
        'comfy-network-sense'
    foreach ($name in @(
            'ship-cutover.jsonl',
            'saddle-cutover.jsonl',
            'routed-rpc-cutover.jsonl',
            'logical-peer-cutover.jsonl',
            'native-network-use.jsonl',
            'lumberjacks-game-session.jsonl')) {
        try {
            & scp -q "am4:$remoteRoot/$name" (Join-Path $serverDirectory $name)
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Best-effort AM4 evidence copy failed for $name."
            }
        } catch {
            Write-Warning ("Best-effort AM4 evidence copy failed for ${name}: " +
                $_.Exception.Message)
        }
    }

    try {
        $container = if ($gatewayImageReceipt -and
            $gatewayImageReceipt.running_container) {
            [string]$gatewayImageReceipt.running_container
        } else { 'lumberjacks-local-gateway-1' }
        $gatewayLines = @(
            docker logs --since 2h $container 2>&1 |
                Select-String -SimpleMatch $RunId |
                ForEach-Object Line)
        [IO.File]::WriteAllLines(
            (Join-Path $runDirectory 'gateway-run.log'),
            $gatewayLines,
            [Text.UTF8Encoding]::new($false))
    } catch {
        Write-Warning ("Best-effort Gateway evidence copy failed: " +
            $_.Exception.Message)
    }
}

function Get-Am4SaveFingerprint {
    $logCommand =
        "docker logs --since 48h '$ServerContainer' 2>&1 | " +
        "grep -E 'ZDOS:|ConnectPortals =>|Portal saved-connection hash join|spawned=|Loaded [0-9]+ locations'"
    $logLines = & ssh -o BatchMode=yes -o ConnectTimeout=10 am4 $logCommand
    if ($LASTEXITCODE -notin @(0, 1)) {
        throw "AM4 save fingerprint log query failed with exit $LASTEXITCODE."
    }
    $logText = @($logLines) -join [Environment]::NewLine

    function Last-Integer([string] $Text, [string] $Pattern) {
        $matches = [regex]::Matches($Text, $Pattern)
        if ($matches.Count -eq 0) { return $null }
        return [long]$matches[$matches.Count - 1].Groups[1].Value
    }

    $statCommand =
        "stat -c '%n|%s|%Y' '$ServerWorldDb' '$ServerWorldFwl'"
    $statLines = & ssh -o BatchMode=yes -o ConnectTimeout=10 am4 $statCommand
    if ($LASTEXITCODE -ne 0) {
        throw "AM4 save fingerprint stat query failed with exit $LASTEXITCODE."
    }
    $files = [ordered]@{}
    foreach ($line in @($statLines)) {
        $parts = [string]$line -split '\|'
        if ($parts.Count -ne 3) {
            throw "AM4 save fingerprint returned an invalid stat row: $line"
        }
        $files[[IO.Path]::GetFileName($parts[0])] = [ordered]@{
            bytes = [long]$parts[1]
            mtime_epoch = [long]$parts[2]
        }
    }

    $fingerprint = [ordered]@{
        schema_version = 1
        receipt_type = 'am4_world_save_fingerprint'
        captured_utc = [DateTimeOffset]::UtcNow.ToString('o')
        container = $ServerContainer
        zdos = Last-Integer $logText 'ZDOS:(\d+)'
        # Newer server builds replace Valheim's quadratic ConnectPortals scan with the
        # mod's saved-connection hash join. Both lines report the number of established
        # portal pairs; accept either spelling so a successful optimization does not make
        # the safety fingerprint unreadable.
        portals = Last-Integer $logText `
            '(?:ConnectPortals => Connected |Portal saved-connection hash join connected=)(\d+)'
        spawned = Last-Integer $logText 'spawned=(\d+)'
        targets = Last-Integer $logText 'targets=(\d+)'
        locations = Last-Integer $logText 'Loaded (\d+) locations'
        world_files = $files
    }
    foreach ($field in @('zdos', 'portals', 'spawned', 'targets', 'locations')) {
        if ($null -eq $fingerprint[$field]) {
            throw "AM4 save fingerprint is missing $field from the current load block."
        }
    }
    foreach ($name in @(
            [IO.Path]::GetFileName($ServerWorldDb),
            [IO.Path]::GetFileName($ServerWorldFwl))) {
        if (-not $files.Contains($name) -or [long]$files[$name].bytes -le 0) {
            throw "AM4 save fingerprint is missing a non-empty $name."
        }
    }
    return $fingerprint
}

function Compare-Am4SaveFingerprint([object] $Before, [object] $After) {
    $checks = [ordered]@{}
    foreach ($field in @('portals', 'spawned', 'targets', 'locations')) {
        $checks["${field}_exact"] =
            [long]$Before.$field -eq [long]$After.$field
    }
    $zdoFloor = [long][Math]::Floor([long]$Before.zdos * 0.99)
    $checks.zdos_no_material_drop = [long]$After.zdos -ge $zdoFloor
    $checks.world_files_present =
        $After.world_files.Count -eq 2 -and
        @($After.world_files.Values |
            Where-Object { [long]$_.bytes -gt 0 }).Count -eq 2
    $failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
    return [ordered]@{
        schema_version = 1
        receipt_type = 'c8_save_integrity'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        run_id = $RunId
        result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
        before = $Before
        after = $After
        checks = $checks
        failed_checks = @($failed | ForEach-Object Key)
    }
}

function Invoke-I5Harness([string[]] $Arguments) {
    $output = & ssh -o BatchMode=yes i5 `
        powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File C:\deploy\baseline\fieldlab\scripts\Invoke-NativeValheimClient.ps1 `
        @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "i5 native-client command failed with exit $LASTEXITCODE."
    }
    return @($output)
}

function Get-OmenHarnessState {
    if (-not $omenHarnessProcess) {
        return [pscustomobject]@{
            terminal = $false
            success = $false
            detail = 'not_started'
        }
    }
    $omenHarnessProcess.Refresh()
    if (-not $omenHarnessProcess.HasExited) {
        return [pscustomobject]@{
            terminal = $false
            success = $false
            detail = 'running'
        }
    }

    $omenHarnessProcess.WaitForExit()
    $omenHarnessProcess.Refresh()
    $lifecyclePath = Join-Path $runDirectory 'omen\lifecycle.json'
    if (Test-Path -LiteralPath $lifecyclePath -PathType Leaf) {
        $lifecycle =
            Get-Content -LiteralPath $lifecyclePath -Raw -Encoding utf8 |
            ConvertFrom-Json
        $success =
            $lifecycle.run_id -eq $RunId -and
            $lifecycle.result -eq 'joined_held_and_stopped' -and
            $lifecycle.scenario_terminal.state -eq 'scenario_complete' -and
            [string]::IsNullOrWhiteSpace([string]$lifecycle.error)
        return [pscustomobject]@{
            terminal = $true
            success = $success
            detail = if ($success) {
                'scenario_complete'
            } elseif (-not [string]::IsNullOrWhiteSpace(
                    [string]$lifecycle.error)) {
                [string]$lifecycle.error
            } else {
                "lifecycle_result=$($lifecycle.result)"
            }
        }
    }

    return [pscustomobject]@{
        terminal = $true
        success = $false
        detail = "no lifecycle receipt; exit=$($omenHarnessProcess.ExitCode)"
    }
}

function Get-I5HarnessState {
    $statusText = Invoke-I5Harness @(
        '-Action', 'task-status',
        '-Client', 'i5')
    $status =
        ($statusText -join [Environment]::NewLine) |
        ConvertFrom-Json
    if ($status.state -ne 'Ready' -or [bool]$status.pending_request) {
        return [pscustomobject]@{
            terminal = $false
            success = $false
            detail = "state=$($status.state) pending=$($status.pending_request)"
            raw = $status
        }
    }
    return [pscustomobject]@{
        terminal = $true
        success = [int]$status.last_task_result -eq 0
        detail = "last_task_result=$($status.last_task_result)"
        raw = $status
    }
}

function Wait-GatewayCutoverReady {
    param([ValidateRange(1, 60)][int] $Seconds = 15)

    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    do {
        try {
            $last = Invoke-RestMethod `
                -Method Get `
                -Uri "$OmenGatewayUrl/live/valheim-cutover" `
                -TimeoutSec 2
        } catch {
            $last = $null
        }
        if ($last -and
            [bool]$last.ready -and
            [bool]$last.canonical_server_connected -and
            [bool]$last.descriptor_published -and
            [string]$last.descriptor_run_id -eq $RunId) {
            return $last
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    $detail = if ($last) {
        "ready=$($last.ready) server=$($last.canonical_server_connected) " +
        "descriptor=$($last.descriptor_published) " +
        "descriptor_run_id=$($last.descriptor_run_id)"
    } else {
        'readiness_endpoint_unavailable'
    }
    throw "Gateway cutover prerequisites failed within ${Seconds}s: $detail. No client was launched."
}

try {
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    if ($scenarioDocument.profile -eq 'c6') {
        $coveragePath = Join-Path $runDirectory 'c6-scenario-coverage.json'
        $coverageOutput =
            & (Join-Path $PSScriptRoot 'Test-C6ScenarioCoverage.ps1') `
                -ScenarioPath $scenario `
                -RunId $RunId `
                -OutputPath $coveragePath
        $coverageReceipt =
            Get-Content -LiteralPath $coveragePath -Raw -Encoding utf8 |
            ConvertFrom-Json
        if ($coverageReceipt.result -ne 'passed') {
            throw 'C6 scenario coverage/alignment is incomplete; no remote state was changed.'
        }
        $coverageOutput | Write-Host
    }
    if ($scenarioDocument.profile -eq 'c10a-vehicle') {
        $coveragePath = Join-Path $runDirectory 'c10a-vehicle-scenario-coverage.json'
        $coverageOutput =
            & (Join-Path $PSScriptRoot 'Test-C10aVehicleScenarioCoverage.ps1') `
                -ScenarioPath $scenario `
                -RunId $RunId `
                -OutputPath $coveragePath
        $coverageReceipt =
            Get-Content -LiteralPath $coveragePath -Raw -Encoding utf8 |
            ConvertFrom-Json
        if ($coverageReceipt.result -ne 'passed') {
            throw 'C10a vehicle release/transfer choreography is incomplete; no remote state was changed.'
        }
        $coverageOutput | Write-Host
    }
    if ($scenarioDocument.profile -eq 'c10a-mount') {
        $coveragePath = Join-Path $runDirectory 'c10a-mount-scenario-coverage.json'
        $coverageOutput =
            & (Join-Path $PSScriptRoot 'Test-C10aMountScenarioCoverage.ps1') `
                -ScenarioPath $scenario `
                -RunId $RunId `
                -OutputPath $coveragePath
        $coverageReceipt =
            Get-Content -LiteralPath $coveragePath -Raw -Encoding utf8 |
            ConvertFrom-Json
        if ($coverageReceipt.result -ne 'passed') {
            throw 'C10a mount rider/reclaim choreography is incomplete; no remote state was changed.'
        }
        $coverageOutput | Write-Host
    }
    $gatewayVerifier = Join-Path $repoRoot `
        'infra\gcp\p7\scripts\Test-GatewayImageRelease.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $gatewayVerifier `
        -Image $GatewayImage `
        -ExpectedRelease $modRelease
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway image '$GatewayImage' does not admit the exact mod release '$modRelease'; no remote state was changed."
    }
    $gatewayExpectedImageId = [string](
        & docker image inspect --format '{{.Id}}' $GatewayImage)
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($gatewayExpectedImageId)) {
        throw "Gateway image '$GatewayImage' is unavailable after release verification."
    }
    if ($EnableC8Composition) {
        $retainedScenario = Join-Path $runDirectory 'scenario.json'
        if (-not [IO.Path]::GetFullPath($scenario).Equals(
                [IO.Path]::GetFullPath($retainedScenario),
                [StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $scenario -Destination $retainedScenario -Force
            $scenario = $retainedScenario
            $scenarioName = 'scenario.json'
            $remoteScenarioPath = "$remoteScenarioDirectory/$scenarioName"
        }
        $coveragePath = Join-Path $runDirectory 'c8-scenario-coverage.json'
        $coverageOutput =
            & (Join-Path $PSScriptRoot 'Test-C8ScenarioCoverage.ps1') `
                -ScenarioPath $scenario `
                -RunId $RunId `
                -OutputPath $coveragePath
        $coverageReceipt =
            Get-Content -LiteralPath $coveragePath -Raw -Encoding utf8 |
            ConvertFrom-Json
        if ($coverageReceipt.result -ne 'passed') {
            throw 'C8 scenario coverage is incomplete; no remote state was changed.'
        }
        $coverageOutput | Write-Host
        $saveIntegrityBefore = Get-Am4SaveFingerprint
        Write-JsonAtomic `
            (Join-Path $runDirectory 'save-integrity-before.json') `
            $saveIntegrityBefore
    }
    & (Join-Path $i5Tools 'Test-I5Link.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'The i5 lane is offline or failed preflight; no retry was attempted.'
    }

    # RunIds are single-use: runtime-control request ids derive from them and
    # the server mod refuses replays (duplicate_request_id) from a durable
    # dedup set. Refuse reuse before any remote state is armed.
    # Dot-wildcards stand in for the JSON quote characters: PowerShell 5.1
    # does not escape embedded double quotes when building a native command
    # line, so the remote pattern must not contain any.
    $runIdPattern = 'request_id.:.' + $RunId + '-'
    $usedRunIdCount = & ssh -o BatchMode=yes am4 (
        "grep -c '" + $runIdPattern + "' " +
        "'/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/runtime-control-receipts.jsonl' 2>/dev/null")
    if ($LASTEXITCODE -eq 0) {
        throw ("RunId '$RunId' already has $usedRunIdCount runtime-control receipts " +
            'on the server; RunIds are single-use - generate a fresh one.')
    }
    if ($LASTEXITCODE -ne 1) {
        throw "RunId preflight could not read the server runtime-control receipts (ssh exit $LASTEXITCODE)."
    }

    $gatewayTunnel = Start-Process `
        -FilePath 'ssh.exe' `
        -ArgumentList @(
            '-N',
            '-o', 'BatchMode=yes',
            '-o', 'ExitOnForwardFailure=yes',
            '-o', 'ServerAliveInterval=15',
            '-R', "127.0.0.1:${I5GatewayTunnelPort}:127.0.0.1:4000",
            'i5') `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Seconds 1
    if ($gatewayTunnel.HasExited) {
        throw "The bounded i5 Gateway reverse tunnel failed with exit $($gatewayTunnel.ExitCode)."
    }

    if ($EnableDirectControlCutover) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $runOutput = & $serverControl `
            -Setting nativeNetworkEvidenceRunId `
            -Value $RunId `
            -RequestId "$RunId-direct-run"
        if ($LASTEXITCODE -ne 0) { throw 'Server run-id control failed.' }
        $serverControlReceipts += (($runOutput -join [Environment]::NewLine) | ConvertFrom-Json)

        $armOutput = & $serverControl `
            -Setting directControlCutoverEnabled `
            -Value true `
            -RequestId "$RunId-direct-arm"
        if ($LASTEXITCODE -ne 0) { throw 'Server direct-control arm failed.' }
        $serverControlReceipts += (($armOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        $serverDirectArmed = $true
    }

    if ($useRoutedRpc) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $runOutput = & $serverControl `
            -Setting nativeNetworkEvidenceRunId `
            -Value $RunId `
            -RequestId "$RunId-routed-run"
        if ($LASTEXITCODE -ne 0) { throw 'Server routed run-id control failed.' }
        $serverRoutedReceipts +=
            (($runOutput -join [Environment]::NewLine) | ConvertFrom-Json)

        $gatewayOutput = & $serverControl `
            -Setting lumberjacksGatewayUrl `
            -Value $ServerGatewayUrl `
            -RequestId "$RunId-routed-gateway"
        if ($LASTEXITCODE -ne 0) { throw 'Server Gateway URL control failed.' }
        $gatewayReceipt =
            (($gatewayOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        $serverRoutedReceipts += $gatewayReceipt
        $oldServerGatewayUrl = [string]$gatewayReceipt.old_value
        $serverGatewayChanged = $true

        $armOutput = & $serverControl `
            -Setting routedRpcCutoverEnabled `
            -Value true `
            -RequestId "$RunId-routed-arm"
        if ($LASTEXITCODE -ne 0) { throw 'Server routed-RPC arm failed.' }
        $serverRoutedReceipts +=
            (($armOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        $serverRoutedArmed = $true
        Start-Sleep -Seconds 3
    }

    if ($EnableZdoJournalCutover) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $journalArmOutput = & $serverControl `
            -Setting zdoJournalCutoverEnabled `
            -Value true `
            -RequestId "$RunId-journal-arm"
        if ($LASTEXITCODE -ne 0) { throw 'Server ZDO-journal arm failed.' }
        $serverJournalReceipts +=
            (($journalArmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        $serverJournalArmed = $true
        if ($EnableZdoJournalCanonicalSession) {
            $canonicalArmOutput = & $serverControl `
                -Setting zdoJournalCanonicalSessionEnabled `
                -Value true `
                -RequestId "$RunId-journal-canonical-arm"
            if ($LASTEXITCODE -ne 0) {
                throw 'Server canonical ZDO-journal arm failed.'
            }
            $serverJournalReceipts +=
                (($canonicalArmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
            $serverJournalCanonicalArmed = $true
        }
    }

    if ($EnableOwnershipLeaseCutover) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $ownershipArmOutput = & $serverControl `
            -Setting ownershipLeaseCutoverEnabled `
            -Value true `
            -RequestId "$RunId-ownership-arm"
        if ($LASTEXITCODE -ne 0) { throw 'Server ownership-lease arm failed.' }
        $serverOwnershipReceipts +=
            (($ownershipArmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        $serverOwnershipArmed = $true
    }

    if ($EnableWorldZoneCutover) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $worldZoneArmOutput = & $serverControl `
            -Setting worldZoneCutoverEnabled `
            -Value true `
            -RequestId "$RunId-world-zone-arm"
        if ($LASTEXITCODE -ne 0) { throw 'Server world/zone cutover arm failed.' }
        $serverWorldZoneReceipts +=
            (($worldZoneArmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        $serverWorldZoneArmed = $true
    }

    if ($EnablePortalTraversal) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $portalTraversalOutput = & $serverControl `
            -Setting portalTraversalEnabled `
            -Value true `
            -RequestId "$RunId-portal-traversal-arm"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server portal-traversal arm failed.'
        }
        $portalTraversalReceipt =
            (($portalTraversalOutput -join [Environment]::NewLine) |
                ConvertFrom-Json)
        $serverPortalTraversalReceipts += $portalTraversalReceipt
        $serverPortalTraversalPrevious =
            [bool]::Parse([string]$portalTraversalReceipt.old_value)
        $serverPortalTraversalChanged =
            -not $serverPortalTraversalPrevious
    }

    if ($EnableMotionAuthorityCutover) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $motionArmOutput = & $serverControl `
            -Setting motionAuthorityCutoverEnabled `
            -Value true `
            -RequestId "$RunId-motion-arm"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server motion-authority cutover arm failed.'
        }
        $serverMotionReceipts +=
            (($motionArmOutput -join [Environment]::NewLine) |
                ConvertFrom-Json)
        $serverMotionArmed = $true
    }

    if ($EnableSteamFreeColdJoin) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $logicalPeerArmOutput = & $serverControl `
            -Setting logicalPeerCutoverEnabled `
            -Value true `
            -RequestId "$RunId-logical-peer-arm"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server logical-peer cutover arm failed.'
        }
        $serverLogicalPeerReceipts +=
            (($logicalPeerArmOutput -join [Environment]::NewLine) |
                ConvertFrom-Json)
        $serverLogicalPeerArmed = $true
    }

    if ($EnableServerNativePoison) {
        $serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
        $poisonArmOutput = & $serverControl `
            -Setting nativeNetworkPoisonEnabled `
            -Value true `
            -RequestId "$RunId-native-poison-arm"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server native-network poison arm failed.'
        }
        $serverPoisonReceipts +=
            (($poisonArmOutput -join [Environment]::NewLine) |
                ConvertFrom-Json)
        $serverPoisonArmed = $true
    }

    & (Join-Path $i5Tools 'Deploy-ToI5.ps1') `
        -Path $clientHarness `
        -Dest C:/deploy/baseline/fieldlab/scripts
    if ($LASTEXITCODE -ne 0) { throw 'i5 harness deployment failed.' }
    & (Join-Path $i5Tools 'Deploy-ToI5.ps1') -Path $dll -ValheimPlugins
    if ($LASTEXITCODE -ne 0) { throw 'i5 mod deployment failed.' }
    & (Join-Path $i5Tools 'Deploy-ToI5.ps1') `
        -Path $scenario `
        -Dest $remoteScenarioDirectory
    if ($LASTEXITCODE -ne 0) { throw 'i5 scenario deployment failed.' }

    $i5Arguments = @(
        '-Action', 'queue-smoke',
        '-Client', 'i5',
        '-Character', $I5Character,
        '-Server', $Server,
        '-GatewayUrl', $I5GatewayUrl,
        '-RunId', $RunId,
        '-ScenarioPath', $remoteScenarioPath,
        '-HoldSeconds', [string]$HoldSeconds,
        '-WaitSeconds', [string]$WaitSeconds)
    if ($useRoutedRpc) {
        $i5Arguments += '-EnableRoutedRpcCutover'
    }
    if ($EnableZdoJournalCutover) {
        $i5Arguments += '-EnableZdoJournalCutover'
        if ($EnableZdoJournalCanonicalSession) {
            $i5Arguments += '-EnableZdoJournalCanonicalSession'
        }
        if ($EnableOwnershipLeaseCutover) {
            $i5Arguments += '-EnableOwnershipLeaseCutover'
        }
        if ($EnableWorldZoneCutover) {
            $i5Arguments += '-EnableWorldZoneCutover'
        }
    }
    if ($EnableMotionAuthorityCutover) {
        $i5Arguments += '-EnableMotionAuthorityCutover'
        $i5Arguments += '-EnableLabSession'
    }
    if ($EnableSocketQuarantineCutover) {
        $i5Arguments += '-EnableSocketQuarantineCutover'
    }
    if ($EnableSteamFreeColdJoin) {
        $i5Arguments += '-EnableSteamFreeColdJoin'
    }

    if ($useConcurrentHarness) {
        $gatewayCompose = Join-Path $repoRoot 'Lumberjacks\infra\docker'
        $previousGatewayImage = $env:LUMBERJACKS_GATEWAY_IMAGE
        Push-Location $gatewayCompose
        try {
            $env:LUMBERJACKS_GATEWAY_IMAGE = $GatewayImage
            & docker compose -p lumberjacks-local up -d --no-deps --no-build gateway
            if ($LASTEXITCODE -ne 0) {
                throw 'Exact paired Gateway deployment for the canonical-session slice failed.'
            }
        } finally {
            if ($null -eq $previousGatewayImage) {
                Remove-Item Env:LUMBERJACKS_GATEWAY_IMAGE -ErrorAction SilentlyContinue
            } else {
                $env:LUMBERJACKS_GATEWAY_IMAGE = $previousGatewayImage
            }
            Pop-Location
        }
        $gatewayContainerImageId = [string](
            & docker inspect --format '{{.Image}}' lumberjacks-local-gateway-1)
        if ($LASTEXITCODE -ne 0 -or
            $gatewayContainerImageId.Trim() -ne $gatewayExpectedImageId.Trim()) {
            throw "Running Gateway bytes do not match '$GatewayImage': expected=$gatewayExpectedImageId actual=$gatewayContainerImageId"
        }
        $gatewayImageReceipt = [ordered]@{
            schema_version = 1
            receipt_type = 'native_cutover_gateway_image_provenance'
            generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
            run_id = $RunId
            mod_release = $modRelease
            requested_image = $GatewayImage
            expected_image_id = $gatewayExpectedImageId.Trim()
            running_container = 'lumberjacks-local-gateway-1'
            running_image_id = $gatewayContainerImageId.Trim()
            exact_image_match = $true
            result = 'passed'
        }
        Write-JsonAtomic `
            (Join-Path $runDirectory 'gateway-image-provenance.json') `
            $gatewayImageReceipt
        if ($EnableWorldZoneCutover) {
            $gatewayReadiness = Wait-GatewayCutoverReady -Seconds 15
            Write-JsonAtomic `
                (Join-Path $runDirectory 'gateway-cutover-readiness.json') `
                $gatewayReadiness
        }

        $omenStdout = Join-Path $runDirectory 'omen-harness.stdout.log'
        $omenStderr = Join-Path $runDirectory 'omen-harness.stderr.log'
        $omenHarnessArguments = @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $clientHarness,
            '-Action', 'smoke',
            '-Client', 'omen',
            '-Character', $OmenCharacter,
            '-Server', $Server,
            '-GatewayUrl', $OmenGatewayUrl,
            '-RunId', $RunId,
            '-DllPath', $dll,
            '-ScenarioPath', $scenario,
            '-EvidenceRoot', $EvidenceRoot,
            '-HoldSeconds', [string]$HoldSeconds,
            '-WaitSeconds', [string]$WaitSeconds)
        if ($useRoutedRpc) {
            $omenHarnessArguments += '-EnableRoutedRpcCutover'
        }
        if ($EnableZdoJournalCutover) {
            $omenHarnessArguments += '-EnableZdoJournalCutover'
        }
        if ($EnableZdoJournalCanonicalSession) {
            $omenHarnessArguments += '-EnableZdoJournalCanonicalSession'
        }
        if ($EnableOwnershipLeaseCutover) {
            $omenHarnessArguments += '-EnableOwnershipLeaseCutover'
        }
        if ($EnableWorldZoneCutover) {
            $omenHarnessArguments += '-EnableWorldZoneCutover'
        }
        if ($EnableMotionAuthorityCutover) {
            $omenHarnessArguments += '-EnableMotionAuthorityCutover'
            # C6/C8 are disposable Lab runs. The client harness enables the
            # canonical session/motion lane only for this bounded smoke and
            # restores the exact at-rest config afterward.
            $omenHarnessArguments += '-EnableLabSession'
        }
        if ($EnableSocketQuarantineCutover) {
            $omenHarnessArguments += '-EnableSocketQuarantineCutover'
        }
        if ($EnableSteamFreeColdJoin) {
            $omenHarnessArguments += '-EnableSteamFreeColdJoin'
        }
        $omenHarnessProcess = Start-Process `
            -FilePath (Join-Path $PSHOME 'powershell.exe') `
            -ArgumentList $omenHarnessArguments `
            -WindowStyle Hidden `
            -RedirectStandardOutput $omenStdout `
            -RedirectStandardError $omenStderr `
            -PassThru

        # Every concurrent-harness run needs the i5 leg queued; gating this on
        # the full composition left focused two-client gates polling a task
        # that was never started and misreading its stale prior result.
        $queue = Invoke-I5Harness $i5Arguments
        $queue | Write-Host
        $i5Queued = $true

        if ($EnableGatewayJournalRestartProof) {
            $serverJournalPath =
                '/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/zdo-journal-cutover.jsonl'
            $mutationDeadline = (Get-Date).AddSeconds($WaitSeconds)
            $mutationRow = $null
            do {
                $omenState = Get-OmenHarnessState
                if ($omenState.terminal) {
                    throw "OMEN harness ended before the first durable C3 mutation: $($omenState.detail)"
                }
                $i5State = Get-I5HarnessState
                if ($i5State.terminal) {
                    throw "i5 harness ended before the first durable C3 mutation: $($i5State.detail)"
                }
                $tail = & ssh -o BatchMode=yes am4 `
                    "if test -f '$serverJournalPath'; then tail -n 256 '$serverJournalPath'; fi"
                if ($LASTEXITCODE -ne 0) {
                    throw 'Server C3 evidence tail failed while waiting for the first mutation.'
                }
                foreach ($line in @($tail)) {
                    try {
                        $row = $line | ConvertFrom-Json -ErrorAction Stop
                        # Only the correlated drive_complete may trigger the
                        # restart: an early mutation_posted raced OMEN's still
                        # -running drive in full28, and reincarnation then
                        # discarded the acks the drive was waiting on. The
                        # durable_objects check below still guards the
                        # delivery-only case the C7-era correction addressed.
                        if ($row.run_id -eq $RunId -and
                            $row.state -eq 'drive_complete' -and
                            $row.action_id -eq 'omen-c8-zdo-journal-drive') {
                            $mutationRow = $row
                        }
                    } catch { }
                }
                if (-not $mutationRow) { Start-Sleep -Seconds 2 }
            } while (-not $mutationRow -and (Get-Date) -lt $mutationDeadline)
            if (-not $mutationRow) {
                throw 'The correlated durable C3 drive was not observed before the deadline.'
            }

            $beforeRestart =
                Invoke-RestMethod -Method Get -Uri "$OmenGatewayUrl/valheim/zdo-journal/status"
            if ([long]$beforeRestart.durable_objects -lt 1) {
                throw 'The correlated C3 drive completed with zero durable Gateway objects.'
            }
            $restartStarted = [DateTimeOffset]::UtcNow
            Push-Location $gatewayCompose
            try {
                & docker compose -p lumberjacks-local restart gateway
                if ($LASTEXITCODE -ne 0) { throw 'Gateway restart for C3 replay proof failed.' }
            } finally {
                Pop-Location
            }
            $healthDeadline = (Get-Date).AddSeconds(90)
            do {
                try {
                    $afterRestart =
                        Invoke-RestMethod -Method Get -Uri "$OmenGatewayUrl/valheim/zdo-journal/status"
                } catch {
                    $afterRestart = $null
                }
                if (-not $afterRestart) { Start-Sleep -Seconds 1 }
            } while (-not $afterRestart -and (Get-Date) -lt $healthDeadline)
            if (-not $afterRestart) {
                throw 'Gateway did not restore the C3 journal status surface after restart.'
            }
            if ([long]$afterRestart.durable_objects -lt 1) {
                throw 'Gateway restart replay restored zero durable C3 objects.'
            }
            $gatewayRestartReceipt = [ordered]@{
                schema_version = 1
                run_id = $RunId
                restarted_utc = $restartStarted.ToString('o')
                mutation = $mutationRow
                before = $beforeRestart
                after = $afterRestart
                durable_replay_verified = [long]$afterRestart.durable_objects -ge 1
            }
            Write-JsonAtomic `
                (Join-Path $runDirectory 'gateway-journal-restart.json') `
                $gatewayRestartReceipt
        }

        if (-not $i5Queued) {
            $queue = Invoke-I5Harness $i5Arguments
            $queue | Write-Host
            $i5Queued = $true
        }
    } else {
        $queue = Invoke-I5Harness $i5Arguments
        $queue | Write-Host

        & $clientHarness `
            -Action smoke `
            -Client omen `
            -Character $OmenCharacter `
            -Server $Server `
            -GatewayUrl $OmenGatewayUrl `
            -RunId $RunId `
            -DllPath $dll `
            -ScenarioPath $scenario `
            -EvidenceRoot $EvidenceRoot `
            -HoldSeconds $HoldSeconds `
            -EnableRoutedRpcCutover:$useRoutedRpc `
            -EnableZdoJournalCutover:$EnableZdoJournalCutover `
            -EnableZdoJournalCanonicalSession:$EnableZdoJournalCanonicalSession `
            -EnableOwnershipLeaseCutover:$EnableOwnershipLeaseCutover `
            -EnableWorldZoneCutover:$EnableWorldZoneCutover `
            -EnableMotionAuthorityCutover:$EnableMotionAuthorityCutover `
            -EnableSocketQuarantineCutover:$EnableSocketQuarantineCutover `
            -EnableSteamFreeColdJoin:$EnableSteamFreeColdJoin `
            -WaitSeconds $WaitSeconds
        if ($LASTEXITCODE -ne 0) { throw 'OMEN cutover scenario failed.' }
    }

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    do {
        $i5State = Get-I5HarnessState
        $omenState = if ($useConcurrentHarness) {
            Get-OmenHarnessState
        } else {
            [pscustomobject]@{
                terminal = $true
                success = $true
                detail = 'synchronous'
            }
        }
        if ($i5State.terminal -and -not $i5State.success) {
            throw "i5 scheduled task failed: $($i5State.detail)"
        }
        if ($omenState.terminal -and -not $omenState.success) {
            throw "OMEN cutover scenario failed: $($omenState.detail); see $omenStderr."
        }
        if ($i5State.terminal -and $omenState.terminal) { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if (-not $i5State.terminal -or
        ($useConcurrentHarness -and -not $omenState.terminal)) {
        throw "The two-client scenario did not finish within $WaitSeconds seconds."
    }
    if ($EnableOwnershipLeaseCutover) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/ownership-lease-cutover.jsonl' `
            "$serverDirectory\ownership-lease-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server ownership-lease evidence retrieval failed.'
        }
    }
    if ($EnableWorldZoneCutover) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/world-zone-cutover.jsonl' `
            "$serverDirectory\world-zone-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server world/zone evidence retrieval failed.'
        }
    }
    if ($EnableMotionAuthorityCutover) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        $remoteMotionEvidence =
            '/home/derek/comfy-valheim-lab/server-state/config/bepinex/' +
            'comfy-network-sense/motion-authority-cutover.jsonl'
        & ssh -o BatchMode=yes am4 "test -f '$remoteMotionEvidence'"
        if ($LASTEXITCODE -eq 0) {
            & scp "am4:$remoteMotionEvidence" `
                "$serverDirectory\motion-authority-cutover.jsonl"
            if ($LASTEXITCODE -ne 0) {
                throw 'Server motion-authority evidence retrieval failed.'
            }
        } elseif ($LASTEXITCODE -eq 1) {
            # LumberjacksMotionRunner is a client-only adapter and explicitly
            # refuses to run on a dedicated server. A clean AM4 therefore has no
            # server-side motion event file for either active C6 probes or C7's
            # wait-only prerequisite. Preserve that absence as an explicit
            # boundary receipt; the two rendered client files are the C6 proof.
            $quietReason = if ($EnableSteamFreeColdJoin) {
                'c7_cold_join_has_no_motion_actions'
            } else {
                'dedicated_server_has_no_client_motion_adapter'
            }
            Write-JsonAtomic `
                (Join-Path $serverDirectory 'motion-authority-evidence-status.json') `
                ([ordered]@{
                    schema_version = 1
                    receipt_type = 'motion_authority_evidence_status'
                    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
                    run_id = $RunId
                    result = 'not_emitted'
                    reason = $quietReason
                    prerequisite_arming_receipt =
                        'server-runtime-motion-authority.json'
                    authoritative_evidence = @(
                        'omen/motion-authority-cutover.jsonl'
                        'i5/motion-authority-cutover.jsonl'
                    )
                })
        } else {
            throw 'Server motion-authority evidence preflight failed.'
        }
    }
    if ($EnableSteamFreeColdJoin) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/logical-peer-cutover.jsonl' `
            "$serverDirectory\logical-peer-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server logical-peer evidence retrieval failed.'
        }
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/native-network-use.jsonl' `
            "$serverDirectory\native-network-use.jsonl"
        if ($LASTEXITCODE -ne 0) {
            throw 'Server native-network evidence retrieval failed.'
        }
    }

    & scp -r `
        "i5:C:/deploy/baseline/fieldlab/runs/native-valheim/$RunId/i5" `
        "$runDirectory\"
    if ($LASTEXITCODE -ne 0) { throw 'i5 evidence retrieval failed.' }
    if ($EnableDirectControlCutover) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/direct-control-cutover.jsonl' `
            "$serverDirectory\direct-control-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) { throw 'Server direct-control evidence retrieval failed.' }
    }
    if ($useRoutedRpc) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/routed-rpc-cutover.jsonl' `
            "$serverDirectory\routed-rpc-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) { throw 'Server routed-RPC evidence retrieval failed.' }
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/ship-cutover.jsonl' `
            "$serverDirectory\ship-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) { throw 'Server ship-cutover evidence retrieval failed.' }
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/saddle-cutover.jsonl' `
            "$serverDirectory\saddle-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) { throw 'Server saddle-cutover evidence retrieval failed.' }
    }
    if ($EnableZdoJournalCutover) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/zdo-journal-cutover.jsonl' `
            "$serverDirectory\zdo-journal-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) { throw 'Server ZDO-journal evidence retrieval failed.' }
        if ($EnableZdoJournalCanonicalSession) {
            & scp `
                'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/lumberjacks-game-session.jsonl' `
                "$serverDirectory\lumberjacks-game-session.jsonl"
            if ($LASTEXITCODE -ne 0) {
                throw 'Server canonical-session evidence retrieval failed.'
            }
        }
    }

    if ($EnableGatewayJournalRestartProof) {
        $worldEpoch = [string]$gatewayRestartReceipt.mutation.world_epoch
        $finalRunStatus =
            Invoke-RestMethod -Method Get -Uri (
                "$OmenGatewayUrl/valheim/zdo-journal/status/$RunId/$worldEpoch")
        $finalGlobalStatus =
            Invoke-RestMethod -Method Get -Uri (
                "$OmenGatewayUrl/valheim/zdo-journal/status")
        $resetReceipt =
            Invoke-RestMethod -Method Post -Uri (
                "$OmenGatewayUrl/valheim/zdo-journal/reset/$worldEpoch")
        Write-JsonAtomic `
            (Join-Path $runDirectory 'gateway-journal-final.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                world_epoch = $worldEpoch
                captured_utc = [DateTimeOffset]::UtcNow.ToString('o')
                run_status = $finalRunStatus
                global_status = $finalGlobalStatus
                reset = $resetReceipt
            })
    }

    $omenLifecycle =
        Get-Content -LiteralPath (Join-Path $runDirectory 'omen\lifecycle.json') -Raw |
        ConvertFrom-Json
    $i5Lifecycle =
        Get-Content -LiteralPath (Join-Path $runDirectory 'i5\lifecycle.json') -Raw |
        ConvertFrom-Json
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'native_cutover_composition'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        run_id = $RunId
        server = $Server
        result = 'completed'
        mod_release = $modRelease
        gateway_image = $GatewayImage
        gateway_image_id = if ($gatewayImageReceipt) {
            $gatewayImageReceipt.running_image_id
        } else { $null }
        steam_free_cold_join = [bool]$EnableSteamFreeColdJoin
        native_zero_composition =
            [bool]$EnableNativeZeroComposition -or [bool]$EnableC8Composition
        scenario_sha256 =
            (Get-FileHash -LiteralPath $scenario -Algorithm SHA256).Hash.ToLowerInvariant()
        clients = @(
            [ordered]@{
                client = 'omen'
                result = $omenLifecycle.result
                resume_count = $omenLifecycle.resume_count
                scenario_terminal = $omenLifecycle.scenario_terminal
            },
            [ordered]@{
                client = 'i5'
                result = $i5Lifecycle.result
                resume_count = $i5Lifecycle.resume_count
                scenario_terminal = $i5Lifecycle.scenario_terminal
            })
    }
    $receiptPath = Join-Path $runDirectory 'composition.json'
    [IO.File]::WriteAllText(
        $receiptPath,
        ($receipt | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    if ($EnableSteamFreeColdJoin) {
        $summaryOutput =
            & (Join-Path $PSScriptRoot 'Write-LogicalPeerCutoverSummary.ps1') `
                -RunDirectory $runDirectory `
                -RunId $RunId
        if ($LASTEXITCODE -ne 0) {
            throw 'C7 Steam-free logical-peer evidence did not satisfy the reducer.'
        }
        $summaryOutput | Write-Host
    }
    $completed = $true
    $receipt | ConvertTo-Json -Depth 12
} finally {
    if ($omenHarnessProcess -and -not $omenHarnessProcess.HasExited) {
        Stop-Process -Id $omenHarnessProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not $completed) {
        # Stop can race a client the harness was still spawning (restartgate1
        # left an orphan that fail-closed the next run), so sweep briefly
        # until no Valheim process remains.
        & $clientHarness -Action stop -Client omen | Out-Null
        # A forced OMEN harness stop can bypass its in-process finally block.
        # Restore from the durable pre-Lab-session backup after the game is
        # stopped so a failed rendered run cannot leave the canonical lane
        # enabled at rest.
        $omenLabBackup =
            Join-Path $runDirectory 'omen\config-before-lab-session.cfg'
        if (Test-Path -LiteralPath $omenLabBackup -PathType Leaf) {
            try {
                & $clientHarness `
                    -Action restore-lab-session `
                    -Client omen `
                    -RunId $RunId `
                    -EvidenceRoot $EvidenceRoot | Out-Null
            } catch {
                Write-Warning ("OMEN Lab-session restore failed: " + $_.Exception.Message)
            }
        }
        if ($i5Queued) {
            try {
                [void](Invoke-I5Harness @('-Action', 'stop', '-Client', 'i5'))
            } catch { }
            # Drain the i5 scheduled task before exiting: stop kills the client
            # and the pending request, but the harness wait loop takes time to
            # notice, and a successor run's queue-smoke correctly refuses while
            # the task still reports Running (full33 collided with full32's tail).
            try {
                $i5DrainDeadline = (Get-Date).AddSeconds(90)
                do {
                    $i5Drain =
                        (Invoke-I5Harness @('-Action', 'task-status', '-Client', 'i5') `
                            -join [Environment]::NewLine) | ConvertFrom-Json
                    if ($i5Drain.state -ne 'Running') { break }
                    Start-Sleep -Seconds 5
                } while ((Get-Date) -lt $i5DrainDeadline)
            } catch { }
            try {
                [void](Invoke-I5Harness @(
                    '-Action', 'restore-lab-session',
                    '-Client', 'i5',
                    '-RunId', $RunId,
                    '-EvidenceRoot', $remoteEvidenceRoot))
            } catch {
                Write-Warning ("i5 Lab-session restore failed: " + $_.Exception.Message)
            }
        }
        for ($sweep = 0; $sweep -lt 3; $sweep++) {
            Start-Sleep -Seconds 4
            if (-not (Get-Process -Name valheim -ErrorAction SilentlyContinue)) { break }
            & $clientHarness -Action stop -Client omen | Out-Null
        }
        # Preserve the remote i5 lifecycle and logs even when the OMEN leg
        # fails first. A failed composition is still evidence; losing the
        # second client's receipt makes a rendezvous failure look like an
        # unexplained one-sided timeout.
        try {
            $i5EvidenceDirectory = Join-Path $runDirectory 'i5'
            if ($i5Queued -and
                -not (Test-Path (Join-Path $i5EvidenceDirectory 'lifecycle.json'))) {
                & scp -r `
                    "i5:$remoteEvidenceRoot/$RunId/i5" `
                    "$runDirectory\"
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "i5 failure evidence retrieval exited $LASTEXITCODE."
                }
            }
        } catch {
            Write-Warning ("i5 failure evidence retrieval failed: " + $_.Exception.Message)
        }
    }
    Copy-ServerFailureEvidenceBestEffort
    if ($gatewayTunnel -and -not $gatewayTunnel.HasExited) {
        Stop-Process -Id $gatewayTunnel.Id -Force -ErrorAction SilentlyContinue
    }
    # Residue hygiene, unconditional: destroy this run's leaked synthetic
    # drive/probe objects. A completed run receipts destroyed=0 (steady-state
    # proof); an abort is exactly where the leak happens (full32/full33 grew
    # zone 35,-1 from 1245 to 1790 and broke bootstrap convergence). Failure
    # here is recorded but never masks the run's own outcome.
    try {
        $residueCleanupOutput =
            & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                -Setting cutoverResidueCleanup `
                -Value $RunId `
                -RequestId "$RunId-residue-cleanup" `
                -WaitSeconds 30
        if ($LASTEXITCODE -eq 0) {
            $residueCleanupReceipt =
                (($residueCleanupOutput -join [Environment]::NewLine) |
                    ConvertFrom-Json)
            if ($completed -and $scenarioDocument.profile -eq 'c10a-mount') {
                $effect = [string]$residueCleanupReceipt.effect
                if ($effect -notmatch '(?:^| )matched=1(?: |$)' -or
                    $effect -notmatch '(?:^| )destroyed=1(?: |$)' -or
                    $effect -notmatch '(?:^| )skipped_live_owner=0(?: |$)' -or
                    $effect -notmatch '(?:^| )mount=1(?: |$)') {
                    $residueCleanupError =
                        "C10a mount cleanup did not destroy exactly one tagged mount: $effect"
                }
            }
        } else {
            $residueCleanupError =
                "Server residue cleanup exited $LASTEXITCODE."
        }
    } catch {
        $residueCleanupError = $_.Exception.Message
    }
    if ($serverPoisonArmed) {
        try {
            $poisonDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting nativeNetworkPoisonEnabled `
                    -Value false `
                    -RequestId "$RunId-native-poison-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverPoisonReceipts +=
                    (($poisonDisarmOutput -join [Environment]::NewLine) |
                        ConvertFrom-Json)
            } else {
                $serverPoisonDisarmError =
                    "Server native poison disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverPoisonDisarmError = $_.Exception.Message
        }
    }
    if ($serverPortalTraversalChanged) {
        try {
            $portalTraversalRestoreOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting portalTraversalEnabled `
                    -Value false `
                    -RequestId "$RunId-portal-traversal-restore"
            if ($LASTEXITCODE -eq 0) {
                $serverPortalTraversalReceipts +=
                    (($portalTraversalRestoreOutput -join [Environment]::NewLine) |
                        ConvertFrom-Json)
            } else {
                $serverPortalTraversalRestoreError =
                    "Server portal-traversal restore exited $LASTEXITCODE."
            }
        } catch {
            $serverPortalTraversalRestoreError = $_.Exception.Message
        }
    }
    if ($serverLogicalPeerArmed) {
        try {
            $logicalPeerDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting logicalPeerCutoverEnabled `
                    -Value false `
                    -RequestId "$RunId-logical-peer-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverLogicalPeerReceipts +=
                    (($logicalPeerDisarmOutput -join [Environment]::NewLine) |
                        ConvertFrom-Json)
            } else {
                $serverLogicalPeerDisarmError =
                    "Server logical-peer disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverLogicalPeerDisarmError = $_.Exception.Message
        }
    }
    if ($serverMotionArmed) {
        try {
            $motionDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting motionAuthorityCutoverEnabled `
                    -Value false `
                    -RequestId "$RunId-motion-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverMotionReceipts +=
                    (($motionDisarmOutput -join [Environment]::NewLine) |
                        ConvertFrom-Json)
            } else {
                $serverMotionDisarmError =
                    "Server motion-authority disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverMotionDisarmError = $_.Exception.Message
        }
    }
    if ($serverDirectArmed) {
        try {
            $disarmOutput = & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                -Setting directControlCutoverEnabled `
                -Value false `
                -RequestId "$RunId-direct-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverControlReceipts +=
                    (($disarmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
            } else {
                $serverDisarmError = "Server direct-control disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverDisarmError = $_.Exception.Message
        }
    }
    if ($serverRoutedArmed) {
        try {
            $routeDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting routedRpcCutoverEnabled `
                    -Value false `
                    -RequestId "$RunId-routed-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverRoutedReceipts +=
                    (($routeDisarmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
            } else {
                $serverRoutedDisarmError =
                    "Server routed-RPC disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverRoutedDisarmError = $_.Exception.Message
        }
    }
    if ($serverOwnershipArmed) {
        try {
            $ownershipDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting ownershipLeaseCutoverEnabled `
                    -Value false `
                    -RequestId "$RunId-ownership-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverOwnershipReceipts +=
                    (($ownershipDisarmOutput -join [Environment]::NewLine) |
                        ConvertFrom-Json)
            } else {
                $serverOwnershipDisarmError =
                    "Server ownership-lease disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverOwnershipDisarmError = $_.Exception.Message
        }
    }
    if ($serverWorldZoneArmed) {
        try {
            $worldZoneDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting worldZoneCutoverEnabled `
                    -Value false `
                    -RequestId "$RunId-world-zone-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverWorldZoneReceipts +=
                    (($worldZoneDisarmOutput -join [Environment]::NewLine) |
                        ConvertFrom-Json)
            } else {
                $serverWorldZoneDisarmError =
                    "Server world/zone disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverWorldZoneDisarmError = $_.Exception.Message
        }
    }
    if ($serverJournalArmed) {
        if ($serverJournalCanonicalArmed) {
            try {
                $canonicalDisarmOutput =
                    & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                        -Setting zdoJournalCanonicalSessionEnabled `
                        -Value false `
                        -RequestId "$RunId-journal-canonical-disarm"
                if ($LASTEXITCODE -eq 0) {
                    $serverJournalReceipts +=
                        (($canonicalDisarmOutput -join [Environment]::NewLine) |
                            ConvertFrom-Json)
                } else {
                    $serverJournalDisarmError =
                        "Server canonical ZDO-journal disarm exited $LASTEXITCODE."
                }
            } catch {
                $serverJournalDisarmError = $_.Exception.Message
            }
        }
        try {
            $journalDisarmOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting zdoJournalCutoverEnabled `
                    -Value false `
                    -RequestId "$RunId-journal-disarm"
            if ($LASTEXITCODE -eq 0) {
                $serverJournalReceipts +=
                    (($journalDisarmOutput -join [Environment]::NewLine) | ConvertFrom-Json)
            } else {
                $serverJournalDisarmError =
                    "Server ZDO-journal disarm exited $LASTEXITCODE."
            }
        } catch {
            $serverJournalDisarmError = $_.Exception.Message
        }
    }
    if ($serverGatewayChanged -and
        -not [string]::IsNullOrWhiteSpace($oldServerGatewayUrl)) {
        try {
            $restoreOutput =
                & (Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1') `
                    -Setting lumberjacksGatewayUrl `
                    -Value $oldServerGatewayUrl `
                    -RequestId "$RunId-routed-restore"
            if ($LASTEXITCODE -eq 0) {
                $serverRoutedReceipts +=
                    (($restoreOutput -join [Environment]::NewLine) | ConvertFrom-Json)
            } elseif (-not $serverRoutedDisarmError) {
                $serverRoutedDisarmError =
                    "Server Gateway URL restore exited $LASTEXITCODE."
            }
        } catch {
            if (-not $serverRoutedDisarmError) {
                $serverRoutedDisarmError = $_.Exception.Message
            }
        }
    }
    if ($EnableDirectControlCutover -and $serverControlReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-direct-control.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverControlReceipts
                disarm_error = $serverDisarmError
            })
    }
    if ($useRoutedRpc -and $serverRoutedReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-routed-rpc.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverRoutedReceipts
                disarm_error = $serverRoutedDisarmError
            })
    }
    if ($EnableZdoJournalCutover -and $serverJournalReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-zdo-journal.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverJournalReceipts
                disarm_error = $serverJournalDisarmError
            })
    }
    if ($EnableOwnershipLeaseCutover -and
        $serverOwnershipReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-ownership-lease.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverOwnershipReceipts
                disarm_error = $serverOwnershipDisarmError
            })
    }
    if ($EnableWorldZoneCutover -and $serverWorldZoneReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-world-zone.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverWorldZoneReceipts
                disarm_error = $serverWorldZoneDisarmError
            })
    }
    if ($EnablePortalTraversal -and
        $serverPortalTraversalReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-portal-traversal.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                persistence = 'runtime_only'
                receipts = $serverPortalTraversalReceipts
                restore_error = $serverPortalTraversalRestoreError
            })
    }
    if ($EnableMotionAuthorityCutover -and
        $serverMotionReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-motion-authority.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverMotionReceipts
                disarm_error = $serverMotionDisarmError
            })
    }
    if ($EnableSteamFreeColdJoin -and
        $serverLogicalPeerReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-logical-peer.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverLogicalPeerReceipts
                disarm_error = $serverLogicalPeerDisarmError
            })
    }
    if ($EnableServerNativePoison -and $serverPoisonReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-native-poison.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverPoisonReceipts
                disarm_error = $serverPoisonDisarmError
            })
    }
    if ($residueCleanupReceipt -or $residueCleanupError) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'residue-cleanup.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipt = $residueCleanupReceipt
                cleanup_error = $residueCleanupError
            })
    }
    if ($scenarioDocument.profile -eq 'c10a-vehicle') {
        # The process exit only says every manifest action reached a terminal
        # state. Correlate both clients with AM4 after cleanup so a stale helm
        # handoff, missing observer leg, or artifact mismatch cannot inherit a
        # green composition result.
        try {
            $vehicleSummaryOutput =
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
                    (Join-Path $PSScriptRoot 'Write-C10aVehicleSummary.ps1') `
                    -RunDirectory $runDirectory `
                    -RunId $RunId `
                    -OutputPath (Join-Path $runDirectory 'c10a-vehicle-summary.json')
            $vehicleSummaryExitCode = $LASTEXITCODE
            $vehicleSummaryOutput | Write-Host
            if ($completed -and $vehicleSummaryExitCode -ne 0) {
                $vehicleSummaryError =
                    "C10a vehicle reducer exited $vehicleSummaryExitCode."
            }
        } catch {
            if ($completed) { $vehicleSummaryError = $_.Exception.Message }
            Write-Warning ("C10a vehicle reducer failed: " + $_.Exception.Message)
        }
    }
    if ($scenarioDocument.profile -eq 'c10a-mount') {
        # Run the reducer out-of-process so its fail-closed exit code cannot
        # interrupt this finally block. Failed scenarios need the same
        # correlated receipt as successful ones, after server logs and exact
        # residue cleanup have been captured.
        try {
            $mountSummaryOutput =
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
                    (Join-Path $PSScriptRoot 'Write-C10aMountSummary.ps1') `
                    -RunDirectory $runDirectory `
                    -RunId $RunId `
                    -OutputPath (Join-Path $runDirectory 'c10a-mount-summary.json')
            $mountSummaryExitCode = $LASTEXITCODE
            $mountSummaryOutput | Write-Host
            if ($completed -and $mountSummaryExitCode -ne 0) {
                $mountSummaryError =
                    "C10a mount reducer exited $mountSummaryExitCode."
            }
        } catch {
            if ($completed) { $mountSummaryError = $_.Exception.Message }
            Write-Warning ("C10a mount reducer failed: " + $_.Exception.Message)
        }
    }
    if ($completed -and $serverDisarmError) {
        throw "Scenario completed but server direct-control disarm failed: $serverDisarmError"
    }
    if ($completed -and $serverRoutedDisarmError) {
        throw "Scenario completed but server routed-RPC cleanup failed: $serverRoutedDisarmError"
    }
    if ($completed -and $serverJournalDisarmError) {
        throw "Scenario completed but server ZDO-journal cleanup failed: $serverJournalDisarmError"
    }
    if ($completed -and $serverOwnershipDisarmError) {
        throw "Scenario completed but server ownership-lease cleanup failed: $serverOwnershipDisarmError"
    }
    if ($completed -and $serverWorldZoneDisarmError) {
        throw "Scenario completed but server world/zone cleanup failed: $serverWorldZoneDisarmError"
    }
    if ($completed -and $serverMotionDisarmError) {
        throw "Scenario completed but server motion-authority cleanup failed: $serverMotionDisarmError"
    }
    if ($completed -and $serverLogicalPeerDisarmError) {
        throw "Scenario completed but server logical-peer cleanup failed: $serverLogicalPeerDisarmError"
    }
    if ($completed -and $serverPoisonDisarmError) {
        throw "Scenario completed but server native-poison cleanup failed: $serverPoisonDisarmError"
    }
    if ($completed -and $residueCleanupError) {
        throw "Scenario completed but server residue cleanup failed: $residueCleanupError"
    }
    if ($completed -and $vehicleSummaryError) {
        throw "C10a vehicle physical evidence did not satisfy the reducer: $vehicleSummaryError"
    }
    if ($completed -and $mountSummaryError) {
        throw "C10a mount physical evidence did not satisfy the reducer: $mountSummaryError"
    }
}

if ($completed -and $EnableC8Composition) {
    $saveIntegrityAfter = Get-Am4SaveFingerprint
    Write-JsonAtomic `
        (Join-Path $runDirectory 'save-integrity-after.json') `
        $saveIntegrityAfter
    $saveIntegrity =
        Compare-Am4SaveFingerprint $saveIntegrityBefore $saveIntegrityAfter
    Write-JsonAtomic `
        (Join-Path $runDirectory 'c8-save-integrity.json') `
        $saveIntegrity
    if ($saveIntegrity.result -ne 'passed') {
        throw 'C8 save-integrity comparison failed.'
    }
    $c8SummaryPath = Join-Path $runDirectory 'c8-composition-summary.json'
    $c8SummaryOutput =
        & (Join-Path $PSScriptRoot 'Write-C8CompositionSummary.ps1') `
            -RunDirectory $runDirectory `
            -RunId $RunId `
            -OutputPath $c8SummaryPath
    $c8Summary =
        Get-Content -LiteralPath $c8SummaryPath -Raw -Encoding utf8 |
        ConvertFrom-Json
    if ($c8Summary.result -ne 'passed') {
        throw 'C8 native-zero composition did not satisfy the reducer.'
    }
    $c8SummaryOutput | Write-Host
}
