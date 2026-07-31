#Requires -Version 5.1
<#
.SYNOPSIS
Run C7's four physical-client Steam-free fail-closed cells on OMEN.

.DESCRIPTION
The positive composition proof uses both OMEN and i5. These negative cells use
one real client because they test admission/bootstrap rejection before a player
can join: invalid enrollment, unavailable Gateway, incompatible release, and
wrong world descriptor. Every cell runs with native poison armed and no
Valheim +connect target. Server gates are restored in finally.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseRunId,

    [string] $Server = '100.116.82.60:2456',

    [string] $GatewayUrl = 'http://127.0.0.1:4000',

    [string] $UnavailableGatewayUrl = 'http://127.0.0.1:45999',

    [string] $ServerGatewayUrl = 'http://100.124.12.37:4000',

    [string] $Character = 'Tugcorp',

    [ValidateRange(60, 300)]
    [int] $WaitSeconds = 90,

    [string] $DllPath = '',

    [string] $EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$clientHarness = Join-Path $PSScriptRoot 'Invoke-NativeValheimClient.ps1'
$serverControl = Join-Path $PSScriptRoot 'Invoke-ValheimServerRuntimeControl.ps1'
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $repoRoot `
        'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repoRoot 'fieldlab\runs\native-valheim'
}
if ($BaseRunId.Length -gt 36 -or
    $BaseRunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'BaseRunId must be a safe token no longer than 36 characters.'
}

$baseDirectory = Join-Path $EvidenceRoot $BaseRunId
New-Item -ItemType Directory -Path $baseDirectory -Force | Out-Null
$controls = New-Object System.Collections.Generic.List[object]
$armed = New-Object System.Collections.Generic.List[string]
$oldGatewayUrl = $null
$completed = $false

function Write-JsonAtomic([string] $Path, [object] $Value) {
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllText(
        $temporary,
        ($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Set-ServerValue(
    [string] $Setting,
    [string] $Value,
    [string] $Suffix) {
    $output = & $serverControl `
        -Setting $Setting `
        -Value $Value `
        -RequestId "$BaseRunId-$Suffix"
    if (-not $?) {
        throw "Server runtime control failed: $Setting=$Value"
    }
    $receipt = (($output -join [Environment]::NewLine) | ConvertFrom-Json)
    [void]$controls.Add($receipt)
    return $receipt
}

function Invoke-Cell(
    [string] $Name,
    [string] $Action,
    [string] $ClientGatewayUrl,
    [string] $ColdJoinFailureMode = '',
    [string] $WorldDescriptorFault = '') {
    $runId = "$BaseRunId-$Name"
    [void](Set-ServerValue `
        'nativeNetworkEvidenceRunId' $runId "run-$Name")
    $arguments = @{
        Action = $Action
        Client = 'omen'
        Character = $Character
        Server = $Server
        GatewayUrl = $ClientGatewayUrl
        RunId = $runId
        DllPath = $DllPath
        EvidenceRoot = $EvidenceRoot
        WaitSeconds = $WaitSeconds
        HoldSeconds = 0
        EnableRoutedRpcCutover = $true
        EnableZdoJournalCutover = $true
        EnableZdoJournalCanonicalSession = $true
        EnableOwnershipLeaseCutover = $true
        EnableWorldZoneCutover = $true
        EnableMotionAuthorityCutover = $true
        EnableSteamFreeColdJoin = $true
        ColdJoinFailureMode = $ColdJoinFailureMode
        WorldDescriptorFault = $WorldDescriptorFault
    }
    & $clientHarness @arguments
    if (-not $?) { throw "C7 negative cell failed: $Name" }
    return [ordered]@{
        name = $Name
        run_id = $runId
        lifecycle = Join-Path (Join-Path $EvidenceRoot $runId) `
            'omen\lifecycle.json'
    }
}

try {
    & $clientHarness -Action stop -Client omen | Out-Null

    $composeDirectory = Join-Path $repoRoot 'Lumberjacks\infra\docker'
    Push-Location $composeDirectory
    try {
        & docker compose -p lumberjacks-local up -d --no-deps --build gateway
        if ($LASTEXITCODE -ne 0) {
            throw 'Local Gateway deployment failed.'
        }
    } finally {
        Pop-Location
    }

    [void](Set-ServerValue `
        'nativeNetworkEvidenceRunId' $BaseRunId 'run-base')
    [void](Set-ServerValue `
        'directControlCutoverEnabled' 'true' 'direct-arm')
    [void]$armed.Add('directControlCutoverEnabled')

    $gatewayReceipt =
        Set-ServerValue 'lumberjacksGatewayUrl' $ServerGatewayUrl 'gateway'
    $oldGatewayUrl = [string]$gatewayReceipt.old_value

    foreach ($gate in @(
        'routedRpcCutoverEnabled',
        'zdoJournalCutoverEnabled',
        'zdoJournalCanonicalSessionEnabled',
        'ownershipLeaseCutoverEnabled',
        'worldZoneCutoverEnabled',
        'motionAuthorityCutoverEnabled',
        'logicalPeerCutoverEnabled')) {
        [void](Set-ServerValue $gate 'true' ($gate + '-arm'))
        [void]$armed.Add($gate)
        if ($gate -eq 'routedRpcCutoverEnabled') {
            Start-Sleep -Seconds 3
        }
    }

    $cells = @()
    $cells += Invoke-Cell `
        'invalid-enrollment' `
        'cold-join-failure-smoke' `
        $GatewayUrl `
        'invalid_enrollment'
    $cells += Invoke-Cell `
        'gateway-unavailable' `
        'cold-join-failure-smoke' `
        $UnavailableGatewayUrl `
        'gateway_unavailable'
    $cells += Invoke-Cell `
        'wrong-release' `
        'descriptor-smoke' `
        $GatewayUrl `
        '' `
        'wrong_release'
    $cells += Invoke-Cell `
        'wrong-descriptor' `
        'descriptor-smoke' `
        $GatewayUrl `
        '' `
        'wrong_protocol'

    $summaryOutput =
        & (Join-Path $PSScriptRoot 'Write-C7ColdJoinNegativeSummary.ps1') `
            -BaseRunDirectory $baseDirectory `
            -BaseRunId $BaseRunId `
            -EvidenceRoot $EvidenceRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'C7 fail-closed cells did not satisfy the reducer.'
    }
    $summaryOutput | Write-Host
    $completed = $true
} finally {
    & $clientHarness -Action stop -Client omen | Out-Null
    for ($index = $armed.Count - 1; $index -ge 0; $index--) {
        $gate = $armed[$index]
        try {
            [void](Set-ServerValue $gate 'false' ($gate + '-disarm'))
        } catch {
            Write-Warning "Server cleanup failed for $gate`: $($_.Exception.Message)"
        }
    }
    if ($null -ne $oldGatewayUrl) {
        try {
            [void](Set-ServerValue `
                'lumberjacksGatewayUrl' $oldGatewayUrl 'gateway-restore')
        } catch {
            Write-Warning "Server Gateway URL restore failed: $($_.Exception.Message)"
        }
    }
    Write-JsonAtomic `
        (Join-Path $baseDirectory 'server-runtime-controls.json') `
        ([ordered]@{
            schema_version = 1
            receipt_type = 'c7_negative_server_runtime_controls'
            generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
            run_id = $BaseRunId
            completed = $completed
            receipts = $controls.ToArray()
        })
}

if (-not $completed) { exit 1 }
