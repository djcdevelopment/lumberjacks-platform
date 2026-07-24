<#
.SYNOPSIS
Static contract check for Companion install/rollback safety.

.DESCRIPTION
The Wave 0 return packet names a rollback/stop path before live testing. This
script proves the Companion source still exposes that path with the required
guards without invoking the live rollback endpoint or mutating installed files.
#>
[CmdletBinding()]
param(
    [string]$OutputJson = 'captures/companion-rollback-contract.json'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$programPath = Join-Path $repoRoot 'Lumberjacks/src/Game.Companion/Program.cs'
$pagePath = Join-Path $repoRoot 'Lumberjacks/src/Game.Companion/CompanionPage.cs'

function New-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [ordered]@{ name = $Name; ok = $Ok; detail = $Detail }
}

$program = Get-Content -LiteralPath $programPath -Raw
$page = Get-Content -LiteralPath $pagePath -Raw

$checks = @()
$checks += New-Check 'rollback_endpoint_exists' ($program -match 'MapPost\("/api/v0/companion/update/rollback"') 'Companion must expose POST /api/v0/companion/update/rollback.'
$checks += New-Check 'rollback_endpoint_requires_game_closed_confirmation' ($program -match 'confirmation\?\.game_closed_confirmed\s*!=\s*true' -and $program -match 'game_closed_confirmation_required') 'Rollback endpoint must reject requests that omit explicit game-closed confirmation.'
$checks += New-Check 'install_endpoint_requires_same_confirmation' ($program -match 'MapPost\("/api/v0/companion/update/install"' -and $program -match 'InstallAsync\(gateway, cancellationToken\)' -and $program -match 'game_closed_confirmation_required') 'Install and rollback should share the same explicit confirmation contract.'
$checks += New-Check 'installer_preserves_config_file' ($program -match 'relative\.EndsWith\("djcdevelopment\.valheim\.comfynetworksense\.cfg"[\s\S]*continue') 'Install must skip the existing ComfyNetworkSense config/credential file.'
$checks += New-Check 'install_records_backup_before_overwrite' ($program -match 'File\.Exists\(target\)[\s\S]*File\.Copy\(target,\s*backup,\s*true\)' -and $program -match 'new InstalledRelease\(manifest\.release,\s*manifest\.mod_release,\s*actualHash,\s*DateTime\.UtcNow,\s*backupRoot,\s*changed\)') 'Install must copy existing files to backupRoot and persist installed backup metadata.'
$checks += New-Check 'rollback_refuses_missing_backup' ($program -match 'current\.installed is null \|\| !Directory\.Exists\(current\.installed\.backup_path\)[\s\S]*rollback_backup_missing') 'Rollback must fail closed if no prior install/backup exists.'
$checks += New-Check 'rollback_refuses_missing_valheim' ($program -match 'valheimPath is null\) return InstallResult\.Fail\("valheim_not_found"\)') 'Rollback must not proceed without locating the Valheim install.'
$checks += New-Check 'rollback_refuses_running_game' ($program -match 'ValheimLocator\.IsRunning\(\)\) return InstallResult\.Fail\("valheim_is_running"\)') 'Rollback must not write files while Valheim or the dedicated server is running.'
$checks += New-Check 'rollback_restores_backup_files_to_valheim' ($program -match 'Directory\.EnumerateFiles\(current\.installed\.backup_path,\s*"\*"[\s\S]*File\.Copy\(backup,\s*target,\s*true\)') 'Rollback must restore every backed-up file to the Valheim install.'
$checks += New-Check 'ui_rollback_button_guarded_by_install_and_ready_state' ($page -match "q\('#rollback'\)\.disabled=!state\.installed\|\|!ready") 'UI must not enable rollback unless a prior install exists and the local readiness checks are green.'
$checks += New-Check 'ui_posts_rollback_with_confirmation_body' ($page -match "fetch\('/api/v0/companion/update/rollback',\{method:'POST',\.\.\.confirmation\(\)\}") 'UI rollback action must post the game_closed_confirmed confirmation body.'

$failed = @($checks | Where-Object { -not $_.ok })
$receipt = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($failed.Count -eq 0) { 'companion_rollback_contract_passed' } else { 'companion_rollback_contract_failed' }
    source_files = @(
        [ordered]@{ path = $programPath; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $programPath).Hash.ToLowerInvariant() },
        [ordered]@{ path = $pagePath; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $pagePath).Hash.ToLowerInvariant() }
    )
    checks = $checks
    failed_checks = @($failed | ForEach-Object { $_.name })
}

$outputPath = if ([IO.Path]::IsPathRooted($OutputJson)) {
    [IO.Path]::GetFullPath($OutputJson)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputJson))
}
$outputDir = Split-Path -Parent $outputPath
if ($outputDir) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }
[IO.File]::WriteAllText($outputPath, (($receipt | ConvertTo-Json -Depth 10) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("Companion rollback contract: {0}" -f $receipt.verdict)
Write-Host ("Receipt JSON: {0}" -f $outputPath)
if ($failed.Count -gt 0) { exit 1 }
