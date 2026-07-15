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
$hostConfig = "/mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg"
$startedUtc = (Get-Date).ToUniversalTime().ToString("o")

dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0 -or !(Test-Path $dll)) {
  throw "ComfyNetworkSense build failed or did not produce $dll"
}

$expectedHash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
scp $dll "${SshTarget}:$remoteDll"
if ($LASTEXITCODE -ne 0) { throw "SCP to $SshTarget failed" }

# The config is a bind mount. Editing or copying it as an OS Login user can leave
# that user's numeric UID on the file, causing ConfigFile.Bind to abort plugin Awake.
$deploy = @"
set -eu
sudo chown 1000:1000 '$hostConfig'
sudo chmod 664 '$hostConfig'
sudo docker cp '$remoteDll' '${Container}:$runtimeDll'
test "`$(sudo stat -c '%u:%g %a' '$hostConfig')" = '1000:1000 664'
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
  $modReady = $startup -match "Telemetry scaffold ready\."
  $serverReady = $startup -match "Game server connected"
} until (($modReady -and $serverReady) -or (Get-Date) -ge $deadline)

if (!$modReady -or !$serverReady) {
  throw "Timed out waiting for ComfyNetworkSense and Valheim readiness"
}

$hashOutput = ssh $SshTarget "sudo docker exec '$Container' sha256sum '$runtimeDll'"
$actualHash = [regex]::Match(($hashOutput -join "`n"), "[0-9a-fA-F]{64}").Value.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
  throw "Runtime DLL hash mismatch: expected $expectedHash, got $actualHash"
}

[pscustomobject]@{
  Target = $SshTarget
  Container = $Container
  Sha256 = $actualHash
  ModReady = $modReady
  ServerReady = $serverReady
}
