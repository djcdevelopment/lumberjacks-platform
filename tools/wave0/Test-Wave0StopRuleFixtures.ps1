<#
.SYNOPSIS
Fixture-test the Wave 0 stop-rule guard without live Valheim clients.

.DESCRIPTION
Creates isolated mock strategy and exit-artifact directories, then verifies that
Test-Wave0StopRule.ps1:

- holds the stop rule when no live exit artifact exists;
- recognizes a sealed visual observation packet;
- recognizes a retained named defect packet;
- fails when the strategy no longer names the stop rule.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-stop-rule-fixtures'
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

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Body
    )

    $dir = Split-Path -Parent $Path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Path, (($Body | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

function Write-Strategy {
    param(
        [string]$Path,
        [bool]$IncludeRule
    )

    $text = if ($IncludeRule) {
        @'
# Fixture strategy

Do not begin M1/M2 expansion work until Wave 0 has either a sealed visual
observation packet for both directions or a named blocking defect.
'@
    } else {
        @'
# Fixture strategy

Wave 0 is active, but this fixture intentionally omits the promotion stop rule.
'@
    }
    [IO.File]::WriteAllText($Path, ($text + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

function Run-Case {
    param(
        [string]$Name,
        [bool]$StrategyIncludesRule,
        [string]$ArtifactKind,
        [string]$ExpectedVerdict,
        [int]$ExpectedExitCode
    )

    $caseRoot = Join-Path $outRoot $Name
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    $strategyPath = Join-Path $caseRoot 'strategy.md'
    $sealPath = Join-Path $caseRoot 'visual-seal.json'
    $defectRoot = Join-Path $caseRoot 'defects'
    $receiptPath = Join-Path $caseRoot 'stop-rule.json'

    Write-Strategy -Path $strategyPath -IncludeRule $StrategyIncludesRule

    if ($ArtifactKind -eq 'sealed_visual') {
        Write-JsonFile -Path $sealPath -Body ([ordered]@{
            schema_version = 1
            artifact_type = 'wave0_visual_evidence_seal'
            verdict = 'wave0_visual_evidence_sealed'
        })
    } elseif ($ArtifactKind -eq 'named_defect') {
        Write-JsonFile -Path (Join-Path $defectRoot 'wave0-fixture-defect\packet.json') -Body ([ordered]@{
            schema_version = 1
            artifact_type = 'wave0_named_defect_packet'
            verdict = 'wave0_named_defect_packet_retained'
            defect_id = 'wave0-fixture-defect'
        })
    }

    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Test-Wave0StopRule.ps1') `
        -StrategyPath $strategyPath `
        -VisualSealJson $sealPath `
        -DefectRoot $defectRoot `
        -OutputJson $receiptPath 2>&1
    $exitCode = $LASTEXITCODE

    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "$Name did not write stop-rule receipt: $($output -join [Environment]::NewLine)"
    }
    $body = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ([string]$body.verdict -ne $ExpectedVerdict) {
        throw "$Name expected verdict '$ExpectedVerdict' but got '$($body.verdict)'"
    }
    if ($exitCode -ne $ExpectedExitCode) {
        throw "$Name expected exit code $ExpectedExitCode but got $exitCode"
    }

    [ordered]@{
        name = $Name
        artifact_kind = $ArtifactKind
        expected_verdict = $ExpectedVerdict
        receipt_verdict = [string]$body.verdict
        exit_code = $exitCode
        receipt_path = $receiptPath
        exit_artifact_present = [bool]$body.exit_artifact_present
        output_tail = @($output | Select-Object -Last 6)
    }
}

$cases = @()
$cases += Run-Case `
    -Name 'no-exit-artifact' `
    -StrategyIncludesRule $true `
    -ArtifactKind 'none' `
    -ExpectedVerdict 'wave0_stop_rule_holds_no_exit_artifact' `
    -ExpectedExitCode 0

$cases += Run-Case `
    -Name 'sealed-visual-exit' `
    -StrategyIncludesRule $true `
    -ArtifactKind 'sealed_visual' `
    -ExpectedVerdict 'wave0_exit_artifact_present' `
    -ExpectedExitCode 0

$cases += Run-Case `
    -Name 'named-defect-exit' `
    -StrategyIncludesRule $true `
    -ArtifactKind 'named_defect' `
    -ExpectedVerdict 'wave0_exit_artifact_present' `
    -ExpectedExitCode 0

$cases += Run-Case `
    -Name 'missing-strategy-rule' `
    -StrategyIncludesRule $false `
    -ArtifactKind 'none' `
    -ExpectedVerdict 'wave0_stop_rule_missing_from_strategy' `
    -ExpectedExitCode 1

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_stop_rule_fixture_checks_passed'
    output_directory = $outRoot
    cases = $cases
}
$summaryPath = Join-Path $outRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 stop-rule fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
