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

    if ($EnableRoutedRpcCutover) {
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
    if ($EnableRoutedRpcCutover) {
        $i5Arguments += '-EnableRoutedRpcCutover'
    }
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
        -EnableRoutedRpcCutover:$EnableRoutedRpcCutover `
        -WaitSeconds $WaitSeconds
    if ($LASTEXITCODE -ne 0) { throw 'OMEN cutover scenario failed.' }

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
    if ($EnableRoutedRpcCutover) {
        $serverDirectory = Join-Path $runDirectory 'server'
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        & scp `
            'am4:/home/derek/comfy-valheim-lab/server-state/config/bepinex/comfy-network-sense/routed-rpc-cutover.jsonl' `
            "$serverDirectory\routed-rpc-cutover.jsonl"
        if ($LASTEXITCODE -ne 0) { throw 'Server routed-RPC evidence retrieval failed.' }
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
    if ($EnableRoutedRpcCutover -and $serverRoutedReceipts.Count -gt 0) {
        Write-JsonAtomic `
            (Join-Path $runDirectory 'server-runtime-routed-rpc.json') `
            ([ordered]@{
                schema_version = 1
                run_id = $RunId
                receipts = $serverRoutedReceipts
                disarm_error = $serverRoutedDisarmError
            })
    }
    if ($completed -and $serverDisarmError) {
        throw "Scenario completed but server direct-control disarm failed: $serverDisarmError"
    }
    if ($completed -and $serverRoutedDisarmError) {
        throw "Scenario completed but server routed-RPC cleanup failed: $serverRoutedDisarmError"
    }
}
