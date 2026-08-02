<#
.SYNOPSIS
Check the local Workbench HTML contract without requiring a browser session.

.DESCRIPTION
This bounded structural check proves that the local control surface publishes its
compatibility routes, responsive viewport, intent navigation, presentation toggle,
job evidence links, privacy posture, and waiting-human observation flow. It does
not claim that a novice found the page usable or that the mobile layout was
visually comfortable; those remain human observations.
#>
[CmdletBinding()]
param(
    [string]$CompanionUrl = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
$CompanionUrl = $CompanionUrl.TrimEnd('/')
$failures = [Collections.Generic.List[string]]::new()

function Assert-That {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { $failures.Add($Message) }
}

function Get-Page {
    param([Parameter(Mandatory)][string]$Path)
    try {
        $response = Invoke-WebRequest -Uri "$CompanionUrl$Path" -UseBasicParsing -TimeoutSec 20
        Assert-That ($response.StatusCode -eq 200) "$Path returned HTTP $($response.StatusCode)"
        $contentType = [string]$response.Headers['Content-Type']
        Assert-That ($contentType -match '(?i)text/html\s*;\s*charset=utf-8') "$Path must declare UTF-8 HTML (received '$contentType')"
        return [string]$response.Content
    } catch {
        $failures.Add("$Path request failed: $($_.Exception.Message)")
        return ''
    }
}

$pages = @{}
foreach ($path in @('/', '/workbench', '/companion')) { $pages[$path] = Get-Page $path }
$html = $pages['/']
Assert-That (-not [string]::IsNullOrWhiteSpace($html)) 'Workbench root returned no HTML'
Assert-That ($html -notmatch '[\u00C2\u00C3\u00E2\uFFFD]') 'Workbench HTML contains mojibake characters'
Assert-That ($html -match '<meta[^>]+name="viewport"[^>]+content="width=device-width') 'responsive viewport contract missing'
foreach ($section in @('home', 'explore', 'build', 'operate', 'recover', 'community')) {
    Assert-That ($html -match ('id="{0}"' -f $section)) "intent section missing: $section"
}
Assert-That ($html -match 'id="mode"') 'Standard/Advanced mode control missing'
Assert-That ($html -match 'toggleMode\(\)') 'presentation toggle handler missing'
Assert-That ($html -match 'baseline\.workbench\.mode') 'presentation preference storage missing'
Assert-That ($html -match 'job-evidence') 'job evidence link decorator missing'
Assert-That ($html -match '/api/v1/workbench/jobs/.+/(events|receipt)') 'job evidence URL contract missing'
Assert-That ($html -match 'waiting_human') 'waiting-human state contract missing'
Assert-That ($html -match 'Record observation') 'human observation action missing'
Assert-That ($html -match 'Privacy:') 'privacy posture presentation missing'
Assert-That ($html -match 'Rendered client A') 'Story topology label for first rendered client missing'
Assert-That ($html -match 'Rendered client B') 'Story topology label for second rendered client missing'
Assert-That ($html -match 'applyStoryTopology') 'Story/Advanced topology presentation adapter missing'
Assert-That ($html -match 'First visit: start safely') 'first-visit orientation path missing'
Assert-That ($html -match 'Inspect system.*read-only') 'first-visit safe action is not explained'
Assert-That ($html -match 'Factory reset is intentionally separate') 'first-visit recovery boundary missing'
Assert-That ($html -match 'applyStorySurface') 'Story/Advanced capability target adapter missing'
Assert-That ($html -match 'No reversible Workbench install is currently active') 'legacy rollback unavailability guidance missing'
Assert-That ($html -match 'capabilityReason') 'capability reason presentation adapter missing'
Assert-That (([regex]::Matches($html, 'function capabilityCard')).Count -eq 1) 'capabilityCard declaration is duplicated or missing'
Assert-That (([regex]::Matches($html, 'async function startJob')).Count -eq 1) 'startJob declaration is duplicated or missing'
Assert-That ($html -notmatch '<script[^>]+src=') 'external script dependency found in local shell'

[ordered]@{
    schema_version = 1
    verdict = if ($failures.Count -eq 0) { 'passed' } else { 'failed' }
    routes = @('/', '/workbench', '/companion')
    section_count = @('home', 'explore', 'build', 'operate', 'recover', 'community').Count
    failures = @($failures)
    visual_mobile_observation_required = $true
} | ConvertTo-Json -Depth 8

if ($failures.Count -gt 0) { exit 1 }
