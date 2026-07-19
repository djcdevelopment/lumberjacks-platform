param(
    [Parameter(Mandatory=$true)][string]$BootstrapUrl,
    [string]$ValheimPath,
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot)
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ComfyGuestCommon.psm1') -Force
if (!$ValheimPath) { $ValheimPath = Find-ComfySteamValheimInstall }
$bep = Join-Path $ValheimPath 'BepInEx'
$plugins = Join-Path $bep 'plugins'
$configDir = Join-Path $bep 'config'
if (!(Test-Path -LiteralPath $bep -PathType Container)) { throw 'BepInEx is missing' }
if (@(Get-Process -Name valheim -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Valheim is running' }
$dll = Join-Path $PackageRoot 'ComfyNetworkSense.dll'
$manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ((Get-ComfySha256 $dll) -ne ([string]$manifest.mod.clean_build_sha256).ToLowerInvariant()) { throw 'guest DLL hash mismatch' }
$boot = Get-ComfyBootstrap $BootstrapUrl
if ($boot.StatusCode -ne 200) { throw ('bootstrap failed with HTTP ' + [string]$boot.StatusCode) }
$required = @('lumberjacksGatewayUrl','lumberjacksAuthoritativeWindowId','lumberjacksEnrollmentId','lumberjacksClientAccessKey')
foreach ($key in $required) { if (!$boot.Values.ContainsKey($key) -or !([string]$boot.Values[$key]).Trim()) { throw ('bootstrap response missing ' + $key + '; body=' + $boot.Body.Replace("`n", '<LF>')) } }
$receiptDir = Join-Path $bep 'comfy-guest-backups'
$receiptPath = Join-Path $bep 'comfy-guest-install.json'
$cfgPath = Join-Path $configDir 'djcdevelopment.valheim.comfynetworksense.cfg'
$pluginPath = Join-Path $plugins 'ComfyNetworkSense.dll'
New-Item -ItemType Directory -Path $plugins,$configDir,$receiptDir -Force | Out-Null
$id = [Guid]::NewGuid().ToString('N')
$backupDir = Join-Path $receiptDir $id
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$touched = @()
try {
    $cfgExisted = Test-Path -LiteralPath $cfgPath -PathType Leaf
    $pluginExisted = Test-Path -LiteralPath $pluginPath -PathType Leaf
    if ($cfgExisted) { Copy-Item -LiteralPath $cfgPath -Destination (Join-Path $backupDir 'config.cfg') -Force }
    if ($pluginExisted) { Copy-Item -LiteralPath $pluginPath -Destination (Join-Path $backupDir 'plugin.dll') -Force }
    $cfgText = if ($cfgExisted) { [IO.File]::ReadAllText($cfgPath, (New-Object Text.UTF8Encoding($false))) } else { '' }
    $values = [ordered]@{ lumberjacksGatewayUrl = $boot.Values['lumberjacksGatewayUrl']; lumberjacksAuthoritativeWindowId = $boot.Values['lumberjacksAuthoritativeWindowId']; lumberjacksEnrollmentId = $boot.Values['lumberjacksEnrollmentId']; lumberjacksClientAccessKey = $boot.Values['lumberjacksClientAccessKey']; zdoAuthoritativeConsumerEnabled = 'true' }
    $merged = Merge-ComfyBepInExSection $cfgText $values
    Invoke-ComfyAtomicReplace $cfgPath { param($tmp); Write-ComfyUtf8NoBom $tmp $merged }
    $touched += [ordered]@{ path = $cfgPath; existed = $cfgExisted; backup = $(if ($cfgExisted) { Join-Path $backupDir 'config.cfg' } else { $null }); managed_keys = @($values.Keys); installed_sha256 = Get-ComfySha256 $cfgPath }
    Invoke-ComfyAtomicReplace $pluginPath { param($tmp); Copy-Item -LiteralPath $dll -Destination $tmp -Force }
    $touched += [ordered]@{ path = $pluginPath; existed = $pluginExisted; backup = $(if ($pluginExisted) { Join-Path $backupDir 'plugin.dll' } else { $null }) }
    $receipt = [ordered]@{ schema = 'comfy-guest-install/v1'; installed_utc = [DateTime]::UtcNow.ToString('o'); package_release_id = [string]$manifest.release_id; backup_dir = $backupDir; files = $touched }
    Invoke-ComfyAtomicReplace $receiptPath { param($tmp); Write-ComfyUtf8NoBom $tmp ($receipt | ConvertTo-Json -Depth 8) }
    Write-Output ($receipt | ConvertTo-Json -Depth 8)
} catch {
    if ($pluginExisted -and (Test-Path -LiteralPath (Join-Path $backupDir 'plugin.dll'))) { Copy-Item -LiteralPath (Join-Path $backupDir 'plugin.dll') -Destination $pluginPath -Force } elseif (!$pluginExisted -and (Test-Path -LiteralPath $pluginPath)) { Remove-Item -LiteralPath $pluginPath -Force }
    if ($cfgExisted -and (Test-Path -LiteralPath (Join-Path $backupDir 'config.cfg'))) { Copy-Item -LiteralPath (Join-Path $backupDir 'config.cfg') -Destination $cfgPath -Force } elseif (!$cfgExisted -and (Test-Path -LiteralPath $cfgPath)) { Remove-Item -LiteralPath $cfgPath -Force }
    throw
}
