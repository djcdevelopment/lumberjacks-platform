#Requires -Version 5.1
<# .SYNOPSIS Run isolated contract fixtures for Test-ModpackPayload.ps1. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$verifier = (Resolve-Path (Join-Path $PSScriptRoot 'Test-ModpackPayload.ps1')).Path
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-modpack-contract-' + [Guid]::NewGuid().ToString('N'))
$valheimRoot = Join-Path $fixtureRoot 'live'
$safeStage = Join-Path $fixtureRoot 'safe-stage'
$unsafeStage = Join-Path $fixtureRoot 'unsafe-stage'
$safeZip = Join-Path $fixtureRoot 'safe.zip'
$unsafeZip = Join-Path $fixtureRoot 'unsafe.zip'

function Invoke-Verifier([string]$Package, [switch]$RequireExactMatch) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $verifier,
        '-PackagePath', $Package, '-ValheimRoot', $valheimRoot)
    if ($RequireExactMatch) { $arguments += '-RequireExactMatch' }
    $output = & powershell.exe @arguments 2>&1 | Out-String
    [pscustomobject]@{ exit_code = $LASTEXITCODE; output = $output; body = try { $output | ConvertFrom-Json } catch { $null } }
}

try {
    $liveFile = Join-Path $valheimRoot 'BepInEx\plugins\fixture.dll'
    $safeFile = Join-Path $safeStage 'Valheim\BepInEx\plugins\fixture.dll'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $liveFile), (Split-Path -Parent $safeFile) | Out-Null
    [IO.File]::WriteAllText($liveFile, 'same payload', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($safeFile, 'same payload', [Text.UTF8Encoding]::new($false))
    Get-ChildItem -LiteralPath $safeStage | Compress-Archive -DestinationPath $safeZip

    $same = Invoke-Verifier $safeZip -RequireExactMatch
    if ($same.exit_code -ne 0 -or $same.body.verdict -ne 'passed' -or $same.body.exact_live_match -ne $true) {
        throw 'exact-match fixture did not pass'
    }

    [IO.File]::WriteAllText($liveFile, 'different payload', [Text.UTF8Encoding]::new($false))
    $different = Invoke-Verifier $safeZip -RequireExactMatch
    if ($different.exit_code -eq 0 -or $different.body.different_count -ne 1 -or
        $different.body.different_entries[0] -ne 'BepInEx/plugins/fixture.dll') {
        throw 'different-payload fixture did not fail with the exact relative path'
    }

    $credential = Join-Path $unsafeStage 'Valheim\BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $credential) | Out-Null
    [IO.File]::WriteAllText($credential, 'fixture = never-a-real-key', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $unsafeStage 'outside.txt'), 'outside', [Text.UTF8Encoding]::new($false))
    Get-ChildItem -LiteralPath $unsafeStage | Compress-Archive -DestinationPath $unsafeZip
    $unsafe = Invoke-Verifier $unsafeZip
    if ($unsafe.exit_code -eq 0 -or $unsafe.body.unsafe_count -ne 2 -or
        -not (@($unsafe.body.unsafe_entries) -match '^credential_config_present:') -or
        -not (@($unsafe.body.unsafe_entries) -match '^outside_valheim:')) {
        throw 'unsafe-boundary fixture did not fail closed'
    }

    [pscustomobject]@{
        schema_version = 1
        verdict = 'passed'
        exact_match = 'passed'
        changed_payload = 'failed_closed'
        credential_config = 'failed_closed'
        outside_valheim = 'failed_closed'
    } | ConvertTo-Json -Depth 4
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolved = [IO.Path]::GetFullPath($fixtureRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved).StartsWith('lumberjacks-modpack-contract-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
