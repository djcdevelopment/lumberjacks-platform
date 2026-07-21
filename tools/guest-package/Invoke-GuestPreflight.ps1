param(
    [string]$ValheimPath,
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$BootstrapUrl,
    [switch]$NoBootstrap
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ComfyGuestCommon.psm1') -Force

function Add-Check([System.Collections.Generic.List[object]]$List, [string]$Id, [bool]$Ok, [string]$Evidence, [string]$Remedy) {
    [void]$List.Add([pscustomobject]@{ id = $Id; status = $(if ($Ok) { 'PASS' } else { 'FAIL' }); evidence = $Evidence; remedy = $Remedy })
}

$inputsPath = Join-Path $PackageRoot 'guest-package-inputs.json'
$inputs = if (Test-Path -LiteralPath $inputsPath) { Get-Content -LiteralPath $inputsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$checks = New-Object 'System.Collections.Generic.List[object]'
$dll = Join-Path $PackageRoot 'ComfyNetworkSense.dll'
$expected = if ($inputs) { $null } else { $null }
if (Test-Path -LiteralPath (Join-Path $PackageRoot 'manifest.json')) {
    $manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $expected = ([string]$manifest.mod.clean_build_sha256).ToLowerInvariant()
}
$actual = if (Test-Path -LiteralPath $dll) { Get-ComfySha256 $dll } else { '' }
Add-Check $checks 'dll_hash' (($actual -ne '') -and ($actual -eq $expected)) ('sha256=' + $(if ($actual) { $actual } else { 'missing' })) 'Restore the packaged DLL from the sealed guest package and rerun preflight.'

if (!$ValheimPath) { try { $ValheimPath = Find-ComfySteamValheimInstall } catch { $ValheimPath = '' } }
$bep = if ($ValheimPath) { Join-Path $ValheimPath 'BepInEx' } else { '' }
Add-Check $checks 'bepinex_present' (($bep -ne '') -and (Test-Path -LiteralPath $bep -PathType Container)) 'BepInEx directory present' 'Install BepInExPack Valheim into the Valheim directory, launch once, then rerun preflight.'
$running = @(Get-Process -Name valheim -ErrorAction SilentlyContinue).Count -gt 0
Add-Check $checks 'valheim_not_running' (!$running) ($(if ($running) { 'valheim.exe is running' } else { 'no valheim.exe process' })) 'Quit Valheim completely, including its launcher process, before installing or changing files.'

$config = if ($ValheimPath) { Join-Path $ValheimPath 'BepInEx\config' } else { '' }
$writable = $false
if ($config -and (Test-Path -LiteralPath $config -PathType Container)) {
    $probe = Join-Path $config ('.comfy-preflight-' + [Guid]::NewGuid().ToString('N'))
    try { [IO.File]::WriteAllText($probe, 'probe'); Remove-Item -LiteralPath $probe -Force; $writable = $true } catch { $writable = $false; if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue } }
}
Add-Check $checks 'config_writable' $writable 'temporary write probe' 'Close tools locking the config directory and grant the current user write access.'

$healthOk = $false; $tlsOk = $true
if ($inputs) {
    $healthUrl = ([string]$inputs.gateway_base_url).TrimEnd('/') + [string]$inputs.gateway_health_path
    try { $h = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -Method Get -TimeoutSec 8; $healthOk = ([int]$h.StatusCode -ge 200 -and [int]$h.StatusCode -lt 300) } catch { $healthOk = $false; if ($healthUrl -match '^https://') { $tlsOk = $false } }
    if ($healthUrl -match '^https://') { $tlsOk = $healthOk }
}
Add-Check $checks 'gateway_health' $healthOk 'anonymous /health probe' 'Confirm the gateway address is reachable and the Gateway health endpoint returns HTTP 2xx.'
Add-Check $checks 'gateway_tls' $tlsOk ($(if ($tlsOk) { 'TLS not required for the committed HTTP endpoint or certificate validated' } else { 'TLS certificate validation failed' })) 'Use the deployed HTTP endpoint or install a certificate trusted by this machine; do not switch production to HTTPS speculatively.'

$bootstrapValues = @{}
$bootstrapStatus = 200
if (!$NoBootstrap -and $BootstrapUrl) {
    $boot = Get-ComfyBootstrap $BootstrapUrl
    $bootstrapStatus = $boot.StatusCode
    $bootstrapValues = $boot.Values
    $bootOk = ($boot.StatusCode -eq 200) -and ($bootstrapValues.ContainsKey('lumberjacksEnrollmentId'))
    Add-Check $checks 'bootstrap_token' $bootOk ('HTTP status ' + [string]$boot.StatusCode) 'Request a fresh one-use bootstrap URL; a consumed token cannot be replayed.'
    $enrollment = $bootstrapValues['lumberjacksEnrollmentId']
} else {
    $enrollment = 'not-probed'
    Add-Check $checks 'bootstrap_token' $true 'bootstrap probe deferred to installer' 'Provide a fresh one-use bootstrap URL to the installer.'
}
$enrollmentOk = ($enrollment -and $enrollment -ne 'not-probed' -and ([string]$enrollment).Trim().Length -gt 0)
if (!$BootstrapUrl -or $NoBootstrap) { $enrollmentOk = $true }
Add-Check $checks 'enrollment_id' $enrollmentOk ($(if ($enrollmentOk) { 'non-empty enrollment id' } else { 'empty enrollment id' })) 'Ask the host for a fresh enrollment bootstrap; do not hand-edit an enrollment id.'

$failed = @($checks | Where-Object { $_.status -eq 'FAIL' })
$checkArray = $checks.ToArray()
[pscustomobject]@{ verdict = $(if ($failed.Count -eq 0) { 'READY' } else { 'NOT_READY' }); checks = $checkArray; generated_utc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 8
