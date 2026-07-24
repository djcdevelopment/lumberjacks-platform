<#
.SYNOPSIS
Validate that the Wave 0 human-test register still names the required return gates.

.DESCRIPTION
This is a lightweight documentation contract check. It keeps the "what Derek must
test when back" list from drifting out of the Wave 0 pre-live lane. The script
does not contact P7, OMEN, or i5 and does not mutate runtime state.
#>
[CmdletBinding()]
param(
    [string]$RegisterPath = 'plans/remaining-human-tests.md',
    [string]$OutputJson = 'captures/wave0-human-test-register.json'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function New-Check {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail
    )

    [ordered]@{
        name = $Name
        ok = $Ok
        detail = $Detail
    }
}

$registerFullPath = Resolve-UnderRepo $RegisterPath
$outputFullPath = Resolve-UnderRepo $OutputJson
$outputDir = Split-Path -Parent $outputFullPath
if ($outputDir) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

$checks = @()
if (-not (Test-Path -LiteralPath $registerFullPath -PathType Leaf)) {
    $checks += New-Check 'register_exists' $false "missing: $registerFullPath"
    $text = ''
} else {
    $text = Get-Content -LiteralPath $registerFullPath -Raw
    $checks += New-Check 'register_exists' $true $registerFullPath
}

foreach ($id in @('H0-1', 'H0-2', 'H0-3', 'H0-4')) {
    $checks += New-Check "contains_$id" ($text -match [regex]::Escape("| $id |")) "required current Wave 0 gate $id"
}

$requiredPatterns = @(
    'tools\wave0\Test-Wave0Prelive.ps1',
    'tools\wave0\New-Wave0ExpectedResultGrid.ps1',
    'tools\wave0\Wait-Wave0LiveGate.ps1',
    'Add-Wave0VisualObservation.ps1',
    'Seal-Wave0VisualEvidence.ps1',
    'Suggest-Wave0DefectPacket.ps1',
    'captures\wave0-prelive-current\return-packet\packet.md',
    'captures\wave0-prelive-current\expected-result-grid.md'
)
foreach ($pattern in $requiredPatterns) {
    $checks += New-Check ("mentions_" + ($pattern -replace '[^A-Za-z0-9]+', '_').Trim('_')) ($text.Contains($pattern)) "required reference: $pattern"
}

$forbiddenPatterns = @(
    'send me a key',
    'paste me',
    'copy the dll',
    'manually update the config'
)
foreach ($pattern in $forbiddenPatterns) {
    $checks += New-Check ("avoids_" + ($pattern -replace '[^A-Za-z0-9]+', '_').Trim('_')) (-not $text.ToLowerInvariant().Contains($pattern)) "forbidden manual-loop phrase: $pattern"
}

$failed = @($checks | Where-Object { -not $_.ok })
$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($failed.Count -eq 0) { 'wave0_human_test_register_current' } else { 'wave0_human_test_register_incomplete' }
    register_path = $registerFullPath
    checks = $checks
}

[IO.File]::WriteAllText($outputFullPath, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 human-test register: {0}" -f $receipt.verdict)
Write-Host ("Receipt JSON: {0}" -f $outputFullPath)
if ($failed.Count -gt 0) {
    Write-Host 'Failed checks:'
    foreach ($check in $failed) { Write-Host ("- {0}: {1}" -f $check.name, $check.detail) }
    exit 1
}
