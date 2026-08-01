<#
.SYNOPSIS
Contract smoke test for the local Workbench v1 HTTP surface.

.DESCRIPTION
This is a bounded local fixture test. It proves browser-token mutation, profile and
target eligibility, durable jobs/events/receipts, and runner authentication without
starting Valheim, touching Steam, or calling a remote Gateway.
#>
[CmdletBinding()]
param(
    [string]$CompanionUrl = 'http://127.0.0.1:8080',
    [string]$ContainerName = 'lumberjacks-companion-companion-1'
)

$ErrorActionPreference = 'Stop'
$CompanionUrl = $CompanionUrl.TrimEnd('/')
$script:Failures = [Collections.Generic.List[string]]::new()

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { $script:Failures.Add($Message) }
}

function Get-Json {
    param([string]$Path, [hashtable]$Headers = @{})
    Invoke-RestMethod -Uri "$CompanionUrl$Path" -Headers $Headers -TimeoutSec 15
}

function Post-Json {
    param([string]$Path, [object]$Body, [hashtable]$Headers = @{})
    $params = @{
        Uri = "$CompanionUrl$Path"
        Method = 'Post'
        Headers = $Headers
        ContentType = 'application/json'
        Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
        TimeoutSec = 20
    }
    Invoke-RestMethod @params
}

function Expect-HttpStatus {
    param([scriptblock]$Action, [int]$Expected)
    try {
        & $Action | Out-Null
        $script:Failures.Add("expected HTTP $Expected but request succeeded")
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        Assert-That ($status -eq $Expected) "expected HTTP $Expected, got HTTP $status"
    }
}

$security = Get-Json '/api/v1/workbench/security'
Assert-That (-not [string]::IsNullOrWhiteSpace($security.browser_token)) 'browser token missing'
$browserHeaders = @{ 'X-Workbench-Token' = $security.browser_token }
Expect-HttpStatus { Post-Json '/api/v1/workbench/installation/claim' @{ label = 'contract-unauthenticated' } } 403
$crossSiteHeaders = @{ 'X-Workbench-Token' = $security.browser_token; Origin = 'https://evil.example'; 'Sec-Fetch-Site' = 'cross-site' }
Expect-HttpStatus { Post-Json '/api/v1/workbench/installation/claim' @{ label = 'contract-cross-site' } $crossSiteHeaders } 403

$projection = Get-Json '/api/v1/workbench'
Assert-That ($projection.schema_version -eq 1) 'projection schema missing'
Assert-That ($projection.capabilities.Count -eq 11) 'capability registry count changed unexpectedly'
Assert-That ($projection.privacy.browser_binding -eq 'loopback_only') 'privacy projection missing loopback binding'
Assert-That ($projection.topology.nodes.Count -ge 8) 'topology is missing declared nodes'
Assert-That (@($projection.topology.nodes | Where-Object id -eq 'gateway').Count -eq 1) 'topology is missing Gateway node'

Expect-HttpStatus { Post-Json '/api/v1/workbench/capabilities/explore.system.inspect/jobs' @{ target = 'local'; inputs = @{ arbitrary_command = 'whoami' } } $browserHeaders } 400
Expect-HttpStatus { Post-Json '/api/v1/workbench/capabilities/recover.recreate.verify/jobs' @{ target = 'local'; inputs = @{} } $browserHeaders } 400
Expect-HttpStatus { Get-Json '/api/v1/workbench/jobs/invalid%20id' } 404

$badTarget = $null
try { Post-Json '/api/v1/workbench/capabilities/explore.system.inspect/jobs' @{ target = 'not-a-target'; inputs = @{} } $browserHeaders | Out-Null }
catch { $badTarget = $_.Exception.Response.StatusCode.value__ }
Assert-That ($badTarget -eq 400) 'ineligible target was accepted'

foreach ($capability in @('explore.system.inspect', 'explore.evidence.list')) {
    $created = Post-Json ("/api/v1/workbench/capabilities/{0}/jobs" -f $capability) @{ target = 'local'; inputs = @{} } $browserHeaders
    Assert-That (-not [string]::IsNullOrWhiteSpace($created.job_id)) "$capability did not return a job"
    $job = Get-Json ("/api/v1/workbench/jobs/{0}" -f $created.job_id)
    Assert-That ($job.state -eq 'succeeded') "$capability did not complete synchronously"
    $events = Get-Json ("/api/v1/workbench/jobs/{0}/events" -f $created.job_id)
    Assert-That ($events.events.Count -ge 2) "$capability event stream is incomplete"
    $receipt = Get-Json ("/api/v1/workbench/jobs/{0}/receipt" -f $created.job_id)
    Assert-That ($receipt.job_id -eq $created.job_id) "$capability receipt is not addressable"
}

$runnerToken = (& docker exec $ContainerName sh -lc 'cat /run/workbench/runner-token 2>/dev/null || cat /data/workbench/runner-token' 2>$null | Out-String).Trim()
Assert-That (-not [string]::IsNullOrWhiteSpace($runnerToken)) 'runner token unavailable'
$runnerHeaders = @{ 'X-Workbench-Runner-Token' = $runnerToken; 'X-Workbench-Runner-Id' = 'contract-runner' }
Expect-HttpStatus { Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/runner/jobs/next" -TimeoutSec 15 } 403
$heartbeat = Post-Json '/api/v1/workbench/runner/heartbeat' @{ runner_version = 'contract-test'; docker_ready = $true; observed_utc = [DateTimeOffset]::UtcNow.ToString('O') } $runnerHeaders
Assert-That ($heartbeat.ok -eq $true) 'authenticated runner heartbeat rejected'

$result = [ordered]@{
    schema_version = 1
    verdict = if ($script:Failures.Count -eq 0) { 'passed' } else { 'failed' }
    failures = @($script:Failures)
    installation_id = $projection.installation.installation_id
    effective_profile = $projection.profile.effective
}
$result | ConvertTo-Json -Depth 8
if ($script:Failures.Count -gt 0) { exit 1 }
