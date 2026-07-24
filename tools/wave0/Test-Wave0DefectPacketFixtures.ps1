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
$suggestionJson = Join-Path $outputRoot 'suggestion.json'
$suggestionMd = Join-Path $outputRoot 'suggestion.md'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Suggest-Wave0DefectPacket.ps1') `
    -FirstReceiptJson (Join-Path $sealRoot 'first-omen-apply.receipt.json') `
    -ReversalReceiptJson (Join-Path $sealRoot 'bad-reversal-omen-apply.receipt.json') `
    -FirstAnnotatedJson (Join-Path $sealRoot 'first-omen-apply.receipt.annotated.json') `
    -ReversalAnnotatedJson (Join-Path $sealRoot 'bad-reversal-omen-apply.receipt.annotated.json') `
    -SealJson (Join-Path $sealRoot 'bad-seal.json') `
    -OutputJson $suggestionJson `
    -OutputMarkdown $suggestionMd | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'defect suggestion generator failed' }

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
$suggestion = Get-Content -LiteralPath $suggestionJson -Raw | ConvertFrom-Json
if ($suggestion.verdict -ne 'wave0_defect_packet_suggested') { throw "unexpected suggestion verdict: $($suggestion.verdict)" }
if ($suggestion.defect_kind -ne 'role_reversal_failed') { throw "unexpected suggestion kind: $($suggestion.defect_kind)" }
if ($suggestion.command -notmatch 'New-Wave0DefectPacket.ps1') { throw 'suggestion did not include defect packet command' }
if ($packet.verdict -ne 'wave0_named_defect_packet_retained') { throw "unexpected packet verdict: $($packet.verdict)" }
if ($packet.evidence_verdict -ne 'wave0_visual_evidence_not_sealed') { throw "unexpected evidence verdict: $($packet.evidence_verdict)" }
if (@($packet.artifacts | Where-Object { $_.present }).Count -lt 5) { throw 'expected all five fixture artifacts to be indexed' }
if (-not (Select-String -LiteralPath $packetMd -Pattern 'wave0-fixture-role-not-reversed' -Quiet)) { throw 'markdown did not contain defect id' }

$sealedVisual = Join-Path $outputRoot 'sealed-visual.json'
[IO.File]::WriteAllText(
    $sealedVisual,
    (([ordered]@{
        schema_version = 1
        artifact_type = 'wave0_visual_evidence_seal'
        verdict = 'wave0_visual_evidence_sealed'
        next_action = 'Fixture sealed visual receipt.'
    } | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $sealedOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\New-Wave0DefectPacket.ps1') `
        -DefectId 'wave0-fixture-should-not-retain' `
        -DefectKind other `
        -Summary 'This fixture must be rejected because visual proof is already sealed.' `
        -SealJson $sealedVisual `
        -OutputJson (Join-Path $outputRoot 'sealed-should-not-retain.json') `
        -OutputMarkdown (Join-Path $outputRoot 'sealed-should-not-retain.md') 2>&1
    $sealedExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($sealedExitCode -eq 0) {
    throw 'defect packet generator unexpectedly accepted a sealed visual-evidence receipt'
}
if ((@($sealedOutput) -join "`n") -notmatch 'refusing to retain') {
    throw 'sealed visual rejection did not explain the boundary'
}

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_defect_packet_fixture_checks_passed'
    output_directory = $outputRoot
    suggestion_json = $suggestionJson
    suggestion_markdown = $suggestionMd
    packet_json = $packetJson
    packet_markdown = $packetMd
    sealed_visual_rejection = 'passed'
}
$summaryPath = Join-Path $outputRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 6) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 defect packet fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
