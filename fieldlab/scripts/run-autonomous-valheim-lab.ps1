param(
  [ValidateRange(0, 4)]
  [int]$Clients = 1,

  [string]$Profile = "host_full",
  [string]$WorldName = "ComfyEra16",
  [string]$WorldSourceDir = (Join-Path $env:USERPROFILE "AppData\LocalLow\IronGate\Valheim\worlds_local"),
  [string]$ServerName = "Comfy Era16 Lab",
  [string]$ServerPassword = "comfytest",
  [string]$DockerProject = "comfy-valheim-lab",

  [switch]$ApplyWslLimits,
  [switch]$ShutdownWsl,
  [switch]$UseWslDocker,
  [switch]$Start,
  [switch]$StopAfter,
  [switch]$SkipBuild,
  [switch]$SkipPackets,

  [int]$TimeoutMinutes = 90
)

$ErrorActionPreference = "Stop"

$fieldLabRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot = (Resolve-Path (Join-Path $fieldLabRoot "..")).Path
$autonomousRoot = Join-Path $fieldLabRoot "autonomous"
$stateRoot = Join-Path $autonomousRoot "state"
$composePath = Join-Path $autonomousRoot "valheim-lab.compose.yml"
$runId = "$(Get-Date -Format yyyyMMdd-HHmmss)-valheim-autonomous-lab"
$runDir = Join-Path $fieldLabRoot "runs\$runId"
$rawDir = Join-Path $runDir "raw"
$telemetryDir = Join-Path $runDir "telemetry"

$dockerDesktopBin = "C:\Program Files\Docker\Docker\resources\bin"
if ((Test-Path $dockerDesktopBin) -and (($env:Path -split ";") -notcontains $dockerDesktopBin)) {
  $env:Path = "$dockerDesktopBin;$env:Path"
}

New-Item -ItemType Directory -Force $rawDir, $telemetryDir | Out-Null

