<#
.SYNOPSIS
Reproduce the Workbench stale-runner ownership contract without touching Valheim.

.DESCRIPTION
This bounded Dev/Lab fixture queues only the public-safe support-export capability,
leases it as a named fixture runner, proves that a different runner receives HTTP
404 for a state update, and completes the fixture through the owning runner. It
also proves the host process gate with a harmless short-lived fixture executable
named valheim.exe. It does not execute the support exporter, game, or any client
operation. Run it against a Dev/Lab Companion with the ordinary host runner paused
or be prepared for the script to fail closed if that runner wins the lease first.
#>
[CmdletBinding()]
param(
    [string]$CompanionUrl = 'http://127.0.0.1:8080',
    [string]$ContainerName = 'lumberjacks-companion-companion-1',
    [ValidateSet('Dev', 'Lab')]
    [string]$ExpectedProfile = 'Lab'
)

$ErrorActionPreference = 'Stop'
$CompanionUrl = $CompanionUrl.TrimEnd('/')
$script:FixtureJobId = $null

function Get-Json {
    param([Parameter(Mandatory)][string]$Path, [hashtable]$Headers = @{})
    Invoke-RestMethod -Uri "$CompanionUrl$Path" -Headers $Headers -TimeoutSec 20
}

function Post-Json {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object]$Body, [hashtable]$Headers = @{})
    Invoke-RestMethod -Uri "$CompanionUrl$Path" -Method Post -Headers $Headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 12 -Compress) -TimeoutSec 20
}

function Get-HttpStatus {
    param([Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$ErrorRecord)
    try { return [int]$ErrorRecord.Exception.Response.StatusCode.value__ } catch { return 0 }
}

$projection = Get-Json -Path '/api/v1/workbench'
if ($projection.profile.effective -ne $ExpectedProfile) {
    throw "runner ownership fixture requires effective profile '$ExpectedProfile'; current profile is '$($projection.profile.effective)'."
}

$runnerSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Start-WorkbenchHostRunner.ps1') -Raw
$launcherSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Start-LocalCompanion.ps1') -Raw
if ($runnerSource -notmatch '\[switch\]\$ReplaceExisting' -or $runnerSource -notmatch '\-File\\s\+') {
    throw 'runner replacement contract is missing from Start-WorkbenchHostRunner.ps1.'
}
if ($launcherSource -notmatch "'\-ReplaceExisting'") {
    throw 'local launcher does not request runner convergence.'
}

$tokens = $null
$parseErrors = $null
$runnerAst = [Management.Automation.Language.Parser]::ParseInput(
    $runnerSource,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'host runner does not parse for process-wrapper verification.' }
$modOperationSource = $null
$captureOperationSource = $null
foreach ($functionName in @('Quote-ProcessArgument', 'Invoke-RunnerChildProcess', 'Test-ValheimHostStopped', 'Invoke-ModOperation', 'Invoke-CompanionCapture')) {
    $definition = $runnerAst.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true)
    if ($null -eq $definition) { throw "host runner is missing $functionName." }
    if ($functionName -eq 'Invoke-ModOperation') { $modOperationSource = $definition.Extent.Text }
    if ($functionName -eq 'Invoke-CompanionCapture') { $captureOperationSource = $definition.Extent.Text }
    Invoke-Expression $definition.Extent.Text
}

$renderedC6Definition = $runnerAst.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Invoke-RenderedC6'
}, $true)
if ($null -eq $renderedC6Definition) { throw 'host runner is missing Invoke-RenderedC6.' }
$renderedCommands = @($renderedC6Definition.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst]
}, $true))
$provenanceCommand = @($renderedCommands | Where-Object {
    $_.GetCommandName() -eq 'powershell.exe' -and $_.Extent.Text -match '\$provenanceGate'
})[0]
$projectionCommand = @($renderedCommands | Where-Object { $_.Extent.Text -match 'api/v1/workbench' })[0]
$i5Command = @($renderedCommands | Where-Object {
    $_.GetCommandName() -eq 'powershell.exe' -and $_.Extent.Text -match '\$i5Link'
})[0]
$orchestratorCommand = @($renderedCommands | Where-Object {
    $_.GetCommandName() -eq 'Invoke-RunnerChildProcess'
})[0]
if ($null -eq $provenanceCommand -or $null -eq $projectionCommand -or
    $null -eq $i5Command -or $null -eq $orchestratorCommand) {
    throw 'rendered C6 provenance/prelive command contract is incomplete.'
}
if ($provenanceCommand.Extent.Text -match 'AllowRetainedStateBridge') {
    throw 'rendered C6 must not admit the temporary retained-state bridge.'
}
if ($provenanceCommand.Extent.StartOffset -ge $projectionCommand.Extent.StartOffset -or
    $provenanceCommand.Extent.StartOffset -ge $i5Command.Extent.StartOffset -or
    $provenanceCommand.Extent.StartOffset -ge $orchestratorCommand.Extent.StartOffset) {
    throw 'rendered C6 provenance gate must precede projection, i5, and client orchestration.'
}
foreach ($requiredText in @(
    'fieldlab\scripts\Test-LabRuntimeProvenance.ps1',
    'rendered_prelive_lab_provenance_gate_missing',
    'rendered_prelive_lab_provenance_receipt_invalid',
    'rendered_prelive_lab_provenance_failed')) {
    if ($renderedC6Definition.Extent.Text -notmatch [regex]::Escape($requiredText)) {
        throw "rendered C6 provenance contract is missing: $requiredText"
    }
}

