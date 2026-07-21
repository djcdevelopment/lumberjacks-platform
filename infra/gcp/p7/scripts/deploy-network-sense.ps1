[CmdletBinding()]
param(
  [string] $SshTarget = "comfy-p7",
  [string] $Container = "comfy-lumberjacks-p7-valheim-server-1",
  [string] $Project = "$PSScriptRoot\..\..\..\..\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj",
  [string] $Configuration = "Release",
  [int] $ReadyTimeoutSeconds = 360,
  [string] $ManifestPath
)

$ErrorActionPreference = "Stop"
$projectPath = [IO.Path]::GetFullPath($Project)
$projectDir = Split-Path $projectPath
$dll = Join-Path $projectDir "bin\$Configuration\ComfyNetworkSense.dll"
$remoteDll = "/tmp/ComfyNetworkSense-deploy.dll"
$runtimeDll = "/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll"
$fallbackDll = "/mnt/comfy-p7/valheim/config/bepinex/plugins/ComfyNetworkSense.dll"
$hostConfig = "/mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg"
$startedUtc = (Get-Date).ToUniversalTime().ToString("o")

dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0 -or !(Test-Path $dll)) {
  throw "ComfyNetworkSense build failed or did not produce $dll"
}

$expectedHash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
$pluginVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString(3)
$backupStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$backupRoot = "/mnt/comfy-p7/backups/comfynetworksense/$backupStamp"
$ErrorActionPreference = 'Continue'
scp $dll "${SshTarget}:$remoteDll"
$scpExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($scpExit -ne 0) { throw "SCP to $SshTarget failed" }

# The config is a bind mount. Editing or copying it as an OS Login user can leave
# that user's numeric UID on the file, causing ConfigFile.Bind to abort plugin Awake.
$deploy = @"
set -eu
sudo install -d -m 0750 '$backupRoot'
if sudo docker inspect '$Container' >/dev/null 2>&1; then
  sudo docker cp '${Container}:$runtimeDll' '$backupRoot/runtime.dll' 2>/dev/null || true
fi
if sudo test -f '$fallbackDll'; then sudo cp -a '$fallbackDll' '$backupRoot/fallback.dll'; fi
if sudo test -f '$hostConfig'; then sudo cp -a '$hostConfig' '$backupRoot/config.cfg'; fi
sudo chown 1000:1000 '$hostConfig'
sudo chmod 664 '$hostConfig'
sudo install -o 1000 -g 1000 -m 0644 '$remoteDll' '$fallbackDll'
sudo docker cp '$remoteDll' '${Container}:$runtimeDll'
test `$(sudo stat -c '%u:%g:%a' '$hostConfig') = '1000:1000:664'
sudo docker exec '$Container' supervisorctl restart valheim-server
"@
$deployEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($deploy))
$ErrorActionPreference = 'Continue'
ssh $SshTarget "printf '%s' '$deployEncoded' | base64 -d | bash"
$deployExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($deployExit -ne 0) { throw "Remote deployment or restart failed" }

# Era16 loads more than nine million ZDOs and routinely needs 80-100 seconds.
# Poll readiness instead of sleeping for the entire operator timeout so a warm or
# faster startup completes immediately while the large-world upper bound remains.
$waitSeconds = [Math]::Min([Math]::Max($ReadyTimeoutSeconds, 20), 360)
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($waitSeconds)
$startup = ''
$modReady = $false
$serverReady = $false
do {
  Start-Sleep -Seconds 5
  $ErrorActionPreference = 'Continue'
  $startup = ssh $SshTarget "sudo docker logs --since '$startedUtc' '$Container' 2>&1" 2>$null
  $logExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  if ($logExit -ne 0) { continue }
  if ($startup -match "UnauthorizedAccessException") {
    throw "BepInEx config ownership failure detected after restart"
  }
  $modReady = [bool]($startup -match "Telemetry scaffold ready\.")
  $serverReady = [bool]($startup -match "Game server connected")
} while ((!$modReady -or !$serverReady) -and [DateTimeOffset]::UtcNow -lt $deadline)

if (!$modReady -or !$serverReady) {
  throw "Timed out waiting for ComfyNetworkSense and Valheim readiness"
}

$ErrorActionPreference = 'Continue'
$hashOutput = ssh $SshTarget "sudo docker exec '$Container' sha256sum '$runtimeDll'"
$hashExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($hashExit -ne 0) { throw "Could not hash the runtime DLL" }
$actualHash = [regex]::Match(($hashOutput -join "`n"), "[0-9a-fA-F]{64}").Value.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
  throw "Runtime DLL hash mismatch: expected $expectedHash, got $actualHash"
}