function Write-JsonFile {
  param(
    [Parameter(Mandatory = $true)]
    [object]$Value,

    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  $Value | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $Path
}

function Convert-ToComposePath {
  param([Parameter(Mandatory = $true)][string]$Path)

  $resolved = (Resolve-Path $Path).Path
  if ($UseWslDocker) {
    $drive = $resolved.Substring(0, 1).ToLowerInvariant()
    $rest = $resolved.Substring(2).Replace("\", "/")
    return "/mnt/$drive$rest"
  }

  return $resolved.Replace("\", "/")
}

function Invoke-Captured {
  param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [Parameter(Mandatory = $true)]
    [string[]]$ArgumentList,

    [Parameter(Mandatory = $true)]
    [string]$Name
  )

  $started = Get-Date
  $output = @()
  $exitCode = 0
  try {
    $output = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
  } catch {
    $output += $_.Exception.Message
    $exitCode = 1
  }

  return [ordered]@{
    name = $Name
    file = $FilePath
    arguments = $ArgumentList
    started_at = $started.ToUniversalTime().ToString("o")
    finished_at = (Get-Date).ToUniversalTime().ToString("o")
    exit_code = $exitCode
    output = $output
  }
}

function Get-DockerCommand {
  if ($UseWslDocker) {
    return [pscustomobject]@{
      file = "wsl.exe"
      prefix = @("--", "docker")
      mode = "wsl"
    }
  }

  $dockerCommand = Get-Command "docker" -ErrorAction SilentlyContinue
  $dockerPath = if ($dockerCommand) {
    $dockerCommand.Source
  } else {
    $defaultDockerPath = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
    if (Test-Path $defaultDockerPath) {
      $defaultDockerPath
    } else {
      "docker"
    }
  }

  return [pscustomobject]@{
    file = $dockerPath
    prefix = @()
    mode = "windows"
  }
}

function Test-ComposeServicesRunning {
  param(
    [Parameter(Mandatory = $true)]
    [object]$Docker,

    [Parameter(Mandatory = $true)]
    [string[]]$ComposeBaseArgs,

    [Parameter(Mandatory = $true)]
    [string[]]$Services
  )

  $psArgs = @()
  $psArgs += $ComposeBaseArgs
  $psArgs += @("ps", "--format", "json")

  $output = @()
  try {
    $output = @(& $Docker.file @psArgs 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
      return [ordered]@{
        ok = $false
        output = $output
        running_services = @()
        missing_services = $Services
      }
    }
  } catch {
    return [ordered]@{
      ok = $false
      output = @($_.Exception.Message)
      running_services = @()
      missing_services = $Services
    }
  }

  $records = @()
  foreach ($line in $output) {
    if ([string]::IsNullOrWhiteSpace($line)) {
      continue
    }
    try {
      $records += ($line | ConvertFrom-Json)
    } catch {
      # Docker Compose v2 may emit a JSON array instead of JSONL.
      try {
        $parsed = ($output -join "`n") | ConvertFrom-Json
        $records = @($parsed)
        break
      } catch {
        return [ordered]@{
          ok = $false
          output = $output
          running_services = @()
          missing_services = $Services
        }
      }
    }
  }

  $runningServices = @($records |
    Where-Object { $_.Service -and ($_.State -eq "running" -or $_.Status -match "^Up") } |
    ForEach-Object { [string]$_.Service })
  $missing = @($Services | Where-Object { $runningServices -notcontains $_ })

  return [ordered]@{
    ok = ($missing.Count -eq 0)
    output = $output
    running_services = $runningServices
    missing_services = $missing
  }
}

function Find-LatestScenarioRun {
  param([string]$ScenarioId)

  $runsRoot = Join-Path $fieldLabRoot "runs"
  if (-not (Test-Path $runsRoot -PathType Container)) {
    return $null
  }

  Get-ChildItem -LiteralPath $runsRoot -Directory |
    Where-Object { $_.Name -like "*-$ScenarioId" } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
}

function Write-NetworkSenseConfig {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [bool]$AutoRehearsalEnabled
  )

  $parent = Split-Path -Parent $Path
  New-Item -ItemType Directory -Force $parent | Out-Null

  @(
    "[General]",
    "isModEnabled = true",
    "",
    "[Logging]",
    "writeTelemetryLogs = true",
    "",
    "[Sampling]",
    "liveSampleIntervalSeconds = 0.5",
    "serverPulseIntervalSeconds = 2",
    "serverHeartbeatWorldZdoCountEnabled = false",
    "serverHeartbeatWorldZdoCountIntervalSeconds = 300",
    "nearbyRadiusMeters = 96",
    "buildScanRadiusMeters = 128",
    "sceneScanEnabled = true",
    "sceneScanIntervalSeconds = 2",
    "",
    "[Benchmark]",
    "benchmarkDurationSeconds = 60",
    "",
    "[Perf]",
    "perfProbeEnabled = true",
    "hitchThresholdMs = 250",
    "severeHitchThresholdMs = 2000",
    "sectionWarnThresholdMs = 25",
    "engineLogProbeEnabled = true",
    "worldZdoCountOnSevereHitchEnabled = false",
    "perfSampleIntervalSeconds = 1",
    "",
    "[Automation]",
    "autoRehearsalEnabled = $($AutoRehearsalEnabled.ToString().ToLowerInvariant())",
    "autoRehearsalRouteFile = teleport-route.tsv",
    "autoRehearsalProfile = $Profile",
    "autoRehearsalDelaySeconds = 20",
    "autoRehearsalRunOncePerSession = true",
    "",
    "[HUD]",
    "showHudOnStart = true",
    "hudPreset = Minimal"
  ) | Set-Content -Encoding UTF8 $Path
}

function Get-ClientInstallRoot {
  param([int]$ClientIndex)

  $clientName = "client{0:00}" -f $ClientIndex
  return Join-Path $stateRoot "$clientName\games\GameLibrary\Steam\steamapps\common\Valheim"
}

function Get-ClientLogRoot {
  param([int]$ClientIndex)

  return Join-Path (Get-ClientInstallRoot -ClientIndex $ClientIndex) "BepInEx\config\comfy-network-sense"
}

function Copy-ValheimWorldSave {
  param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,

    [Parameter(Mandatory = $true)]
    [string]$DestinationDir,

    [Parameter(Mandatory = $true)]
    [string]$WorldName
  )

  New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null

  if (-not (Test-Path $SourceDir -PathType Container)) {
    throw "Valheim world source directory was not found: $SourceDir"
  }

  $requiredFiles = @(
    "$WorldName.db",
    "$WorldName.fwl"
  )
  $optionalFiles = @(
    "$WorldName`_heightTexCache",
    "$WorldName`_mapTexCache",
    "$WorldName`_forestMaskTexCache"
  )

  $copied = New-Object System.Collections.Generic.List[object]
  foreach ($fileName in $requiredFiles) {
    $sourcePath = Join-Path $SourceDir $fileName
    if (-not (Test-Path $sourcePath -PathType Leaf)) {
      throw "Required Valheim world file was not found: $sourcePath"
    }

    $destinationPath = Join-Path $DestinationDir $fileName
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    $item = Get-Item -LiteralPath $sourcePath
    $copied.Add([ordered]@{
        name = $fileName
        source = $sourcePath
        destination = $destinationPath
        bytes = $item.Length
        source_last_write_time_utc = $item.LastWriteTimeUtc.ToString("o")
        required = $true
      }) | Out-Null
  }

  foreach ($fileName in $optionalFiles) {
    $sourcePath = Join-Path $SourceDir $fileName
    if (-not (Test-Path $sourcePath -PathType Leaf)) {
      continue
    }

    $destinationPath = Join-Path $DestinationDir $fileName
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    $item = Get-Item -LiteralPath $sourcePath
    $copied.Add([ordered]@{
        name = $fileName
        source = $sourcePath
        destination = $destinationPath
        bytes = $item.Length
        source_last_write_time_utc = $item.LastWriteTimeUtc.ToString("o")
        required = $false
      }) | Out-Null
  }

  return [ordered]@{
    world_name = $WorldName
    source_dir = $SourceDir
    destination_dir = $DestinationDir
    copied_files = @($copied.ToArray())
  }
}

