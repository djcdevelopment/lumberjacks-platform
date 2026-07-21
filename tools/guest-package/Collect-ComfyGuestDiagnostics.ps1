param(
    [Parameter(Mandatory=$true)][string]$ValheimPath,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ComfyGuestCommon.psm1') -Force
if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$config = Join-Path $ValheimPath 'BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg'
$log = Join-Path $ValheimPath 'BepInEx\LogOutput.log'
$sensitive = @()
if (Test-Path -LiteralPath $config -PathType Leaf) {
    $rawConfig = Get-Content -LiteralPath $config -Raw
    foreach ($m in [regex]::Matches($rawConfig, '(?im)^\s*(?:lumberjacksClientAccessKey|lumberjacksTelemetryKey)\s*=\s*(\S+)')) { $sensitive += $m.Groups[1].Value }
}
foreach ($source in @($config,$log)) {
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $name = Split-Path -Leaf $source
    $redacted = Remove-ComfySensitiveText (Get-Content -LiteralPath $source -Raw) $sensitive
    Write-ComfyUtf8NoBom (Join-Path $OutputRoot $name) $redacted
}
$report = Join-Path $OutputRoot 'diagnostics.json'
Write-ComfyUtf8NoBom $report (([ordered]@{ schema = 'comfy-guest-diagnostics/v1'; created_utc = [DateTime]::UtcNow.ToString('o'); files = @(Get-ChildItem -LiteralPath $OutputRoot -File | ForEach-Object { [ordered]@{ path = $_.Name; sha256 = Get-ComfySha256 $_.FullName; bytes = $_.Length } }) } | ConvertTo-Json -Depth 8))
$prohibited = @('\b7656119\d{10}\b','(?i)(?:client[_ -]?access[_ -]?key|telemetry[_ -]?key|admin[_ -]?key|bearer|invite[_ -]?token|password)\s*[=:]\s*[A-Za-z0-9+/_=-]{12,}')
foreach ($file in @(Get-ChildItem -LiteralPath $OutputRoot -File -Recurse)) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($pattern in $prohibited) { if ($text -match $pattern) { throw ('diagnostics redaction failed: ' + $file.Name) } }
}
Write-Output $OutputRoot