$ErrorActionPreference = 'Continue'
$fallbackHashOutput = ssh $SshTarget "sudo sha256sum '$fallbackDll'"
$fallbackHashExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($fallbackHashExit -ne 0) { throw "Could not hash the cold-start DLL" }
$fallbackHash = [regex]::Match(($fallbackHashOutput -join "`n"), "[0-9a-fA-F]{64}").Value.ToLowerInvariant()
if ($fallbackHash -ne $expectedHash) {
  throw "Cold-start DLL hash mismatch: expected $expectedHash, got $fallbackHash"
}

# Keep the cold-start deployment fallback in sync after the server has loaded the
# expected DLL. Gateway prefers the live Valheim heartbeat, so it must not be
# restarted (and its authoritative queue discarded) just to refresh this label.
$updateMetadata = @"
set -eu
sudo sed -i '/^COMFY_NETWORKSENSE_VERSION=/d' /etc/comfy-p7/environment
printf '%s\n' 'COMFY_NETWORKSENSE_VERSION=$pluginVersion' | sudo tee -a /etc/comfy-p7/environment >/dev/null
sudo grep -Fx 'COMFY_NETWORKSENSE_VERSION=$pluginVersion' /etc/comfy-p7/environment
"@
$metadataEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($updateMetadata))
$ErrorActionPreference = 'Continue'
ssh $SshTarget "printf '%s' '$metadataEncoded' | base64 -d | bash"
$metadataExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($metadataExit -ne 0) { throw "Deployment metadata fallback reconciliation failed" }

[pscustomobject]@{
  Target = $SshTarget
  Container = $Container
  Sha256 = $actualHash
  Version = $pluginVersion
  BackupPath = $backupRoot
  ModReady = $modReady
  ServerReady = $serverReady
}

if ($ManifestPath) {
  $sourceRevision = (& git -C $projectDir rev-parse HEAD 2>$null | Out-String).Trim()
  $assemblyInputs = @(
    "C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core\BepInEx.dll",
    "C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll",
    "C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\UnityEngine.CoreModule.dll"
  ) | Where-Object { Test-Path $_ } | ForEach-Object {
    [ordered]@{ path = $_; sha256 = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
  }
  $ErrorActionPreference = 'Continue'
  $remoteImage = ssh $SshTarget "sudo docker image inspect '$Container' --format '{{index .Config.Image}} {{index .Image}}'" 2>$null
  $ErrorActionPreference = 'Stop'
  $manifest = [ordered]@{
    schema = 'comfy-p7-release/v1'
    captured_utc = (Get-Date).ToUniversalTime().ToString('o')
    target = $SshTarget
    source_revision = $sourceRevision
    plugin = [ordered]@{ version = $pluginVersion; sha256 = $expectedHash; runtime_sha256 = $actualHash; cold_start_sha256 = $fallbackHash }
    assembly_inputs = @($assemblyInputs)
    backup_path = $backupRoot
    gateway_image = (($remoteImage -join "`n").Trim())
    container = $Container
  }
  $manifestDir = Split-Path -Parent $ManifestPath
  if ($manifestDir) { New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null }
  $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
  Write-Output "Release manifest: $ManifestPath"
}
