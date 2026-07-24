<#
.SYNOPSIS
Verify the public Wave 0 roadmap summary matches live P7 release truth.

.DESCRIPTION
Reads the local living roadmap source and generated HTML, then compares their
current release text with live P7 modpack, Gateway deployment, and Companion
bootstrap manifests. By default it also checks the public /roadmap body. This is
a freshness guard: it does not deploy, rebuild, install files, or move players.
#>
[CmdletBinding()]
param(
    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',
    [string]$OutputJson = 'captures/wave0-roadmap-freshness.json',
    [switch]$SkipPublicRoadmap
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$roadmapJsonPath = Join-Path $repoRoot 'Lumberjacks/docs/roadmap/valheim-volunteer-roadmap.json'
$roadmapHtmlPath = Join-Path $repoRoot 'Lumberjacks/src/Game.Gateway/Community/roadmap.html'
$gatewayRoot = $GatewayUrl.TrimEnd('/')
$httpTimeoutSeconds = 15

function Get-Json {
    param([string]$Url)
    Invoke-RestMethod -Uri $Url -Method Get -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $httpTimeoutSeconds
}

function New-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [ordered]@{
        name = $Name
        ok = $Ok
        detail = $Detail
    }
}

function Has-Text {
    param([string]$Haystack, [string]$Needle)
    if ([string]::IsNullOrWhiteSpace($Needle)) { return $false }
    return $Haystack.Contains($Needle)
}

$roadmap = Get-Content -LiteralPath $roadmapJsonPath -Raw | ConvertFrom-Json
$html = Get-Content -LiteralPath $roadmapHtmlPath -Raw
$modManifest = Get-Json "$gatewayRoot/api/v0/valheim/modpack/manifest"
$deployment = Get-Json "$gatewayRoot/api/v0/telemetry/deployment"
$bootstrap = Get-Json "$gatewayRoot/api/v0/companion/bootstrap/manifest"
$publicRoadmap = if ($SkipPublicRoadmap) { $null } else { (Invoke-WebRequest -Uri "$gatewayRoot/roadmap" -UseBasicParsing -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $httpTimeoutSeconds).Content }

$expectedRelease = [string]$modManifest.mod_release
$expectedPackageSha = [string]$modManifest.package.sha256
$expectedGateway = [string]$deployment.lumberjacks_version
$expectedBootstrap = [string]$bootstrap.release
$expectedBootstrapShort = $expectedBootstrap -replace '^companion-bootstrap-20260723-', ''
$focusText = [string]::Join("`n", @($roadmap.current_focus))

$checks = @()
$checks += New-Check 'roadmap-current-release-id' `
    ([string]$roadmap.current_release.release_id -eq $expectedRelease) `
    ("roadmap={0}; p7_modpack={1}" -f $roadmap.current_release.release_id, $expectedRelease)
$checks += New-Check 'roadmap-gateway-image-id' `
    ([string]$roadmap.current_release.gateway_image_release_id -eq $expectedGateway) `
    ("roadmap={0}; p7_gateway={1}" -f $roadmap.current_release.gateway_image_release_id, $expectedGateway)
$checks += New-Check 'roadmap-admitted-mod-release' `
    ([string]$roadmap.current_release.gateway_admits_mod_release -eq $expectedRelease) `
    ("roadmap={0}; p7_modpack={1}" -f $roadmap.current_release.gateway_admits_mod_release, $expectedRelease)
$checks += New-Check 'current-focus-names-runtime-release' `
    (Has-Text $focusText $expectedRelease) `
    ("expected current_focus to mention {0}" -f $expectedRelease)
$checks += New-Check 'current-focus-names-bootstrap-release' `
    ((Has-Text -Haystack $focusText -Needle $expectedBootstrap) -or (Has-Text -Haystack $focusText -Needle $expectedBootstrapShort)) `
    ("expected current_focus to mention {0}" -f $expectedBootstrap)
$checks += New-Check 'rendered-html-names-runtime-release' `
    (Has-Text $html $expectedRelease) `
    ("expected rendered HTML to mention {0}" -f $expectedRelease)
$checks += New-Check 'rendered-html-names-bootstrap-release' `
    ((Has-Text -Haystack $html -Needle $expectedBootstrap) -or (Has-Text -Haystack $html -Needle $expectedBootstrapShort)) `
    ("expected rendered HTML to mention {0}" -f $expectedBootstrap)

if (-not $SkipPublicRoadmap) {
    $checks += New-Check 'public-roadmap-names-runtime-release' `
        (Has-Text $publicRoadmap $expectedRelease) `
        ("expected public /roadmap to mention {0}" -f $expectedRelease)
    $checks += New-Check 'public-roadmap-names-bootstrap-release' `
        ((Has-Text -Haystack $publicRoadmap -Needle $expectedBootstrap) -or (Has-Text -Haystack $publicRoadmap -Needle $expectedBootstrapShort)) `
        ("expected public /roadmap to mention {0}" -f $expectedBootstrap)
}

$failed = @($checks | Where-Object { -not $_.ok })
$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($failed.Count -eq 0) { 'wave0_roadmap_freshness_passed' } else { 'wave0_roadmap_freshness_failed' }
    expected_release = $expectedRelease
    expected_package_sha256 = $expectedPackageSha
    expected_gateway = $expectedGateway
    expected_companion_bootstrap = $expectedBootstrap
    checked_public_roadmap = -not $SkipPublicRoadmap
    roadmap_json = $roadmapJsonPath
    roadmap_html = $roadmapHtmlPath
    checks = $checks
    failed_checks = @($failed | ForEach-Object { $_.name })
}

if ($OutputJson) {
    $path = [IO.Path]::GetFullPath($OutputJson)
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($path, ($receipt | ConvertTo-Json -Depth 20) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

Write-Host ("Wave 0 roadmap freshness: {0}" -f $receipt.verdict)
foreach ($check in $checks) {
    $mark = if ($check.ok) { 'OK' } else { 'FAIL' }
    Write-Host ("[{0}] {1} - {2}" -f $mark, $check.name, $check.detail)
}
if ($OutputJson) { Write-Host ("Receipt JSON: {0}" -f ([IO.Path]::GetFullPath($OutputJson))) }

if ($failed.Count -gt 0) { exit 1 }
