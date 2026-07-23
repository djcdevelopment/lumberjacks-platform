<#
.SYNOPSIS
Smoke-test the Wave 0 visual seal verifier without Valheim clients.

.DESCRIPTION
Creates two minimal mock live-gate receipts, annotates them through
Add-Wave0VisualObservation.ps1, verifies the happy path, then proves the seal
verifier rejects a non-reversed role pair.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/wave0-visual-seal-fixtures'
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

function Write-MockReceipt {
    param(
        [string]$Name,
        [string]$ApplyClient,
        [string]$ObserveClient
    )

    $path = Join-Path $outputRoot "$Name.receipt.json"
    $receipt = [ordered]@{
        schema_version = 1
        run_id = "fixture-$Name"
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        verdict = 'role_preflight_passed_stopped_before_motion'
        desired_apply_client = $ApplyClient
        pattern = 'straight_north'
        motion_duration_seconds = 10
        capture_duration_seconds = 30
        role_preflight = [ordered]@{
            summary = [ordered]@{
                apply_client = $ApplyClient
                observe_client = $ObserveClient
                exactly_one_apply_enabled = $true
                omen_apply_enabled = ($ApplyClient -eq 'omen')
                i5_apply_enabled = ($ApplyClient -eq 'i5')
            }
        }
        p7_peer_check = [ordered]@{
            peer_count = 2
            players = @('fixture-apply', 'fixture-observe')
        }
    }
    [IO.File]::WriteAllText($path, (($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $path
}

function Invoke-Annotation {
    param(
        [string]$Receipt,
        [string]$ApplyClient,
        [string]$ObserveClient,
        [string]$RoleReversalRun
    )

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Add-Wave0VisualObservation.ps1') `
        -ReceiptJson $Receipt `
        -ApplyClient $ApplyClient `
        -ObserveClient $ObserveClient `
        -VisualResult followed_role `
        -StraightMovement smooth `
        -StutterMovement mixed `
        -RoleReversalRun $RoleReversalRun `
        -Force | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "annotation failed for $Receipt" }
    [IO.Path]::ChangeExtension($Receipt, '.annotated.json')
}

$firstReceipt = Write-MockReceipt -Name 'first-omen-apply' -ApplyClient omen -ObserveClient i5
$reversalReceipt = Write-MockReceipt -Name 'reversal-i5-apply' -ApplyClient i5 -ObserveClient omen
$badReversalReceipt = Write-MockReceipt -Name 'bad-reversal-omen-apply' -ApplyClient omen -ObserveClient i5

$firstAnnotated = Invoke-Annotation -Receipt $firstReceipt -ApplyClient omen -ObserveClient i5 -RoleReversalRun no
$reversalAnnotated = Invoke-Annotation -Receipt $reversalReceipt -ApplyClient i5 -ObserveClient omen -RoleReversalRun yes
$badAnnotated = Invoke-Annotation -Receipt $badReversalReceipt -ApplyClient omen -ObserveClient i5 -RoleReversalRun yes

$goodSeal = Join-Path $outputRoot 'good-seal.json'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Seal-Wave0VisualEvidence.ps1') `
    -FirstAnnotatedJson $firstAnnotated `
    -ReversalAnnotatedJson $reversalAnnotated `
    -OutputJson $goodSeal `
    -AllowMockReceipts | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'good seal fixture failed' }

$badSeal = Join-Path $outputRoot 'bad-seal.json'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools\wave0\Seal-Wave0VisualEvidence.ps1') `
    -FirstAnnotatedJson $firstAnnotated `
    -ReversalAnnotatedJson $badAnnotated `
    -OutputJson $badSeal `
    -AllowMockReceipts | Out-Host
if ($LASTEXITCODE -eq 0) { throw 'bad seal fixture unexpectedly passed' }

$good = Get-Content -LiteralPath $goodSeal -Raw | ConvertFrom-Json
$bad = Get-Content -LiteralPath $badSeal -Raw | ConvertFrom-Json
if ($good.verdict -ne 'wave0_visual_seal_fixture_passed') { throw "unexpected good verdict: $($good.verdict)" }
if ($bad.verdict -ne 'wave0_visual_evidence_not_sealed') { throw "unexpected bad verdict: $($bad.verdict)" }
if ('apply_role_reversed' -notin @($bad.failed_checks)) { throw 'bad fixture did not fail apply_role_reversed' }

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = 'wave0_visual_seal_fixture_checks_passed'
    output_directory = $outputRoot
    good_seal = $goodSeal
    bad_seal = $badSeal
}
$summaryPath = Join-Path $outputRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 6) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Wave 0 visual seal fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
