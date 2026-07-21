<#
.SYNOPSIS
  Proves one real Valheim ZDO through Comfy Importance -> Lumberjacks -> Comfy consumer.

.DESCRIPTION
  Builds the exact Comfy Release DLL without touching the Windows Steam plugin, builds the Gateway
  image with the same admitted release, and runs the existing local dedicated-server state through
  one newly created Gateway container and one newly created headless-client container. The lab state
  is backed up and restored. Only containers created by this script are removed.

  This is intentionally not a curl-as-mod test. A real headless Valheim client joins the real
  dedicated server. The server's CreateSyncList Harmony hook observes actual ZDO work, the final
  Importance gate allows one item and rejects another, and the real client-side consumer invokes
  RPC_ZDOData before acknowledging it.
#>
[CmdletBinding()]
param(
  [string] $ReleaseId = 'm4-integration-20260719-r1',
  [string] $ComfyRoot = 'C:\work\comfy',
  [string] $LumberjacksRoot = 'C:\work\Lumberjacks',
  [string] $ServerContainer = 'comfy-valheim-lab-valheim-server-1',
  [string] $ClientContainer = 'comfy-valheim-lab-valheim-client-01-1',
  [string] $LabNetwork = 'comfy-valheim-lab_default',
  [string] $ServerPassword = 'comfytest',
  [int] $ClientLaunchTimeoutMinutes = 15,
  [int] $TimeoutMinutes = 12,
  [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$python = 'C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe'
$module = Join-Path $ComfyRoot 'tools\guest-package\lib\ComfyGuestCommon.psm1'
Import-Module $module -Force

$modProject = Join-Path $ComfyRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj'
$modSource = Join-Path $ComfyRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.cs'
$modDll = Join-Path $ComfyRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
$verifier = Join-Path $ComfyRoot 'tools\verify_comfy_lumberjacks_integration.py'
$releaseReader = Join-Path $ComfyRoot 'infra\gcp\p7\scripts\lib\ReleaseIdentity.ps1'
$imageVerifier = Join-Path $ComfyRoot 'infra\gcp\p7\scripts\Test-GatewayImageRelease.ps1'
$livePlugin = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\ComfyNetworkSense.dll'

$stateRoot = Join-Path $ComfyRoot 'fieldlab\autonomous\state'
$serverCfg = Join-Path $stateRoot 'server\config\bepinex\djcdevelopment.valheim.comfynetworksense.cfg'
$serverSeedPlugin = Join-Path $stateRoot 'server\config\bepinex\plugins\ComfyNetworkSense.dll'
$serverRedirect = Join-Path $stateRoot 'server\config\bepinex\comfy-network-sense\redirect-send.jsonl'
$clientSharedRoot = Join-Path $stateRoot 'client-shared'
$clientSharedCfg = Join-Path $stateRoot 'client-shared\config\djcdevelopment.valheim.comfynetworksense.cfg'
$clientSharedPlugin = Join-Path $stateRoot 'client-shared\plugins\ComfyNetworkSense.dll'
$clientHomeRoot = Join-Path $stateRoot 'client01\home'
$clientGamesRoot = Join-Path $stateRoot 'client01\games'
$clientRoot = Join-Path $stateRoot 'client01\games\GameLibrary\Steam\steamapps\common\Valheim'
$clientCfg = Join-Path $clientRoot 'BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg'
$clientPlugin = Join-Path $clientRoot 'BepInEx\plugins\ComfyNetworkSense.dll'
$clientLog = Join-Path $clientRoot 'BepInEx\LogOutput.log'
$clientLauncher = Join-Path $clientRoot 'start_game_bepinex.sh'
$clientExecutable = Join-Path $clientRoot 'valheim.x86_64'
$clientManifest = Join-Path $clientGamesRoot 'GameLibrary\Steam\steamapps\appmanifest_892970.acf'
$clientConnectionLog = Join-Path $clientHomeRoot '.steam\steam\logs\connection_log.txt'
$serverRuntimePlugin = '/opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll'
$serverRuntimeLog = '/opt/valheim/bepinex/BepInEx/LogOutput.log'

foreach ($path in @($python, $module, $modProject, $modSource, $verifier, $releaseReader,
    $imageVerifier, $livePlugin, $serverCfg, $serverSeedPlugin, $clientSharedCfg,
    $clientSharedPlugin, $clientCfg, $clientPlugin, $clientLauncher, $clientExecutable,
    $clientManifest, $clientConnectionLog)) {
  if (!(Test-Path -LiteralPath $path)) { throw "missing integration input: $path" }
}

$sourceText = [IO.File]::ReadAllText($modSource)
$sourceMatch = [regex]::Match($sourceText, 'public const string ReleaseId = "([^"]+)";')
if (!$sourceMatch.Success -or $sourceMatch.Groups[1].Value -ne $ReleaseId) {
  throw "Comfy source release '$($sourceMatch.Groups[1].Value)' does not equal requested '$ReleaseId'"
}

$runId = 'comfy-lj-integration-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$window = $runId
$runRoot = Join-Path $ComfyRoot ('fieldlab\runs\' + $runId)
$backupRoot = Join-Path $runRoot 'backup'
$gatewayState = Join-Path $runRoot 'gateway'
$gatewayContainer = 'comfy-lj-gateway-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$integrationServerContainer = 'comfy-lj-server-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$integrationServerAlias = 'comfy-lj-integration-server'
$integrationClientContainer = 'comfy-lj-client-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$gatewayAlias = 'comfy-lj-integration-gateway'
$image = 'lumberjacks-gateway:' + $ReleaseId

New-Item -ItemType Directory -Path $backupRoot,$gatewayState -Force | Out-Null

$liveStart = (Get-Item -LiteralPath $livePlugin).LastWriteTime.ToString('o')
$gatewayCreated = $false
$runtimeBackedUp = $false
$integrationServerCreated = $false
$integrationClientCreated = $false
$originalServerStopped = $false
$activeServerContainer = $ServerContainer
$proofPassed = $false

function Invoke-Native {
  param([string] $File, [string[]] $Arguments)
  & $File @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$File failed with exit $LASTEXITCODE" }
}

function Write-NoBom([string] $Path, [string] $Text) {
  Write-ComfyUtf8NoBom -Path $Path -Text $Text
}

function Read-SharedText([string] $Path) {
  $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
  $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
  try {
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
  } finally { $stream.Dispose() }
}

function Merge-Config([string] $Path, [string] $Section, [hashtable] $Values) {
  $text = [IO.File]::ReadAllText($Path)
  $updated = Merge-ComfyBepInExSection -Text $text -Section $Section -Values $Values
  Write-NoBom $Path $updated
}

function Wait-Http([string] $Url, [int] $Seconds) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  do {
    try { return Invoke-RestMethod -UseBasicParsing -Uri $Url -TimeoutSec 3 }
    catch { Start-Sleep -Seconds 1 }
  } while ((Get-Date) -lt $deadline)
  throw "HTTP timeout: $Url"
}

function Capture-DockerLogs([string] $Container, [string] $Path) {
  $stdout = $Path + '.stdout.tmp'
  $stderr = $Path + '.stderr.tmp'
  $docker = (Get-Command docker).Source
  $process = Start-Process -FilePath $docker -ArgumentList @('logs', $Container) -Wait -PassThru `
      -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  $text = ''
  if (Test-Path -LiteralPath $stdout) { $text += [IO.File]::ReadAllText($stdout) }
  if (Test-Path -LiteralPath $stderr) { $text += [IO.File]::ReadAllText($stderr) }
  Write-NoBom $Path $text
  Remove-Item -LiteralPath $stdout,$stderr -Force -ErrorAction SilentlyContinue
  if ($process.ExitCode -ne 0) { throw "docker logs failed for $Container" }
}

try {
  Write-Host '=== effective integration configuration ===' -ForegroundColor Cyan
  $configView = [ordered]@{
    gateway_address = 'http://' + $gatewayAlias + ':4000'
    valheim_server_address = $integrationServerAlias + ':2456'
    valheim_server_password = '(configured, redacted)'
    transport = 'http'
    caller_identity = 'private-plane (Gateway-derived)'
    recipient = 'legacy (Gateway-scoped)'
    mod_release = $ReleaseId
    expected_release = $ReleaseId
    submission_schema_version = 2
    consumer_delivery_schema_version = 1
    consumer_enabled = $true
    consumer_available = 'headless Valheim client after join'
    comfy_submission_enabled = $true
    importance_filtering_enabled = $true
    importance_max_priority_rank = 4
    result_ack_mode = 'RPC_ZDOData readback + sequence ack + correlated consumer telemetry'
  }
  $configView | Format-List | Out-Host
  Write-NoBom (Join-Path $runRoot 'effective-config.json') (($configView | ConvertTo-Json -Depth 5) + "`n")

  if (!$SkipBuild) {
    Write-Host '=== build exact Comfy Release DLL (isolated output) ===' -ForegroundColor Cyan
    Invoke-Native 'dotnet' @('build', $modProject, '-c', 'Release', '--nologo',
        '-p:PluginOutputPath=C:\__comfy_cut_no_plugin_copy__')

    Write-Host '=== build exact Gateway image ===' -ForegroundColor Cyan
    Push-Location $LumberjacksRoot
    try {
      Invoke-Native 'docker' @('build', '--target', 'gateway', '-t', $image,
          '--build-arg', ('LUMBERJACKS_EXPECTED_MOD_RELEASE=' + $ReleaseId),
          '--build-arg', 'LUMBERJACKS_REQUIRE_RELEASE=1', '.')
    } finally { Pop-Location }
  }

  . $releaseReader
  $dllRelease = Get-AssemblyMetadataValue -DllPath $modDll -Key 'LumberjacksModReleaseId'
  if ($dllRelease -ne $ReleaseId) { throw "built DLL release '$dllRelease' does not match '$ReleaseId'" }
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $imageVerifier `
      -Image $image -ExpectedRelease $ReleaseId
  if ($LASTEXITCODE -ne 0) { throw 'Gateway image release verification failed' }

  $dllHash = Get-ComfySha256 $modDll
  $imageId = (& docker image inspect $image --format '{{.Id}}').Trim()
  if ($LASTEXITCODE -ne 0) { throw 'docker image inspect failed' }
  $payloadHashLine = (& docker run --rm --entrypoint sha256sum $image /app/Game.Gateway.dll).Trim()
  if ($LASTEXITCODE -ne 0) { throw 'Gateway payload hash failed' }
  $gatewayPayloadHash = ($payloadHashLine -split '\s+')[0]
  $artifactView = [ordered]@{
    comfy_commit = (& git -C $ComfyRoot rev-parse HEAD).Trim()
    lumberjacks_commit = (& git -c safe.directory=C:/work/Lumberjacks -C $LumberjacksRoot rev-parse HEAD).Trim()
    dll_sha256 = $dllHash
    gateway_image_id = $imageId
    gateway_payload_sha256 = $gatewayPayloadHash
    effective_release = $ReleaseId
    submission_schema_version = 2
  }
  Write-NoBom (Join-Path $runRoot 'artifacts.json') (($artifactView | ConvertTo-Json) + "`n")
  $artifactView | Format-List | Out-Host

  $clientState = (& docker inspect $ClientContainer --format '{{.State.Running}}').Trim()
  if ($LASTEXITCODE -ne 0) { throw "missing client container $ClientContainer" }
  if ($clientState -ne 'false') { throw 'headless client must be stopped before the integration run' }
  $clientImage = (& docker inspect $ClientContainer --format '{{.Config.Image}}').Trim()
  if ($LASTEXITCODE -ne 0 -or !$clientImage) { throw "could not resolve image for $ClientContainer" }
  $serverState = (& docker inspect $ServerContainer --format '{{.State.Running}}').Trim()
  if ($serverState -ne 'true') { throw 'local dedicated server must already be running' }
  $serverImage = (& docker inspect $ServerContainer --format '{{.Config.Image}}').Trim()
  if ($LASTEXITCODE -ne 0 -or !$serverImage) { throw "could not resolve image for $ServerContainer" }
  $serverEnvironmentJson = (& docker inspect $ServerContainer --format '{{json .Config.Env}}' | Out-String).Trim()
  if ($LASTEXITCODE -ne 0 -or !$serverEnvironmentJson) {
    throw "could not resolve environment for $ServerContainer"
  }
  $serverEnvironment = $serverEnvironmentJson | ConvertFrom-Json

  Copy-Item -LiteralPath $serverCfg -Destination (Join-Path $backupRoot 'server.cfg')
  Copy-Item -LiteralPath $serverSeedPlugin -Destination (Join-Path $backupRoot 'server-seed.dll')
  Copy-Item -LiteralPath $clientSharedCfg -Destination (Join-Path $backupRoot 'client-shared.cfg')
  Copy-Item -LiteralPath $clientSharedPlugin -Destination (Join-Path $backupRoot 'client-shared.dll')
  Copy-Item -LiteralPath $clientCfg -Destination (Join-Path $backupRoot 'client-runtime.cfg')
  Copy-Item -LiteralPath $clientPlugin -Destination (Join-Path $backupRoot 'client-runtime.dll')
  Invoke-Native 'docker' @('cp', ($ServerContainer + ':' + $serverRuntimePlugin),
      (Join-Path $backupRoot 'server-runtime.dll'))
  $runtimeBackedUp = $true

  Copy-Item -LiteralPath $modDll -Destination $serverSeedPlugin -Force
  Copy-Item -LiteralPath $modDll -Destination $clientSharedPlugin -Force
  Copy-Item -LiteralPath $modDll -Destination $clientPlugin -Force
  Invoke-Native 'docker' @('cp', $modDll, ($ServerContainer + ':' + $serverRuntimePlugin))

  $gatewayUrl = 'http://' + $gatewayAlias + ':4000'
  Merge-Config $serverCfg 'Lumberjacks' @{
    lumberjacksGatewayUrl = $gatewayUrl
    lumberjacksCutoverMode = 'lumberjacks-primary'
    lumberjacksEnrollmentManifestId = $window
    lumberjacksAuthoritativeWindowId = $window
    zdoAuthoritativeConsumerEnabled = 'false'
    lumberjacksTelemetryHeartbeatEnabled = 'true'
  }
  Merge-Config $serverCfg 'Netcode' @{
    zdoRedirectEnabled = 'true'
    zdoRedirectPrefabs = '*'
    zdoRedirectEndpoint = $gatewayUrl
    zdoRedirectWindowId = $window
    zdoRedirectActiveSeconds = '0'
    zdoRedirectMaxPriorityRank = '4'
    netcodeProbeMaxDetailRows = '200000'
  }
  Merge-Config $clientSharedCfg 'Lumberjacks' @{
    lumberjacksGatewayUrl = $gatewayUrl
    lumberjacksAuthoritativeWindowId = $window
    zdoAuthoritativeConsumerEnabled = 'true'
    lumberjacksTelemetryHeartbeatEnabled = 'true'
  }
  Merge-Config $clientSharedCfg 'Netcode' @{ zdoRedirectEnabled = 'false' }
  Merge-Config $clientCfg 'Lumberjacks' @{
    lumberjacksGatewayUrl = $gatewayUrl
    lumberjacksAuthoritativeWindowId = $window
    zdoAuthoritativeConsumerEnabled = 'true'
    lumberjacksTelemetryHeartbeatEnabled = 'true'
  }
  Merge-Config $clientCfg 'Netcode' @{ zdoRedirectEnabled = 'false' }

  Write-Host '=== start isolated Gateway ===' -ForegroundColor Cyan
  Invoke-Native 'docker' @('run', '-d', '--name', $gatewayContainer,
      '--network', $LabNetwork, '--network-alias', $gatewayAlias,
      '-p', '127.0.0.1::4000', '-e', 'Urls=http://+:4000',
      '-e', 'VALHEIM_ZDO_QUEUE_PATH=/state/redirect.wal',
      '-e', 'ValheimQueue__ProducerEmitsRecipients=false',
      '-v', ($gatewayState + ':/state'), $image)
  $gatewayCreated = $true
  $portLine = (& docker port $gatewayContainer '4000/tcp').Trim()
  if ($portLine -notmatch ':(\d+)$') { throw "could not resolve Gateway host port: $portLine" }
  $gatewayHost = 'http://127.0.0.1:' + $Matches[1]
  [void](Wait-Http ($gatewayHost + '/health') 90)

  # Supplemental negative admission check: same ingress, wrong release, no queue mutation.
  $wrongBody = @{
    schema_version = 2; source_instance = 'negative-release-probe';
    mod_release = 'm4-integration-20260719-r0'; operation = 'zdo_redirect';
    window_id = $window; payload = @()
  } | ConvertTo-Json -Depth 6
  $wrongStatus = 0
  try { Invoke-WebRequest -UseBasicParsing -Method Post -Uri ($gatewayHost + '/valheim/zdo-redirect/receipts') `
      -ContentType 'application/json' -Body $wrongBody -TimeoutSec 10 | Out-Null }
  catch { if ($_.Exception.Response) { $wrongStatus = [int]$_.Exception.Response.StatusCode } }
  if ($wrongStatus -ne 409) { throw "wrong-release probe expected 409, got $wrongStatus" }

  Write-Host '=== start isolated real dedicated server with exact DLL ===' -ForegroundColor Cyan
  Invoke-Native 'docker' @('stop', '-t', '120', $ServerContainer)
  $originalServerStopped = $true
  $serverRunArguments = @('run', '-d', '--name', $integrationServerContainer,
      '--network', $LabNetwork, '--network-alias', $integrationServerAlias,
      '--hostname', 'valheim-server-integration', '--cap-add', 'sys_nice',
      '--volumes-from', $ServerContainer)
  foreach ($entry in $serverEnvironment) {
    $name = ([string]$entry -split '=', 2)[0]
    if ($name -notin @('CROSSPLAY', 'SERVER_PUBLIC', 'BACKUPS', 'UPDATE_IF_IDLE', 'RESTART_IF_IDLE')) {
      $serverRunArguments += @('-e', [string]$entry)
    }
  }
  $serverRunArguments += @('-e', 'CROSSPLAY=false', '-e', 'SERVER_PUBLIC=false',
      '-e', 'BACKUPS=false', '-e', 'UPDATE_IF_IDLE=false', '-e', 'RESTART_IF_IDLE=false',
      $serverImage)
  Invoke-Native 'docker' $serverRunArguments
  $integrationServerCreated = $true
  $activeServerContainer = $integrationServerContainer
  $serverReadyDeadline = (Get-Date).AddMinutes(4)
  do {
    $serverLogs = (& docker logs --tail 300 $activeServerContainer 2>&1 | Out-String)
    if ($serverLogs -match ('Lumberjacks contract release=' + [regex]::Escape($ReleaseId))) { break }
    Start-Sleep -Seconds 3
  } while ((Get-Date) -lt $serverReadyDeadline)
  if ($serverLogs -notmatch ('Lumberjacks contract release=' + [regex]::Escape($ReleaseId))) {
    throw 'dedicated server did not log the explicit integration contract'
  }
  $loadedServerHashLine = (& docker exec $activeServerContainer sha256sum $serverRuntimePlugin).Trim()
  $loadedServerHash = ($loadedServerHashLine -split '\s+')[0]
  if ($loadedServerHash -ne $dllHash) {
    throw "running server DLL hash $loadedServerHash does not equal built hash $dllHash"
  }

  Write-Host '=== start isolated real headless Valheim consumer ===' -ForegroundColor Cyan
  $clientLogBefore = if (Test-Path -LiteralPath $clientLog) {
    (Get-Item -LiteralPath $clientLog).LastWriteTimeUtc
  } else { [DateTime]::MinValue }
  $connectionLogBefore = (Get-Item -LiteralPath $clientConnectionLog).LastWriteTimeUtc
  $connectionLogLengthBefore = (Read-SharedText $clientConnectionLog).Length
  Invoke-Native 'docker' @('run', '-d', '--name', $integrationClientContainer,
      '--network', $LabNetwork, '--hostname', 'valheim-client-integration', '--privileged',
      '--ipc=host', '--shm-size=2g', '-e', 'TZ=America/Los_Angeles',
      '-e', 'USER_LOCALES=en_US.UTF-8 UTF-8', '-e', 'DISPLAY=:55', '-e', 'MODE=primary',
      '-e', 'WEB_UI_MODE=vnc', '-e', 'ENABLE_VNC_AUDIO=false', '-e', 'ENABLE_STEAM=true',
      '-e', 'STEAM_ARGS=-silent', '-e', 'ENABLE_SUNSHINE=false',
      '-e', 'ENABLE_EVDEV_INPUTS=false', '-e', 'FORCE_X11_DUMMY_CONFIG=true',
      '-e', 'COMFY_STEAM_AUTOSTART=false', '-e', 'COMFY_AUTOJOIN=true',
      '-e', 'COMFY_AUTOJOIN_INDEX=0',
      '-v', ($clientSharedRoot + ':/mnt/comfy:ro'),
      '-v', ($clientHomeRoot + ':/home/default'),
      '-v', ($clientGamesRoot + ':/mnt/games'), $clientImage)
  $integrationClientCreated = $true

  # The stock image owns the single Steam process. Wait for a fresh authenticated session, then
  # launch the real Linux game through BepInEx's required Doorstop wrapper. A normal Steam
  # -applaunch starts valheim.x86_64 directly and therefore cannot prove that the mod loaded.
  $launchDeadline = (Get-Date).AddMinutes($ClientLaunchTimeoutMinutes)
  $steamReady = $false
  do {
    Start-Sleep -Seconds 5
    $running = (& docker inspect $integrationClientContainer --format '{{.State.Running}}').Trim()
    if ($running -ne 'true') { throw 'headless client container exited before Steam authentication' }
    $connectionLogItem = Get-Item -LiteralPath $clientConnectionLog
    if ($connectionLogItem.LastWriteTimeUtc -gt $connectionLogBefore) {
      $connectionText = Read-SharedText $clientConnectionLog
      $connectionTail = if ($connectionText.Length -ge $connectionLogLengthBefore) {
        $connectionText.Substring($connectionLogLengthBefore)
      } else { $connectionText }
      if ($connectionTail -match '\[Logged On[^\r\n]*processing complete') {
        $steamReady = $true
        break
      }
    }
  } while ((Get-Date) -lt $launchDeadline)
  if (!$steamReady) { throw 'headless client Steam session did not authenticate before launch timeout' }

  Invoke-Native 'docker' @('exec', '-d', '--user', 'default', '--env', 'DISPLAY=:55',
      '--workdir', '/mnt/games/GameLibrary/Steam/steamapps/common/Valheim',
      $integrationClientContainer, './start_game_bepinex.sh', '-console',
      '+connect', ($integrationServerAlias + ':2456'), '+password', $ServerPassword)

  $gameLaunchDeadline = (Get-Date).AddMinutes(4)
  $clientContract = 'Lumberjacks contract release=' + $ReleaseId
  $clientLaunched = $false
  do {
    Start-Sleep -Seconds 5
    $running = (& docker inspect $integrationClientContainer --format '{{.State.Running}}').Trim()
    if ($running -ne 'true') { throw 'headless Valheim client exited before game launch' }
    if (Test-Path -LiteralPath $clientLog) {
      $clientLogItem = Get-Item -LiteralPath $clientLog
      if ($clientLogItem.LastWriteTimeUtc -gt $clientLogBefore) {
        $clientText = Read-SharedText $clientLog
        if ($clientText -match [regex]::Escape($clientContract)) {
          $clientLaunched = $true
          break
        }
      }
    }
  } while ((Get-Date) -lt $gameLaunchDeadline)
  if (!$clientLaunched) {
    throw 'real Valheim client did not launch and log the explicit integration contract'
  }
  $loadedClientHash = Get-ComfySha256 $clientPlugin
  if ($loadedClientHash -ne $dllHash) {
    throw "running client DLL hash $loadedClientHash does not equal built hash $dllHash"
  }

  # Current Linux Valheim accepts +connect but does not consume +password. Wait until the real
  # Steam socket is connected and vanilla presents its focused password field, then type into
  # that dialog. This preserves the real vanilla password gate; it is UI automation, not an auth
  # bypass or a parallel transport.
  $passwordDeadline = (Get-Date).AddMinutes(4)
  $passwordDialogReady = $false
  do {
    Start-Sleep -Seconds 2
    $clientText = Read-SharedText $clientLog
    if ($clientText -match 'Got connection SteamID') {
      $passwordDialogReady = $true
      break
    }
  } while ((Get-Date) -lt $passwordDeadline)
  if (!$passwordDialogReady) { throw 'real Valheim password dialog was not reached after socket connection' }
  Start-Sleep -Seconds 2
  Invoke-Native 'docker' @('exec', '--user', 'default', '--env', 'DISPLAY=:55',
      $integrationClientContainer, 'xdotool', 'type', '--clearmodifiers', '--delay', '25',
      $ServerPassword)
  Invoke-Native 'docker' @('exec', '--user', 'default', '--env', 'DISPLAY=:55',
      $integrationClientContainer, 'xdotool', 'key', 'Return')
  Write-Host 'Submitted the configured password through the real Valheim dialog.'

  $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
  $status = $null
  $consumer = $null
  do {
    Start-Sleep -Seconds 5
    $running = (& docker inspect $integrationClientContainer --format '{{.State.Running}}').Trim()
    if ($running -ne 'true') { throw 'headless Valheim client exited before producing proof' }
    try { $status = Invoke-RestMethod -UseBasicParsing -Uri ($gatewayHost + '/valheim/zdo-redirect/status/' + $window) -TimeoutSec 5 }
    catch { $status = $null }
    try { $consumer = Invoke-RestMethod -UseBasicParsing -Uri ($gatewayHost + '/api/v0/valheim/zdo-consumers/' + $window) -TimeoutSec 5 }
    catch { $consumer = $null }
    if ($consumer -and $consumer.first_correlation_id -and
        ([long]$status.acknowledged -gt 0) -and
        ([long]$consumer.priority_fast_lane_applied -gt 0)) { break }
  } while ((Get-Date) -lt $deadline)
  if (!$consumer -or !$consumer.first_correlation_id) { throw 'consumer never exposed a correlated result' }
  if ([long]$status.acknowledged -le 0) { throw 'Gateway never observed a consumer acknowledgement' }

  Write-NoBom (Join-Path $runRoot 'gateway-status.json') (($status | ConvertTo-Json -Depth 8) + "`n")
  Write-NoBom (Join-Path $runRoot 'consumer-status.json') (($consumer | ConvertTo-Json -Depth 8) + "`n")
  Capture-DockerLogs $gatewayContainer (Join-Path $runRoot 'gateway-full.log')
  $gatewayRelevant = Get-Content -LiteralPath (Join-Path $runRoot 'gateway-full.log') |
      Where-Object { $_ -match 'ComfyLumberjacksIntegration' }
  Write-NoBom (Join-Path $runRoot 'gateway-contract.log') (($gatewayRelevant -join "`n") + "`n")

  Invoke-Native 'docker' @('cp', ($activeServerContainer + ':' + $serverRuntimeLog),
      (Join-Path $runRoot 'server-full.log'))
  $serverRelevant = Get-Content -LiteralPath (Join-Path $runRoot 'server-full.log') |
      Where-Object { $_ -match 'ComfyNetworkSense' }
  Write-NoBom (Join-Path $runRoot 'server-contract.log') (($serverRelevant -join "`n") + "`n")
  $clientRelevant = (Read-SharedText $clientLog) -split "`r?`n" |
      Where-Object { $_ -match 'ComfyNetworkSense' }
  Write-NoBom (Join-Path $runRoot 'client-contract.log') (($clientRelevant -join "`n") + "`n")
  Copy-Item -LiteralPath $serverRedirect -Destination (Join-Path $runRoot 'redirect-send.jsonl') -Force

  $verdictOutput = & $python $verifier `
      --server-jsonl (Join-Path $runRoot 'redirect-send.jsonl') `
      --server-log (Join-Path $runRoot 'server-contract.log') `
      --client-log (Join-Path $runRoot 'client-contract.log') `
      --gateway-log (Join-Path $runRoot 'gateway-contract.log') `
      --status (Join-Path $runRoot 'gateway-status.json') `
      --consumer (Join-Path $runRoot 'consumer-status.json') `
      --window $window --release $ReleaseId
  if ($LASTEXITCODE -ne 0) { throw 'correlated integration verifier failed' }
  $verdictOutput | Out-Host
  Write-NoBom (Join-Path $runRoot 'verdict.json') (($verdictOutput -join "`n") + "`n")
  $proofPassed = $true
}
finally {
  Write-Host '=== restore local lab state ===' -ForegroundColor Cyan
  if ($integrationClientCreated) {
    try { & docker rm -f $integrationClientContainer | Out-Null } catch { Write-Warning $_ }
  }
  if ($integrationServerCreated) {
    try { & docker stop -t 120 $integrationServerContainer | Out-Null } catch { Write-Warning $_ }
    try { & docker rm $integrationServerContainer | Out-Null } catch { Write-Warning $_ }
  }
  if ($runtimeBackedUp) {
    try {
      Copy-Item -LiteralPath (Join-Path $backupRoot 'server.cfg') -Destination $serverCfg -Force
      Copy-Item -LiteralPath (Join-Path $backupRoot 'server-seed.dll') -Destination $serverSeedPlugin -Force
      Copy-Item -LiteralPath (Join-Path $backupRoot 'client-shared.cfg') -Destination $clientSharedCfg -Force
      Copy-Item -LiteralPath (Join-Path $backupRoot 'client-shared.dll') -Destination $clientSharedPlugin -Force
      Copy-Item -LiteralPath (Join-Path $backupRoot 'client-runtime.cfg') -Destination $clientCfg -Force
      Copy-Item -LiteralPath (Join-Path $backupRoot 'client-runtime.dll') -Destination $clientPlugin -Force
      & docker cp (Join-Path $backupRoot 'server-runtime.dll') ($ServerContainer + ':' + $serverRuntimePlugin) | Out-Null
      if ($originalServerStopped) {
        & docker start $ServerContainer | Out-Null
      } else {
        & docker exec $ServerContainer /usr/local/bin/supervisorctl restart valheim-server | Out-Null
      }
    } catch { Write-Warning ("runtime restore failed: " + $_) }
  }
  if ($gatewayCreated) {
    try { & docker rm -f $gatewayContainer | Out-Null } catch { Write-Warning $_ }
  }
  $liveEnd = (Get-Item -LiteralPath $livePlugin).LastWriteTime.ToString('o')
  Write-NoBom (Join-Path $runRoot 'tripwire.json') (([ordered]@{
      start = $liveStart; end = $liveEnd; unchanged = ($liveStart -eq $liveEnd)
    } | ConvertTo-Json) + "`n")
  if ($liveStart -ne $liveEnd) { throw 'Windows live Steam plugin LastWriteTime changed' }
}

if (!$proofPassed) { throw 'integration proof did not pass' }
Write-Host "PASS: real correlated positive and Importance-rejected paths. Evidence: $runRoot" -ForegroundColor Green