$zeroExit = Invoke-RunnerChildProcess `
    -JobId 'process-wrapper-fixture' `
    -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-Command', (Quote-ProcessArgument 'exit 0'))
$nonzeroExit = Invoke-RunnerChildProcess `
    -JobId 'process-wrapper-fixture' `
    -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-Command', (Quote-ProcessArgument 'exit 7'))
if ($zeroExit -ne 0 -or $nonzeroExit -ne 7) {
    throw "host runner child exit-code contract failed (zero=$zeroExit nonzero=$nonzeroExit)."
}

if (-not (Test-ValheimHostStopped)) {
    throw 'host process fixture requires Valheim and valheim_server to be stopped.'
}
$processFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('workbench-valheim-gate-' + [Guid]::NewGuid().ToString('N'))
$fixtureProcess = $null
$hostProcessGate = $false
try {
    New-Item -ItemType Directory -Force -Path $processFixtureRoot | Out-Null
    $fixtureExecutable = Join-Path $processFixtureRoot 'valheim.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\ping.exe') -Destination $fixtureExecutable
    $fixtureProcess = Start-Process -FilePath $fixtureExecutable `
        -ArgumentList @('127.0.0.1', '-n', '15') `
        -WindowStyle Hidden -PassThru
    for ($index = 0; $index -lt 20; $index++) {
        if (Get-Process -Id $fixtureProcess.Id -ErrorAction SilentlyContinue) { break }
        Start-Sleep -Milliseconds 100
    }
    $hostProcessGate = -not (Test-ValheimHostStopped)
    if (-not $hostProcessGate) { throw 'host process gate did not detect the valheim.exe negative control.' }
}
finally {
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $fixtureProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $processFixtureRoot) {
        $resolvedFixture = [IO.Path]::GetFullPath($processFixtureRoot)
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedFixture).StartsWith('workbench-valheim-gate-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
        }
    }
}
if (-not (Test-ValheimHostStopped)) { throw 'host process gate fixture did not clean up.' }

$dispatchGate = & {
    param([string]$FunctionSource)
    Invoke-Expression $FunctionSource
    $script:UnexpectedRestCall = $false
    function Test-ValheimHostStopped { return $false }
    function Invoke-RestMethod {
        $script:UnexpectedRestCall = $true
        throw 'mod process gate called the Companion despite a running host process'
    }
    $result = Invoke-ModOperation -CapabilityId 'operate.mod.install' -Inputs ([pscustomobject]@{ game_closed_confirmed = $true })
    [pscustomobject]@{
        passed = $result.verdict -eq 'failed' -and
            $result.reason -eq 'valheim_is_running_host' -and
            $result.result.host_process_gate -eq 'valheim_running' -and
            -not $script:UnexpectedRestCall
        reason = $result.reason
        unexpected_rest_call = $script:UnexpectedRestCall
    }
} $modOperationSource
if (-not $dispatchGate.passed) {
    throw "mod operation did not fail closed at the host process gate (reason=$($dispatchGate.reason), REST=$($dispatchGate.unexpected_rest_call))."
}

