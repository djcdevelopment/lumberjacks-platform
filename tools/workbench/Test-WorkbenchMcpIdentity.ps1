<#
.SYNOPSIS
Read-only provenance gate for the Baseline Dev/Lab MCP endpoint.

.DESCRIPTION
Health and port reachability do not identify a project. This check asks the
authenticated `/identity` route for the runtime's project, profile, published
port, provider set, source/image metadata, caller registry, and ledger path.
It fails closed with a machine-readable verdict and never stops a listener.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Dev','Lab')]
    [string]$Profile = 'Dev',
    [ValidateRange(1024,65535)]
    [int]$McpPort = 8721,
    [string]$BaseUrl = '',
    [string]$ApiKey = 'comfy-dev-local',
    [string]$ExpectedSourceRevision = '',
    [string]$ExpectedImage = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = "http://127.0.0.1:$McpPort"
}
$BaseUrl = $BaseUrl.TrimEnd('/')

$result = [ordered]@{
    schema_version = 1
    expected = [ordered]@{
        project = 'baseline'
        profile = $Profile
        published_port = $McpPort
        required_provider = 'comfy_gateway.toolsurface.workbench'
        source_revision = if ($ExpectedSourceRevision) { $ExpectedSourceRevision } else { $null }
        image = if ($ExpectedImage) { $ExpectedImage } else { $null }
    }
    endpoint = "$BaseUrl/identity"
    identity = $null
    verdict = $null
    error = $null
}

try {
    $headers = @{ 'X-Comfy-Key' = $ApiKey }
    $identity = Invoke-RestMethod -Uri $result.endpoint -Headers $headers -Method Get -TimeoutSec 5
    $result.identity = $identity
} catch {
    $result.verdict = 'mcp_endpoint_unavailable'
    $result.error = $_.Exception.Message
    $result | ConvertTo-Json -Depth 10
    exit 2
}

$mismatches = @()
if ($identity.schema -ne 'baseline.mcp.identity.v1') { $mismatches += 'schema' }
if ($identity.project -ne 'baseline') { $mismatches += 'project' }
if ($identity.profile -ne $Profile) { $mismatches += 'profile' }
if ([int]$identity.published_port -ne $McpPort) { $mismatches += 'published_port' }
if (@($identity.providers) -notcontains 'comfy_gateway.toolsurface.workbench') { $mismatches += 'required_provider' }
if ([string]::IsNullOrWhiteSpace([string]$identity.source_revision)) { $mismatches += 'source_revision_missing' }
if ([string]::IsNullOrWhiteSpace([string]$identity.source_dirty)) { $mismatches += 'source_dirty_missing' }
if ([string]::IsNullOrWhiteSpace([string]$identity.image)) { $mismatches += 'image_missing' }
if ([string]::IsNullOrWhiteSpace([string]$identity.caller_registry)) { $mismatches += 'caller_registry_missing' }
if ([string]::IsNullOrWhiteSpace([string]$identity.ledger_dir)) { $mismatches += 'ledger_dir_missing' }
if ($ExpectedSourceRevision -and $identity.source_revision -ne $ExpectedSourceRevision) { $mismatches += 'source_revision' }
if ($ExpectedImage -and $identity.image -ne $ExpectedImage) { $mismatches += 'image' }

if ($mismatches.Count -gt 0) {
    $result.verdict = 'mcp_endpoint_identity_mismatch'
    $result.error = ($mismatches -join ',')
    $result.mismatches = $mismatches
    $result | ConvertTo-Json -Depth 10
    exit 2
}

$result.verdict = 'passed'
$result | ConvertTo-Json -Depth 10
exit 0
