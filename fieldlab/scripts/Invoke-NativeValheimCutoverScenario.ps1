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

    [string] $OmenCharacter = 'Tugcorp',

    [string] $I5Character = 'durracktu',

    [string] $DllPath = '',

    [string] $EvidenceRoot = '',

    [ValidateRange(60, 1800)]
    [int] $WaitSeconds = 900,

    [ValidateRange(0, 120)]
    [int] $HoldSeconds = 5
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
    & (Join-Path $i5Tools 'Test-I5Link.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'The i5 lane is offline or failed preflight; no retry was attempted.'
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

    $queue = Invoke-I5Harness @(
        '-Action', 'queue-smoke',
        '-Client', 'i5',
        '-Character', $I5Character,
        '-Server', $Server,
        '-RunId', $RunId,
        '-ScenarioPath', $remoteScenarioPath,
        '-HoldSeconds', [string]$HoldSeconds,
        '-WaitSeconds', [string]$WaitSeconds)
    $queue | Write-Host

    & $clientHarness `
        -Action smoke `
        -Client omen `
        -Character $OmenCharacter `
        -Server $Server `
        -RunId $RunId `
        -DllPath $dll `
        -ScenarioPath $scenario `
        -EvidenceRoot $EvidenceRoot `
        -HoldSeconds $HoldSeconds `
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

    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    & scp -r `
        "i5:C:/deploy/baseline/fieldlab/runs/native-valheim/$RunId/i5" `
        "$runDirectory\"
    if ($LASTEXITCODE -ne 0) { throw 'i5 evidence retrieval failed.' }

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
}
