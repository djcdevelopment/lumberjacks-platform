<#
.SYNOPSIS
Fixture-test the Wave 0 expected-result grid command/content contract.

.DESCRIPTION
Generates the grid without live systems and verifies the operator handoff fields
needed before a two-client run:

- five expected phases are present in order;
- command surface includes prelive, first direction, first annotation, reversal,
  reversal annotation, seal, and named-defect suggestion;
- Markdown avoids copy/paste framing and contains the annotation commands.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-expected-result-grid-fixtures'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

$gridJson = Join-Path $outRoot 'expected-result-grid.json'
$gridMd = Join-Path $outRoot 'expected-result-grid.md'

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\New-Wave0ExpectedResultGrid.ps1') `
    -OutputJson $gridJson `
    -OutputMarkdown $gridMd | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'expected-result grid generation failed' }

$body = Get-Content -LiteralPath $gridJson -Raw | ConvertFrom-Json
$markdown = Get-Content -LiteralPath $gridMd -Raw

if ([string]$body.verdict -ne 'wave0_expected_result_grid_ready') { throw "unexpected verdict: $($body.verdict)" }

$expectedPhases = @('preflight', 'join', 'first_direction', 'role_reversal', 'seal_or_defect')
$actualPhases = @($body.rows | ForEach-Object { [string]$_.phase })
if (($actualPhases -join '|') -ne ($expectedPhases -join '|')) {
    throw "phase order mismatch: actual=$($actualPhases -join ',') expected=$($expectedPhases -join ',')"
}

$requiredCommands = @(
    'prelive',
    'first_direction',
    'annotate_first_direction',
    'reversal',
    'annotate_reversal',
    'seal',
    'suggest_named_defect'
)
foreach ($commandName in $requiredCommands) {
    $value = [string]$body.commands.$commandName
    if ([string]::IsNullOrWhiteSpace($value)) { throw "missing command: $commandName" }
}

$requiredMarkdown = @(
    'Add-Wave0VisualObservation.ps1',
    'Seal-Wave0VisualEvidence.ps1',
    'Suggest-Wave0DefectPacket.ps1',
    'Agent records observed values after first direction',
    'Agent records observed values after role reversal'
)
foreach ($text in $requiredMarkdown) {
    if ($markdown -notmatch [regex]::Escape($text)) { throw "markdown missing: $text" }
}

$forbidden = @('Derek can copy', 'copy or annotate')
foreach ($text in $forbidden) {
    if ($markdown -match [regex]::Escape($text)) { throw "markdown contains forbidden text: $text" }
}

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_expected_result_grid_fixture_checks_passed'
    output_directory = $outRoot
    grid_json = $gridJson
    grid_markdown = $gridMd
    phases = $actualPhases
    command_count = $requiredCommands.Count
}
$summaryPath = Join-Path $outRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 expected-result grid fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