function Test-RehearsalMarker {
  param([datetime]$NotBeforeUtc = [datetime]::MinValue)

  $notBefore = $NotBeforeUtc.ToUniversalTime()
  $eventFiles = @(Get-ChildItem -LiteralPath $stateRoot -Recurse -Filter "event-timeline.jsonl" -ErrorAction SilentlyContinue)
  foreach ($file in $eventFiles) {
    if ($file.LastWriteTimeUtc -lt $notBefore) {
      continue
    }

    $matches = @(Select-String -LiteralPath $file.FullName -Pattern "auto_rehearsal end|route_rehearsal .* end" -ErrorAction SilentlyContinue)
    if ($matches.Count -gt 0) {
      return [ordered]@{
        found = $true
        path = $file.FullName
        line = $matches[-1].Line
      }
    }
  }

  return [ordered]@{
    found = $false
    path = $null
    line = $null
  }
}

function Wait-ForRehearsalMarker {
  param(
    [int]$TimeoutMinutes,
    [datetime]$NotBeforeUtc = [datetime]::MinValue
  )

  $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
  do {
    $marker = Test-RehearsalMarker -NotBeforeUtc $NotBeforeUtc
    if ($marker.found) {
      return $marker
    }
    Start-Sleep -Seconds 10
  } while ((Get-Date) -lt $deadline)

  return Test-RehearsalMarker -NotBeforeUtc $NotBeforeUtc
}

$steps = New-Object System.Collections.Generic.List[object]
$warnings = New-Object System.Collections.Generic.List[string]

if ($ServerPassword.Length -lt 5) {
  throw "Valheim server password must be at least 5 characters."
}

foreach ($path in @(
    $stateRoot,
    (Join-Path $stateRoot "server\config\bepinex\plugins"),
    (Join-Path $stateRoot "server\config\bepinex\config"),
    (Join-Path $stateRoot "server\config\worlds_local"),
    (Join-Path $stateRoot "client-shared\plugins"),
    (Join-Path $stateRoot "client-shared\config"),
    (Join-Path $stateRoot "client-shared\comfy-network-sense")
  )) {
  New-Item -ItemType Directory -Force $path | Out-Null
}

