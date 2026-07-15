[CmdletBinding()]
param(
  [string] $SshTarget = "comfy-p7",
  [string] $Container = "comfy-lumberjacks-p7-valheim-server-1",
  [string] $Project = "$PSScriptRoot\..\..\..\..\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj",
  [string] $Configuration = "Release",
  [int] $ReadyTimeoutSeconds = 360
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
scp $dll "${SshTarget}:$remoteDll"
if ($LASTEXITCODE -ne 0) { throw "SCP to $SshTarget failed" }

# The config is a bind mount. Editing or copying it as an OS Login user can leave
# that user's numeric UID on the file, causing ConfigFile.Bind to abort plugin Awake.
$deploy = @"
set -eu
sudo chown 1000:1000 '$hostConfig'
sudo chmod 664 '$hostConfig'
sudo install -o 1000 -g 1000 -m 0644 '$remoteDll' '$fallbackDll'
sudo docker cp '$remoteDll' '${Container}:$runtimeDll'
test `$(sudo stat -c '%u:%g:%a' '$hostConfig') = '1000:1000:664'
sudo docker exec '$Container' supervisorctl restart valheim-server
"@
ssh $SshTarget $deploy
if ($LASTEXITCODE -ne 0) { throw "Remote deployment or restart failed" }

$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$startup = ""
do {
  Start-Sleep -Seconds 5
  $startup = ssh $SshTarget "sudo docker logs --since '$startedUtc' '$Container' 2>&1"
  if ($startup -match "UnauthorizedAccessException") {
    throw "BepInEx config ownership failure detected after restart"
  }
  $modReady = [bool]($startup -match "Telemetry scaffold ready\.")
  $serverReady = [bool]($startup -match "Game server connected")
} until (($modReady -and $serverReady) -or (Get-Date) -ge $deadline)

if (!$modReady -or !$serverReady) {
  throw "Timed out waiting for ComfyNetworkSense and Valheim readiness"
}

$hashOutput = ssh $SshTarget "sudo docker exec '$Container' sha256sum '$runtimeDll'"
$actualHash = [regex]::Match(($hashOutput -join "`n"), "[0-9a-fA-F]{64}").Value.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
  throw "Runtime DLL hash mismatch: expected $expectedHash, got $actualHash"
}

$fallbackHashOutput = ssh $SshTarget "sudo sha256sum '$fallbackDll'"
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
ssh $SshTarget $updateMetadata
if ($LASTEXITCODE -ne 0) { throw "Deployment metadata fallback reconciliation failed" }

[pscustomobject]@{
  Target = $SshTarget
  Container = $Container
  Sha256 = $actualHash
  Version = $pluginVersion
  ModReady = $modReady
  ServerReady = $serverReady
}
