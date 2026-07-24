#Requires -Version 5.1
<##
.SYNOPSIS
Coordinate multiple disposable Valheim lab clients without touching physical installs.

.DESCRIPTION
This is the thin N-client layer over Invoke-HeadlessValheimLab.ps1. It exists to
remove the next human iteration cost in M7: every selected client is refreshed and
preflighted before any client is started, and a partial start is cleaned up before
the command returns.

The coordinator does not add a general workflow engine, arbitrary shell bridge,
profile creation, or player-install behavior. Once the clients are in-world, an
agent uses the existing bounded MCP mailbox and telemetry tools; this script owns
only lifecycle and receipt aggregation.

.PARAMETER Clients
One or more disposable Compose client numbers. Defaults to client01 and client02.

.PARAMETER Action
preflight, start, status, stop, or capture.

.PARAMETER EnvFile
Optional Compose environment file passed to every child lifecycle invocation.

.PARAMETER NoBuild
Stage the existing Release DLL without invoking the local mod build.

.EXAMPLE
.\Invoke-HeadlessValheimScenario.ps1 -Action preflight -Clients 01,02

.EXAMPLE
.\Invoke-HeadlessValheimScenario.ps1 -Action start -Clients 01,02 -NoBuild

.EXAMPLE
.\Invoke-HeadlessValheimScenario.ps1 -Action stop -Clients 01,02
#>
[CmdletBinding()]
param(
    [string[]] $Clients = @('01', '02'),

    [ValidateSet('preflight', 'start', 'status', 'stop', 'capture')]
    [string] $Action = 'preflight',

    [string] $EnvFile = '',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lifecycleScript = Join-Path $repoRoot 'fieldlab\scripts\Invoke-HeadlessValheimLab.ps1'
$stateRoot = Join-Path $repoRoot 'fieldlab\autonomous\state'
$normalizedClients = @($Clients |
    ForEach-Object { $_ -split ',' } |
    ForEach-Object {
        $value = $_.ToString().Trim()
        if ($value -notmatch '^(0?[1-4])$') { throw "client must be 01..04: $value" }
        '{0:D2}' -f [int]$value
    })
$duplicateClients = @($normalizedClients | Group-Object | Where-Object { $_.Count -gt 1 })
if ($duplicateClients.Count -gt 0) { throw "duplicate clients: $(($duplicateClients | ForEach-Object Name) -join ', ')" }
if (-not (Test-Path -LiteralPath $lifecycleScript -PathType Leaf)) { throw "lifecycle script not found: $lifecycleScript" }

$runId = 'multi-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$receiptPath = Join-Path $stateRoot ($runId + '.json')

function Write-Receipt([string] $Result, [object[]] $Results, [string] $Note = '') {
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'lab_multi_client_lifecycle'
        run_id = $runId
        generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
        action = $Action
        clients = $normalizedClients
        result = $Result
        note = $Note
        results = @($Results)
    }
    $temporary = "$receiptPath.tmp"
    ($receipt | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $receiptPath -Force
    Write-Host "scenario $Result -> $receiptPath"
    return $receipt
}

function Invoke-Lifecycle([string] $Client, [string] $ChildAction, [switch] $ChildNoBuild) {
    $invokeArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $lifecycleScript,
        '-Client', $Client,
        '-Action', $ChildAction
    )
    if ($EnvFile) { $invokeArgs += @('-EnvFile', $EnvFile) }
    if ($ChildNoBuild) { $invokeArgs += '-NoBuild' }

    Write-Host "[$Client] $ChildAction"
    $childOutput = @(& powershell.exe @invokeArgs 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($line in $childOutput) { Write-Host ("[$Client] {0}" -f $line) }
    [pscustomobject]@{
        client = "client$Client"
        action = $ChildAction
        exit_code = $exitCode
        ok = ($exitCode -eq 0)
    }
}

function Invoke-All([string] $ChildAction, [switch] $ChildNoBuild) {
    $results = @()
    foreach ($client in $normalizedClients) {
        $results += Invoke-Lifecycle $client $ChildAction -ChildNoBuild:$ChildNoBuild
    }
    return $results
}

function Read-ClientGate([string] $Client) {
    $path = Join-Path $stateRoot "client$Client\lab-preflight.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) }
    catch { return $null }
}

function Select-ScenarioResult([int] $FailureCount, [string] $Success, [string] $Failure) {
    if ($FailureCount -eq 0) { return $Success }
    return $Failure
}

function Select-ScenarioNote([int] $FailureCount, [string] $Success, [string] $Failure) {
    if ($FailureCount -eq 0) { return $Success }
    return $Failure
}

switch ($Action) {
    'preflight' {
        $results = Invoke-All 'preflight'
        $blocked = @($results | Where-Object { -not $_.ok })
        Write-Receipt (Select-ScenarioResult $blocked.Count 'ready' 'blocked') $results (Select-ScenarioNote $blocked.Count 'All selected clients passed the operator-touch gate.' 'No client was started; inspect each clientNN/lab-preflight.json.') | Out-Null
        if ($blocked.Count -gt 0) { exit 3 }
        break
    }
    'start' {
        # Refresh all clients first. Build only once; subsequent clients receive the
        # same verified DLL and get their own writable init script staged.
        $refreshResults = @()
        $first = $true
        foreach ($client in $normalizedClients) {
            $refreshResults += Invoke-Lifecycle $client 'refresh' -ChildNoBuild:($NoBuild -or -not $first)
            $first = $false
        }
        if (@($refreshResults | Where-Object { -not $_.ok }).Count -gt 0) {
            Write-Receipt 'blocked' $refreshResults 'Refresh failed; no client was started.' | Out-Null
            exit 1
        }

        $preflightResults = Invoke-All 'preflight'
        $blocked = @($preflightResults | Where-Object { -not $_.ok })
        if ($blocked.Count -gt 0) {
            Write-Receipt 'blocked' ($refreshResults + $preflightResults) 'Preflight failed for at least one client; no client was started.' | Out-Null
            exit 3
        }

        $started = @()
        $startResults = @()
        try {
            foreach ($client in $normalizedClients) {
                $result = Invoke-Lifecycle $client 'start' -ChildNoBuild
                $startResults += $result
                if (-not $result.ok) { throw "client$client failed to start" }
                $started += $client
            }
            Write-Receipt 'started' ($refreshResults + $preflightResults + $startResults) 'All selected clients started; use MCP telemetry and bounded mailbox commands, then run stop.' | Out-Null
        } catch {
            Write-Host "partial start detected: $($_.Exception.Message)"
            foreach ($client in @($started | Select-Object -Reverse)) {
                try { $null = Invoke-Lifecycle $client 'stop' }
                catch { Write-Host "cleanup failed for client${client}: $($_.Exception.Message)" }
            }
            Write-Receipt 'cleanup_after_start_failure' ($refreshResults + $preflightResults + $startResults) 'A partial start was stopped before returning failure.' | Out-Null
            exit 1
        }
        break
    }
    'status' {
        $results = Invoke-All 'status'
        Write-Receipt 'observed' $results 'Status is observational; no lifecycle mutation was requested.' | Out-Null
        break
    }
    'stop' {
        $results = Invoke-All 'stop'
        $failed = @($results | Where-Object { -not $_.ok })
        Write-Receipt (Select-ScenarioResult $failed.Count 'stopped' 'cleanup_failed') $results (Select-ScenarioNote $failed.Count 'All selected clients are closed.' 'At least one client did not return a green closure result.') | Out-Null
        if ($failed.Count -gt 0) { exit 1 }
        break
    }
    'capture' {
        $results = Invoke-All 'capture'
        $failed = @($results | Where-Object { -not $_.ok })
        Write-Receipt (Select-ScenarioResult $failed.Count 'captured' 'capture_incomplete') $results 'Each client capture runs only after its container is closed.' | Out-Null
        if ($failed.Count -gt 0) { exit 1 }
        break
    }
}