for ($index = 1; $index -le 4; $index++) {
  $clientName = "client{0:00}" -f $index
  $clientHome = Join-Path $stateRoot "$clientName\home"
  $clientInit = Join-Path $clientHome "init.d"
  New-Item -ItemType Directory -Force -Path @(
    $clientHome,
    $clientInit,
    (Join-Path $stateRoot "$clientName\games")
  ) | Out-Null

  Copy-Item `
    -LiteralPath (Join-Path $autonomousRoot "client-init\20-comfy-valheim-autostart.sh") `
    -Destination (Join-Path $clientInit "20-comfy-valheim-autostart.sh") `
    -Force
}

$wslScript = Join-Path $fieldLabRoot "scripts\set-wsl-resource-profile.ps1"
$wslParams = @{
  Profile = $Profile
  Json = $true
}
if ($ApplyWslLimits) {
  $wslParams.Apply = $true
}
if ($ShutdownWsl) {
  $wslParams.ShutdownWsl = $true
}
$wslStarted = Get-Date
$wslOutput = @()
$wslExitCode = 0
try {
  $wslOutput = @(& $wslScript @wslParams 2>&1 | ForEach-Object { [string]$_ })
  $wslExitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
} catch {
  $wslOutput += $_.Exception.Message
  $wslExitCode = 1
}
$wslStep = [ordered]@{
  name = "wsl-resource-profile"
  file = $wslScript
  arguments = @($wslParams.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
  started_at = $wslStarted.ToUniversalTime().ToString("o")
  finished_at = (Get-Date).ToUniversalTime().ToString("o")
  exit_code = $wslExitCode
  output = $wslOutput
}
$steps.Add($wslStep) | Out-Null
try {
  ($wslStep.output -join "`n") | ConvertFrom-Json | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 (Join-Path $rawDir "wsl-resource-profile.json")
} catch {
  $warnings.Add("Could not parse WSL resource profile output.") | Out-Null
}

