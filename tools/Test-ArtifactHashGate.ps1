[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseCut = Join-Path $repoRoot 'infra\gcp\p7\scripts\New-ReleaseCut.ps1'
$badArtifact = Join-Path $repoRoot 'README.md'
$wrongHash = '0' * 64

$ErrorActionPreference = 'Continue'
$output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $releaseCut `
    -ReleaseId m7-guard-20260812-r1 `
    -ModArtifact $badArtifact `
    -ExpectedSha256 $wrongHash `
    -WhatIf 2>&1)
$exitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($exitCode -eq 0 -or ($output -join "`n") -notmatch 'hash mismatch') {
    throw "G6 did not reject the checked-in wrong-hash fixture (exit=$exitCode)."
}

[pscustomobject]@{
    Schema = 'lumberjacks-boundary-guard/v1'
    Guard = 'G6'
    Fixture = 'README.md with an intentionally wrong SHA-256'
    ObservedExit = $exitCode
    Verdict = 'passed'
} | ConvertTo-Json -Compress
