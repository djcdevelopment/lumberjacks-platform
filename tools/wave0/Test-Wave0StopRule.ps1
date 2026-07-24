<#
.SYNOPSIS
Check the Wave 0 stop rule before any later-wave expansion.

.DESCRIPTION
The strategy says not to begin M1/M2 expansion until Wave 0 has either a sealed
visual observation packet for both directions or a named blocking defect. This
script checks the strategy text and the live artifact locations without scanning
fixture directories.
#>
[CmdletBinding()]
param(
    [string]$StrategyPath = 'plans/full-roadmap-working-strategy.md',
    [string]$VisualSealJson = 'captures/wave0-live-seal/visual-seal.json',
    [string]$DefectRoot = 'captures/wave0-defects',
    [string]$OutputJson = 'captures/wave0-stop-rule.json'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Resolve-UnderRepo {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Read-JsonOrNull {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}

function New-Artifact {
    param(
        [string]$Kind,
        [bool]$Present,
        [string]$Path,
        [string]$Verdict
    )

    [ordered]@{
        kind = $Kind
        present = $Present
        path = $Path
        verdict = $Verdict
        sha256 = if ($Present -and (Test-Path -LiteralPath $Path -PathType Leaf)) {
            (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
        } else {
            $null
        }
    }
}

$strategyFullPath = Resolve-UnderRepo $StrategyPath
$sealFullPath = Resolve-UnderRepo $VisualSealJson
$defectFullRoot = Resolve-UnderRepo $DefectRoot
$outputFullPath = Resolve-UnderRepo $OutputJson
$outputDir = Split-Path -Parent $outputFullPath
if ($outputDir) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

$strategyText = if (Test-Path -LiteralPath $strategyFullPath -PathType Leaf) {
    Get-Content -LiteralPath $strategyFullPath -Raw
} else {
    ''
}
$strategyNamesRule = $strategyText.Contains('Do not begin M1/M2 expansion work until Wave 0') -and
    $strategyText.Contains('sealed visual') -and
    $strategyText.Contains('named blocking defect')

$seal = Read-JsonOrNull $sealFullPath
$sealArtifact = New-Artifact `
    -Kind 'visual_seal' `
    -Present ($null -ne $seal) `
    -Path $sealFullPath `
    -Verdict ($(if ($seal -and $seal.verdict) { [string]$seal.verdict } else { '' }))

$defectPackets = @()
if (Test-Path -LiteralPath $defectFullRoot -PathType Container) {
    $files = Get-ChildItem -LiteralPath $defectFullRoot -Recurse -File -Filter 'packet.json' |
        Sort-Object LastWriteTimeUtc -Descending
    foreach ($file in $files) {
        $body = Read-JsonOrNull $file.FullName
        if ($body -and [string]$body.artifact_type -eq 'wave0_named_defect_packet') {
            $defectPackets += New-Artifact `
                -Kind 'named_defect_packet' `
                -Present $true `
                -Path $file.FullName `
                -Verdict ([string]$body.verdict)
        }
    }
}

$sealedVisual = $sealArtifact.present -and [string]$sealArtifact.verdict -eq 'wave0_visual_evidence_sealed'
$namedDefect = @($defectPackets | Where-Object { [string]$_.verdict -eq 'wave0_named_defect_packet_retained' }).Count -gt 0
$exitArtifactPresent = $sealedVisual -or $namedDefect

$verdict = if (-not $strategyNamesRule) {
    'wave0_stop_rule_missing_from_strategy'
} elseif ($exitArtifactPresent) {
    'wave0_exit_artifact_present'
} else {
    'wave0_stop_rule_holds_no_exit_artifact'
}

$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = $verdict
    strategy_path = $strategyFullPath
    strategy_names_rule = [bool]$strategyNamesRule
    exit_artifact_present = [bool]$exitArtifactPresent
    sealed_visual_present = [bool]$sealedVisual
    named_defect_present = [bool]$namedDefect
    artifacts = @($sealArtifact) + @($defectPackets)
    next_action = if ($exitArtifactPresent) {
        'Wave 0 has an exit artifact. Inspect it before deciding whether later-wave expansion is allowed.'
    } else {
        'Do not begin M1/M2 expansion. Continue waiting for sealed visual evidence or retain a named Wave 0 defect.'
    }
}

[IO.File]::WriteAllText($outputFullPath, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host ("Wave 0 stop rule: {0}" -f $receipt.verdict)
Write-Host ("Receipt JSON: {0}" -f $outputFullPath)
if (-not $strategyNamesRule) { exit 1 }