$captureGate = & {
    param([string]$FunctionSource)
    Invoke-Expression $FunctionSource
    function Invoke-RestMethod {
        return [pscustomobject]@{ run_id = 'no-peer-fixture'; verdict = 'no_peer_window' }
    }
    $result = Invoke-CompanionCapture -JobId 'capture-fixture' -Inputs ([pscustomobject]@{ duration_seconds = 5 })
    [pscustomobject]@{
        passed = $result.verdict -eq 'failed' -and
            $result.reason -eq 'transport_capture_no_peer_window' -and
            $result.result.verdict -eq 'no_peer_window'
        reason = $result.reason
    }
} $captureOperationSource
if (-not $captureGate.passed) {
    throw "no-peer transport capture was not classified as inconclusive (reason=$($captureGate.reason))."
}

$security = Get-Json -Path '/api/v1/workbench/security'
$browserHeaders = @{ 'X-Workbench-Token' = $security.browser_token }
$runnerToken = (& docker exec $ContainerName sh -lc 'cat /run/workbench/runner-token 2>/dev/null || cat /data/workbench/runner-token' 2>$null | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($runnerToken)) { throw "runner token unavailable from '$ContainerName'." }

$fixtureId = "ownership-fixture-$PID"
$staleId = "$fixtureId-stale"
$ownerHeaders = @{ 'X-Workbench-Runner-Token' = $runnerToken; 'X-Workbench-Runner-Id' = $fixtureId }
$staleHeaders = @{ 'X-Workbench-Runner-Token' = $runnerToken; 'X-Workbench-Runner-Id' = $staleId }

$created = Post-Json -Path '/api/v1/workbench/capabilities/recover.support.export/jobs' -Headers $browserHeaders -Body @{ target = 'local'; inputs = @{} }
$script:FixtureJobId = $created.job_id
$next = Get-Json -Path '/api/v1/workbench/runner/jobs/next' -Headers $ownerHeaders
if (-not $next.job) { throw 'runner ownership fixture was not leased; another runner may have claimed it.' }
if ($next.job.job_id -ne $script:FixtureJobId -or $next.job.runner_id -ne $fixtureId) {
    throw "runner ownership fixture lost its lease to '$($next.job.runner_id)' (job '$($next.job.job_id)')."
}

$staleStatus = 0
try {
    Post-Json -Path ("/api/v1/workbench/runner/jobs/{0}/events" -f [Uri]::EscapeDataString($script:FixtureJobId)) -Headers $staleHeaders -Body @{ state = 'running'; reason_code = 'stale_runner_should_be_rejected' } | Out-Null
} catch {
    $staleStatus = Get-HttpStatus $_
}
if ($staleStatus -ne 404) { throw "expected stale runner event HTTP 404, got HTTP $staleStatus." }

$initialLease = [DateTimeOffset]$next.job.lease_expires_utc
Start-Sleep -Milliseconds 100
$renewed = Post-Json -Path ("/api/v1/workbench/runner/jobs/{0}/events" -f [Uri]::EscapeDataString($script:FixtureJobId)) -Headers $ownerHeaders -Body @{ state = 'running'; reason_code = 'ownership_fixture_running' }
$renewedLease = [DateTimeOffset]$renewed.lease_expires_utc
if ($renewedLease -le $initialLease) { throw 'owning runner event did not renew the active job lease.' }
$completed = Post-Json -Path ("/api/v1/workbench/runner/jobs/{0}/complete" -f [Uri]::EscapeDataString($script:FixtureJobId)) -Headers $ownerHeaders -Body @{ verdict = 'passed'; result = @{ fixture = 'runner_ownership'; executed = $false }; reason_code = 'ownership_fixture_complete' }
$receipt = Get-Json -Path ("/api/v1/workbench/jobs/{0}/receipt" -f [Uri]::EscapeDataString($script:FixtureJobId))

[ordered]@{
    schema_version = 1
    verdict = if ($completed.state -eq 'succeeded' -and $staleStatus -eq 404) { 'passed' } else { 'failed' }
    profile = $projection.profile.effective
    job_id = $script:FixtureJobId
    fixture_runner = $fixtureId
    stale_runner_http = $staleStatus
    receipt_reason = $receipt.reason_code
    executed_external_operation = $false
    replace_existing_contract = $true
    active_lease_renewed = $renewedLease -gt $initialLease
    child_exit_code_contract = $zeroExit -eq 0 -and $nonzeroExit -eq 7
    host_process_gate = $hostProcessGate
    host_process_dispatch_gate = $dispatchGate.passed
    no_peer_capture_fails_closed = $captureGate.passed
} | ConvertTo-Json -Depth 10

if ($completed.state -ne 'succeeded' -or $staleStatus -ne 404) { exit 1 }
