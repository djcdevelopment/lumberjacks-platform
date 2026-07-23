<#
.SYNOPSIS
Smoke-test Wave 0 named defect packet generation without Valheim clients.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-defect-packet-fixtures'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Test-Wave0SealFixtures.ps1') `
    -OutputDirectory (Join-Path $outputRoot 'seal-fixtures') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'seal fixtures did not prepare defect inputs' }

$sealRoot = Join-Path $outputRoot 'seal-fixtures'
$packetJson = Join-Path $outputRoot 'packet.json'
$packetMd = Join-Path $outputRoot 'packet.md'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\New-Wave0DefectPacket.ps1') `
    -DefectId 'wave0-fixture-role-not-reversed' `
    -DefectKind role_reversal_failed `
    -Summary 'Fixture packet for a visual seal that failed because the apply/observe role did not reverse.' `
    -FirstReceiptJson (Join-Path $sealRoot 'first-omen-apply.receipt.json') `
    -ReversalReceiptJson (Join-Path $sealRoot 'bad-reversal-omen-apply.receipt.json') `
    -FirstAnnotatedJson (Join-Path $sealRoot 'first-omen-apply.receipt.annotated.json') `
    -ReversalAnnotatedJson (Join-Path $sealRoot 'bad-reversal-omen-apply.receipt.annotated.json') `
    -SealJson (Join-Path $sealRoot 'bad-seal.json') `
    -OutputJson $packetJson `
    -OutputMarkdown $packetMd | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'defect packet generator failed' }

$packet = Get-Content -LiteralPath $packetJson -Raw | ConvertFrom-Json
if ($packet.verdict -ne 'wave0_named_defect_packet_retained') { throw "unexpected packet verdict: $($packet.verdict)" }
if ($packet.evidence_verdict -ne 'wave0_visual_evidence_not_sealed') { throw "unexpected evidence verdict: $($packet.evidence_verdict)" }
if (@($packet.artifacts | Where-Object { $_.present }).Count -lt 5) { throw 'expected all five fixture artifacts to be indexed' }
if (-not (Select-String -LiteralPath $packetMd -Pattern 'wave0-fixture-role-not-reversed' -Quiet)) { throw 'markdown did not contain defect id' }

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_defect_packet_fixture_checks_passed'
    output_directory = $outputRoot
    packet_json = $packetJson
    packet_markdown = $packetMd
}
$summaryPath = Join-Path $outputRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 6) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 defect packet fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
