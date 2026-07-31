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

    [switch] $EnableMotionAuthorityCutover,

    [switch] $EnableGatewayJournalRestartProof,

    [string] $ServerGatewayUrl = 'http://100.124.12.37:4000'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$clientHarness = Join-Path $PSScriptRoot 'Invoke-NativeValheimClient.ps1'
$i5Tools = Join-Path $repoRoot 'tools\i5'

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

$scenario = (Resolve-Path -LiteralPath $ScenarioPath -ErrorAction Stop).Path
$dll = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
$scenarioName = Split-Path -Leaf $scenario
$remoteScenarioDirectory = 'C:/deploy/baseline/fieldlab/scenarios'
$remoteScenarioPath = "$remoteScenarioDirectory/$scenarioName"
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
$gatewayRestartReceipt = $null
$omenHarnessProcess = $null
$useRoutedRpc =
    [bool]$EnableRoutedRpcCutover -or [bool]$EnableZdoJournalCutover
$useConcurrentHarness =
    [bool]$EnableZdoJournalCutover -or [bool]$EnableMotionAuthorityCutover

function Write-JsonAtomic([string] $Path, [object] $Value) {
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllText(
        $temporary,
        ($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
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

try {
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    & (Join-Path $i5Tools 'Test-I5Link.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'The i5 lane is offline or failed preflight; no retry was attempted.'
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
    }

    if ($useConcurrentHarness) {
        $gatewayCompose = Join-Path $repoRoot 'Lumberjacks\infra\docker'
        Push-Location $gatewayCompose
        try {
            & docker compose -p lumberjacks-local up -d --no-deps --build gateway
            if ($LASTEXITCODE -ne 0) {
                throw 'Gateway deployment for the canonical-session slice failed.'
            }
        } finally {
            Pop-Location
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
        }
        $omenHarnessProcess = Start-Process `
            -FilePath (Join-Path $PSHOME 'powershell.exe') `
            -ArgumentList $omenHarnessArguments `
            -WindowStyle Hidden `
            -RedirectStandardOutput $omenStdout `
            -RedirectStandardError $omenStderr `
            -PassThru

        if ($EnableGatewayJournalRestartProof) {
            $serverJournalPath =
                '/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/zdo-journal-cutover.jsonl'
            $mutationDeadline = (Get-Date).AddSeconds($WaitSeconds)
            $mutationRow = $null
            do {
                $tail = & ssh -o BatchMode=yes am4 `
                    "if test -f '$serverJournalPath'; then tail -n 256 '$serverJournalPath'; fi"
                if ($LASTEXITCODE -ne 0) {
                    throw 'Server C3 evidence tail failed while waiting for the first mutation.'
                }
                foreach ($line in @($tail)) {
                    try {
                        $row = $line | ConvertFrom-Json -ErrorAction Stop
                        if ($row.run_id -eq $RunId -and
                            $row.state -eq 'mutation_posted' -and
                            $row.detail -notmatch 'delivery_only=True') {
                            $mutationRow = $row
                        }
                    } catch { }
                }
                if (-not $mutationRow) { Start-Sleep -Seconds 2 }
            } while (-not $mutationRow -and (Get-Date) -lt $mutationDeadline)
            if (-not $mutationRow) {
                throw 'The first durable C3 mutation was not observed before the deadline.'
            }

            $beforeRestart =
                Invoke-RestMethod -Method Get -Uri "$OmenGatewayUrl/valheim/zdo-journal/status"
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

        $queue = Invoke-I5Harness $i5Arguments
        $queue | Write-Host
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
            -EnableMotionAuthorityCutover:$EnableMotionAuthorityCutover `
            -WaitSeconds $WaitSeconds
        if ($LASTEXITCODE -ne 0) { throw 'OMEN cutover scenario failed.' }
    }

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    do {
        $statusText = Invoke-I5Harness @('-Action', 'task-status', '-Client', 'i5')
        $status = ($statusText -join [Environment]::NewLine) | ConvertFrom-Json
        if ($status.state -eq 'Ready') { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if ($status.state -ne 'Ready') {
        throw "i5 scheduled task did not finish within $WaitSeconds seconds."
    }
    if ([int]$status.last_task_result -ne 0) {
        throw "i5 scheduled task failed with result $($status.last_task_result)."
    }
    if ($useConcurrentHarness) {
        while (-not $omenHarnessProcess.HasExited -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $omenHarnessProcess.Refresh()
        }
        if (-not $omenHarnessProcess.HasExited) {
            throw 'OMEN C3 harness did not finish before the scenario deadline.'
        }
        # PowerShell 5.1 can expose a null ExitCode until WaitForExit has
        # synchronized the native process handle, even after HasExited is true. In
        # some redirected-output runs it remains null after synchronization. Fail
        # closed against the harness's atomic lifecycle receipt in that case.
        $omenHarnessProcess.WaitForExit()
        $omenHarnessProcess.Refresh()
        $omenExitCode = $omenHarnessProcess.ExitCode
        if ($null -eq $omenExitCode) {
            $omenLifecyclePath = Join-Path $runDirectory 'omen\lifecycle.json'
            if (-not (Test-Path -LiteralPath $omenLifecyclePath -PathType Leaf)) {
                throw "OMEN journal harness exposed no exit code or lifecycle receipt; see $omenStderr."
            }
            $omenCompletedLifecycle =
                Get-Content -LiteralPath $omenLifecyclePath -Raw |
                ConvertFrom-Json
            if ($omenCompletedLifecycle.run_id -ne $RunId -or
                $omenCompletedLifecycle.result -ne 'joined_held_and_stopped' -or
                $omenCompletedLifecycle.scenario_terminal.state -ne 'scenario_complete' -or
                -not [string]::IsNullOrWhiteSpace(
                    [string]$omenCompletedLifecycle.error)) {
                throw "OMEN journal harness exposed no exit code and its lifecycle receipt is not complete; see $omenStderr."
            }
            $omenExitCode = 0
        }
        if ($omenExitCode -ne 0) {
            throw "OMEN journal harness failed with exit $omenExitCode; see $omenStderr."
        }
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
    $completed = $true
    $receipt | ConvertTo-Json -Depth 12
} finally {
    if (-not $completed) {
        & $clientHarness -Action stop -Client omen | Out-Null
        try {
            [void](Invoke-I5Harness @('-Action', 'stop', '-Client', 'i5'))
        } catch { }
    }
    if ($omenHarnessProcess -and -not $omenHarnessProcess.HasExited) {
        Stop-Process -Id $omenHarnessProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($gatewayTunnel -and -not $gatewayTunnel.HasExited) {
        Stop-Process -Id $gatewayTunnel.Id -Force -ErrorAction SilentlyContinue
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
    if ($completed -and $serverDisarmError) {
        throw "Scenario completed but server direct-control disarm failed: $serverDisarmError"
    }
    if ($completed -and $serverRoutedDisarmError) {
        throw "Scenario completed but server routed-RPC cleanup failed: $serverRoutedDisarmError"
    }
    if ($completed -and $serverJournalDisarmError) {
        throw "Scenario completed but server ZDO-journal cleanup failed: $serverJournalDisarmError"
    }
    if ($completed -and $serverWorldZoneDisarmError) {
        throw "Scenario completed but server world/zone cleanup failed: $serverWorldZoneDisarmError"
    }
}
