<#
.SYNOPSIS
Reproduce the Workbench stale-runner ownership contract without touching Valheim.

.DESCRIPTION
This bounded Dev/Lab fixture queues only the public-safe support-export capability,
leases it as a named fixture runner, proves that a different runner receives HTTP
404 for a state update, and completes the fixture through the owning runner. It
does not execute the support exporter or any game/client operation. Run it against
a Dev/Lab Companion with the ordinary host runner paused or be prepared for the
script to fail closed if that runner wins the lease first.
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

Post-Json -Path ("/api/v1/workbench/runner/jobs/{0}/events" -f [Uri]::EscapeDataString($script:FixtureJobId)) -Headers $ownerHeaders -Body @{ state = 'running'; reason_code = 'ownership_fixture_running' } | Out-Null
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
} | ConvertTo-Json -Depth 10

if ($completed.state -ne 'succeeded' -or $staleStatus -ne 404) { exit 1 }