if (-not $SkipBuild) {
  $dotnet = if (Test-Path "C:\work\dotnet9\dotnet.exe") { "C:\work\dotnet9\dotnet.exe" } else { "dotnet" }
  $buildStep = Invoke-Captured `
    -FilePath $dotnet `
    -ArgumentList @("build", (Join-Path $repoRoot "network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj"), "-c", "Release") `
    -Name "build-comfy-networksense"
  $steps.Add($buildStep) | Out-Null
  if ($buildStep.exit_code -ne 0) {
    throw "ComfyNetworkSense build failed. See $rawDir."
  }
}

if (-not $SkipPackets) {
  $previousNetworkSenseLogDir = $env:FIELDLAB_NETWORKSENSE_LOG_DIR
  $previousResourceProfile = $env:FIELDLAB_RESOURCE_PROFILE
  try {
    $env:FIELDLAB_RESOURCE_PROFILE = $Profile
    $readinessStep = Invoke-Captured `
      -FilePath (Join-Path $fieldLabRoot "scripts\run-experiment.ps1") `
      -ArgumentList @((Join-Path $fieldLabRoot "scenarios\valheim-era16-volunteer-readiness-baseline.yaml")) `
      -Name "readiness-baseline"
    $steps.Add($readinessStep) | Out-Null

    $rehearsalStep = Invoke-Captured `
      -FilePath (Join-Path $fieldLabRoot "scripts\run-experiment.ps1") `
      -ArgumentList @((Join-Path $fieldLabRoot "scenarios\valheim-era16-teleport-rehearsal.yaml")) `
      -Name "teleport-rehearsal-stage"
    $steps.Add($rehearsalStep) | Out-Null
  } finally {
    $env:FIELDLAB_NETWORKSENSE_LOG_DIR = $previousNetworkSenseLogDir
    $env:FIELDLAB_RESOURCE_PROFILE = $previousResourceProfile
  }
}

$latestRehearsal = Find-LatestScenarioRun -ScenarioId "valheim-era16-teleport-rehearsal"
if (-not $latestRehearsal) {
  throw "No valheim-era16-teleport-rehearsal run found; cannot stage teleport route."
}

$routeSource = Join-Path $latestRehearsal.FullName "telemetry\teleport-route.tsv"
if (-not (Test-Path $routeSource)) {
  throw "Teleport route not found: $routeSource"
}

$routeDest = Join-Path $stateRoot "client-shared\comfy-network-sense\teleport-route.tsv"
Copy-Item -LiteralPath $routeSource -Destination $routeDest -Force
Copy-Item -LiteralPath $routeSource -Destination (Join-Path $rawDir "teleport-route.tsv") -Force

$dllSource = Join-Path $repoRoot "network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll"
if (-not (Test-Path $dllSource)) {
  throw "ComfyNetworkSense DLL not found after build: $dllSource"
}

Copy-Item -LiteralPath $dllSource -Destination (Join-Path $stateRoot "server\config\bepinex\plugins\ComfyNetworkSense.dll") -Force
Copy-Item -LiteralPath $dllSource -Destination (Join-Path $stateRoot "client-shared\plugins\ComfyNetworkSense.dll") -Force

Write-NetworkSenseConfig -Path (Join-Path $stateRoot "server\config\bepinex\config\djcdevelopment.valheim.comfynetworksense.cfg") -AutoRehearsalEnabled:$false
Write-NetworkSenseConfig -Path (Join-Path $stateRoot "client-shared\config\djcdevelopment.valheim.comfynetworksense.cfg") -AutoRehearsalEnabled:$true

$worldStageDir = Join-Path $stateRoot "server\config\worlds_local"
$worldStage = Copy-ValheimWorldSave -SourceDir $WorldSourceDir -DestinationDir $worldStageDir -WorldName $WorldName
Write-JsonFile -Value $worldStage -Path (Join-Path $rawDir "world-stage.json")

$composeRootPath = Convert-ToComposePath $autonomousRoot
$repoPath = Convert-ToComposePath $repoRoot
$composeFileForCommand = if ($UseWslDocker) { Convert-ToComposePath $composePath } else { (Resolve-Path $composePath).Path }
$envPath = Join-Path $rawDir "valheim-lab.env"

@(
  "TZ=America/Los_Angeles",
  "COMFY_ROOT=$repoPath",
  "AUTONOMOUS_ROOT=$composeRootPath",
  "VALHEIM_SERVER_IMAGE=ghcr.io/community-valheim-tools/valheim-server:latest",
  "STEAM_HEADLESS_IMAGE=josh5/steam-headless:debian",
  "COMFY_GATEWAY_IMAGE=python:3.12-slim",
  "SERVER_NAME=$ServerName",
  "WORLD_NAME=$WorldName",
  "SERVER_PASS=$ServerPassword",
  "SERVER_PUBLIC=false",
  "SERVER_PORT=2456",
  "SERVER_PORT_END=2457",
  "CROSSPLAY=false",
  "BACKUPS=true",
  "COMFY_GATEWAY_PORT=8720",
  "USER_LOCALES=en_US.UTF-8 UTF-8",
  "DISPLAY=:55",
  "WEB_UI_MODE=vnc",
  "ENABLE_VNC_AUDIO=false",
  "PORT_NOVNC_WEB=8080",
  "ENABLE_STEAM=true",
  "STEAM_ARGS=-silent",
  "ENABLE_SUNSHINE=false",
  "ENABLE_EVDEV_INPUTS=false",
  "FORCE_X11_DUMMY_CONFIG=true",
  "COMFY_STEAM_AUTOSTART=true",
  "COMFY_VALHEIM_SERVER=valheim-server:2456",
  "COMFY_VALHEIM_PASSWORD=$ServerPassword",
  "COMFY_VALHEIM_LAUNCH_ARGS=-console",
  "COMFY_VALHEIM_INSTALL_DIR=/mnt/games/GameLibrary/Steam/steamapps/common/Valheim",
  "CLIENT01_NOVNC_PORT=8081",
  "CLIENT02_NOVNC_PORT=8082",
  "CLIENT03_NOVNC_PORT=8083",
  "CLIENT04_NOVNC_PORT=8084",
  "CLIENT01_SUNSHINE_PORT=47991",
  "CLIENT02_SUNSHINE_PORT=47992",
  "CLIENT03_SUNSHINE_PORT=47993",
  "CLIENT04_SUNSHINE_PORT=47994"
) | Set-Content -Encoding UTF8 $envPath

$envPathForCommand = if ($UseWslDocker) { Convert-ToComposePath $envPath } else { (Resolve-Path $envPath).Path }

$serviceNames = @("valheim-server", "comfy-gateway")
for ($index = 1; $index -le $Clients; $index++) {
  $serviceNames += ("valheim-client-{0:00}" -f $index)
}

$docker = Get-DockerCommand
$composeBaseArgs = @()
foreach ($prefixArg in $docker.prefix) {
  $composeBaseArgs += [string]$prefixArg
}
$composeBaseArgs += @(
  "compose",
  "--env-file",
  $envPathForCommand,
  "-f",
  $composeFileForCommand,
  "-p",
  $DockerProject
)
$upArgs = @()
$upArgs += $composeBaseArgs
$upArgs += @("up", "-d")
$upArgs += $serviceNames
$downArgs = @()
$downArgs += $composeBaseArgs
$downArgs += "down"

$dockerVersionArgs = @()
foreach ($prefixArg in $docker.prefix) {
  $dockerVersionArgs += [string]$prefixArg
}
$dockerVersionArgs += @("version", "--format", "{{.Server.Version}}")
$dockerVersionStep = Invoke-Captured -FilePath $docker.file -ArgumentList $dockerVersionArgs -Name "docker-version"
$steps.Add($dockerVersionStep) | Out-Null
if ($dockerVersionStep.exit_code -ne 0) {
  $warnings.Add("Docker is not reachable through $($docker.mode) mode; staged files were still generated.") | Out-Null
}

$marker = [ordered]@{ found = $false; path = $null; line = $null }
$postRunReadiness = $null
$postRunRehearsal = $null

if ($Start) {
  $upStep = Invoke-Captured -FilePath $docker.file -ArgumentList $upArgs -Name "docker-compose-up"
  $steps.Add($upStep) | Out-Null
  if ($upStep.exit_code -ne 0) {
    $composeState = Test-ComposeServicesRunning -Docker $docker -ComposeBaseArgs $composeBaseArgs -Services $serviceNames
    $steps.Add([ordered]@{
        name = "docker-compose-up-state-after-nonzero"
        exit_code = if ($composeState.ok) { 0 } else { 1 }
        output = $composeState.output
        running_services = $composeState.running_services
        missing_services = $composeState.missing_services
      }) | Out-Null
    if ($composeState.ok) {
      $warnings.Add("docker compose up returned exit code $($upStep.exit_code), but requested services are running.") | Out-Null
    } else {
      throw "docker compose up failed with exit code $($upStep.exit_code): $($upStep.output -join ' ')"
    }
  }

  $waitNotBeforeUtc = (Get-Date).ToUniversalTime()
  if ($Clients -gt 0 -and $TimeoutMinutes -gt 0) {
    $marker = Wait-ForRehearsalMarker -TimeoutMinutes $TimeoutMinutes -NotBeforeUtc $waitNotBeforeUtc
    if (-not $marker.found) {
      $warnings.Add("Timed out waiting for auto_rehearsal/route_rehearsal completion markers.") | Out-Null
    }
  }

  $clientLogRoot = Get-ClientLogRoot -ClientIndex 1
  if ($Clients -gt 0 -and (Test-Path $clientLogRoot -PathType Container)) {
    $previousNetworkSenseLogDir = $env:FIELDLAB_NETWORKSENSE_LOG_DIR
    $previousResourceProfile = $env:FIELDLAB_RESOURCE_PROFILE
    try {
      $env:FIELDLAB_NETWORKSENSE_LOG_DIR = $clientLogRoot
      $env:FIELDLAB_RESOURCE_PROFILE = $Profile
      $postRehearsalStep = Invoke-Captured `
        -FilePath (Join-Path $fieldLabRoot "scripts\run-experiment.ps1") `
        -ArgumentList @((Join-Path $fieldLabRoot "scenarios\valheim-era16-teleport-rehearsal.yaml")) `
        -Name "post-run-teleport-rehearsal"
      $steps.Add($postRehearsalStep) | Out-Null
      $postRunRehearsal = (Find-LatestScenarioRun -ScenarioId "valheim-era16-teleport-rehearsal").Name

      $postReadinessStep = Invoke-Captured `
        -FilePath (Join-Path $fieldLabRoot "scripts\run-experiment.ps1") `
        -ArgumentList @((Join-Path $fieldLabRoot "scenarios\valheim-era16-volunteer-readiness-baseline.yaml")) `
        -Name "post-run-readiness-baseline"
      $steps.Add($postReadinessStep) | Out-Null
      $postRunReadiness = (Find-LatestScenarioRun -ScenarioId "valheim-era16-volunteer-readiness-baseline").Name
    } finally {
      $env:FIELDLAB_NETWORKSENSE_LOG_DIR = $previousNetworkSenseLogDir
      $env:FIELDLAB_RESOURCE_PROFILE = $previousResourceProfile
    }
  } elseif ($Clients -gt 0) {
    $clientInstallRoot = Get-ClientInstallRoot -ClientIndex 1
    if (Test-Path $clientInstallRoot -PathType Container) {
      $warnings.Add("Client 01 NetworkSense log directory was not found: $clientLogRoot") | Out-Null
    } else {
      $warnings.Add("Client 01 Valheim install was not found yet: $clientInstallRoot") | Out-Null
    }
  }

  if ($StopAfter) {
    $downStep = Invoke-Captured -FilePath $docker.file -ArgumentList $downArgs -Name "docker-compose-down"
    $steps.Add($downStep) | Out-Null
  }
}

$status = if ($Start -and $marker.found) {
  "pass"
} elseif ($Start -and $Clients -eq 0) {
  "server_gateway_started"
} elseif ($Start) {
  "started_waiting_for_client_marker"
} else {
  "staged_not_started"
}

$upCommandText = "{0} {1}" -f $docker.file, ([string]::Join(" ", [string[]]$upArgs))
$downCommandText = "{0} {1}" -f $docker.file, ([string]::Join(" ", [string[]]$downArgs))

$composeSummary = [ordered]@{}
$composeSummary["file"] = $composePath
$composeSummary["env_file"] = $envPath
$composeSummary["services"] = $serviceNames
$composeSummary["up_command"] = $upCommandText
$composeSummary["down_command"] = $downCommandText

$stagedPaths = [ordered]@{}
$stagedPaths["autonomous_root"] = $autonomousRoot
$stagedPaths["state_root"] = $stateRoot
$stagedPaths["route"] = $routeDest
$stagedPaths["client_config"] = Join-Path $stateRoot "client-shared\config\djcdevelopment.valheim.comfynetworksense.cfg"
$stagedPaths["server_config"] = Join-Path $stateRoot "server\config\bepinex\config\djcdevelopment.valheim.comfynetworksense.cfg"
$stagedPaths["worlds_local"] = $worldStageDir
$stagedPaths["client01_log_root"] = Get-ClientLogRoot -ClientIndex 1

$sourceRuns = [ordered]@{}
$sourceRuns["route_rehearsal"] = $latestRehearsal.Name

$observed = [ordered]@{}
$observed["rehearsal_marker"] = $marker
$observed["post_run_rehearsal"] = $postRunRehearsal
$observed["post_run_readiness"] = $postRunReadiness

$summary = [ordered]@{}
$summary["schema_version"] = 1
$summary["status"] = $status
$summary["run_id"] = $runId
$summary["generated_at_utc"] = (Get-Date).ToUniversalTime().ToString("o")
$summary["profile"] = $Profile
$summary["clients"] = $Clients
$summary["start_requested"] = [bool]$Start
$summary["stop_after"] = [bool]$StopAfter
$summary["docker_mode"] = $docker.mode
$summary["docker_project"] = $DockerProject
$summary["world_name"] = $WorldName
$summary["world_source_dir"] = $WorldSourceDir
$summary["world_stage"] = $worldStage
$summary["server_name"] = $ServerName
$summary["compose"] = $composeSummary
$summary["staged_paths"] = $stagedPaths
$summary["source_runs"] = $sourceRuns
$summary["observed"] = $observed
$summary["warnings"] = @($warnings.ToArray())
$summary["steps"] = @($steps.ToArray())

Write-JsonFile -Value $summary -Path (Join-Path $telemetryDir "autonomous-lab-summary.json")

@(
  "# Valheim Autonomous Lab",
  "",
  "Status: $status",
  "",
  "Docker up command:",
  "",
  '```powershell',
  $upCommandText,
  '```',
  "",
  "Docker down command:",
  "",
  '```powershell',
  $downCommandText,
  '```',
  "",
  "Expected in-game command path: none. Clients use ComfyNetworkSense auto rehearsal config.",
  "",
  "Client one noVNC: http://127.0.0.1:8081",
  "Gateway health: http://127.0.0.1:8720/healthz"
) | Set-Content -Encoding UTF8 (Join-Path $rawDir "operator-runbook.md")

Write-Output "Created autonomous lab packet: $runDir"
Write-Output "Status: $status"
Write-Output "Next: inspect telemetry\autonomous-lab-summary.json and raw\operator-runbook.md"
