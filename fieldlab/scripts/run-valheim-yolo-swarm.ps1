param(
  [ValidateRange(1, 4)]
  [int]$Players = 2,

  [ValidateRange(1, 4)]
  [int]$SourceClient = 1,

  [ValidateRange(0, 3600)]
  [int]$SpawnDelaySeconds = 30,

  [ValidateRange(0, 7200)]
  [int]$ObserveSeconds = 120,

  [string]$DockerProject = "comfy-valheim-lab",
  [string]$EnvPath,

  [switch]$Start,
  [switch]$ClonePlayers,
  [switch]$AllowIncompleteSource,
  [switch]$StopAfter
)

$ErrorActionPreference = "Stop"

$fieldLabRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot = (Resolve-Path (Join-Path $fieldLabRoot "..")).Path
$autonomousRoot = Join-Path $fieldLabRoot "autonomous"
$stateRoot = Join-Path $autonomousRoot "state"
$composePath = Join-Path $autonomousRoot "valheim-lab.compose.yml"
$runId = "$(Get-Date -Format yyyyMMdd-HHmmss)-valheim-yolo-swarm"
$runDir = Join-Path $fieldLabRoot "runs\$runId"
$rawDir = Join-Path $runDir "raw"
$telemetryDir = Join-Path $runDir "telemetry"

$dockerDesktopBin = "C:\Program Files\Docker\Docker\resources\bin"
if ((Test-Path $dockerDesktopBin) -and (($env:Path -split ";") -notcontains $dockerDesktopBin)) {
  $env:Path = "$dockerDesktopBin;$env:Path"
}

New-Item -ItemType Directory -Force -Path $rawDir, $telemetryDir | Out-Null

function Write-JsonFile {
  param(
    [Parameter(Mandatory = $true)]
    [object]$Value,

    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  $Value | ConvertTo-Json -Depth 30 | Set-Content -Encoding UTF8 $Path
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
  $dockerCommand = Get-Command "docker" -ErrorAction SilentlyContinue
  if ($dockerCommand) {
    return $dockerCommand.Source
  }

  $defaultDockerPath = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
  if (Test-Path $defaultDockerPath) {
    return $defaultDockerPath
  }

  return "docker"
}

function Find-LatestAutonomousEnv {
  $runsRoot = Join-Path $fieldLabRoot "runs"
  if (-not (Test-Path $runsRoot -PathType Container)) {
    return $null
  }

  $runs = @(Get-ChildItem -LiteralPath $runsRoot -Directory |
    Where-Object { $_.Name -like "*-valheim-autonomous-lab" } |
    Sort-Object LastWriteTimeUtc -Descending)
  foreach ($run in $runs) {
    $candidate = Join-Path $run.FullName "raw\valheim-lab.env"
    if (Test-Path $candidate -PathType Leaf) {
      return $candidate
    }
  }

  return $null
}

function Get-ClientName {
  param([int]$ClientIndex)
  "client{0:00}" -f $ClientIndex
}

function Get-ClientService {
  param([int]$ClientIndex)
  "valheim-client-{0:00}" -f $ClientIndex
}

function Get-ClientHome {
  param([int]$ClientIndex)
  Join-Path $stateRoot "$(Get-ClientName -ClientIndex $ClientIndex)\home"
}

function Get-ClientGames {
  param([int]$ClientIndex)
  Join-Path $stateRoot "$(Get-ClientName -ClientIndex $ClientIndex)\games"
}

function Get-ClientInstallRoot {
  param([int]$ClientIndex)
  Join-Path (Get-ClientGames -ClientIndex $ClientIndex) "GameLibrary\Steam\steamapps\common\Valheim"
}

function Get-ClientLogRoot {
  param([int]$ClientIndex)
  Join-Path (Get-ClientInstallRoot -ClientIndex $ClientIndex) "BepInEx\config\comfy-network-sense"
}

function Assert-StateChildPath {
  param([Parameter(Mandatory = $true)][string]$Path)

  $stateFull = [System.IO.Path]::GetFullPath($stateRoot)
  $pathFull = [System.IO.Path]::GetFullPath($Path)
  if (-not $pathFull.StartsWith($stateFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to mutate path outside autonomous state root: $pathFull"
  }
}

function Sync-Directory {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [Parameter(Mandatory = $true)]
    [string]$Name
  )

  if (-not (Test-Path $Source -PathType Container)) {
    throw "Source directory not found: $Source"
  }
  Assert-StateChildPath -Path $Destination
  New-Item -ItemType Directory -Force -Path $Destination | Out-Null

  $args = @(
    $Source,
    $Destination,
    "/MIR",
    "/R:2",
    "/W:2",
    "/NFL",
    "/NDL",
    "/NP",
    "/NJH",
    "/NJS",
    "/XJ",
    "/SL"
  )
  $step = Invoke-Captured -FilePath "robocopy.exe" -ArgumentList $args -Name $Name
  if ($step.exit_code -le 7) {
    $step.exit_code = 0
  }
  return $step
}

function Reset-StateDirectory {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  Assert-StateChildPath -Path $Path
  if (Test-Path $Path -PathType Container) {
    try {
      Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    } catch {
      Write-Warning "Could not fully remove $Path; clearing children best-effort. $($_.Exception.Message)"
      Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
  New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Copy-FileIfPresent {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
  )

  if (-not (Test-Path $Source -PathType Leaf)) {
    return $false
  }

  Assert-StateChildPath -Path (Split-Path -Parent $Destination)
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
  Copy-Item -LiteralPath $Source -Destination $Destination -Force
  return $true
}

function Sync-ClientHomeSubset {
  param(
    [Parameter(Mandatory = $true)]
    [int]$SourceIndex,

    [Parameter(Mandatory = $true)]
    [int]$TargetIndex
  )

  $sourceHome = Get-ClientHome -ClientIndex $SourceIndex
  $targetHome = Get-ClientHome -ClientIndex $TargetIndex
  Reset-StateDirectory -Path $targetHome

  $steps = New-Object System.Collections.Generic.List[object]
  foreach ($relativeDir in @(
      ".steam",
      ".config\autostart",
      ".config\dconf",
      ".config\xfce4",
      "init.d",
      ".comfy",
      # Valheim character profiles (.fch). Kept as explicit clone steps so seeded
      # characters survive a re-clone even if the broad .steam mirror is trimmed:
      #   - native Linux build path
      ".config\unity3d\IronGate\Valheim\characters",
      #   - Proton prefix path (Steam app 892970)
      ".steam\steam\steamapps\compatdata\892970\pfx\drive_c\users\steamuser\AppData\LocalLow\IronGate\Valheim\characters"
    )) {
    $sourceDir = Join-Path $sourceHome $relativeDir
    if (-not (Test-Path $sourceDir -PathType Container)) {
      continue
    }
    $targetDir = Join-Path $targetHome $relativeDir
    $steps.Add((Sync-Directory -Source $sourceDir -Destination $targetDir -Name "clone-home-$relativeDir-$SourceIndex-to-$TargetIndex")) | Out-Null
  }

  foreach ($relativeFile in @(
      ".bashrc",
      ".config\mimeapps.list",
      ".config\user-dirs.dirs",
      ".config\user-dirs.locale"
    )) {
    Copy-FileIfPresent -Source (Join-Path $sourceHome $relativeFile) -Destination (Join-Path $targetHome $relativeFile) | Out-Null
  }

  return @($steps.ToArray())
}

function Write-PlayerDescriptor {
  param(
    [Parameter(Mandatory = $true)]
    [int]$ClientIndex,

    [Parameter(Mandatory = $true)]
    [int]$SourceIndex
  )

  $clientHome = Get-ClientHome -ClientIndex $ClientIndex
  $comfyDir = Join-Path $clientHome ".comfy"
  New-Item -ItemType Directory -Force -Path $comfyDir | Out-Null
  $descriptor = [ordered]@{
    client = Get-ClientName -ClientIndex $ClientIndex
    service = Get-ClientService -ClientIndex $ClientIndex
    cloned_from = Get-ClientName -ClientIndex $SourceIndex
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    mcp_label = "valheim-player-{0:00}" -f $ClientIndex
    install_root = Get-ClientInstallRoot -ClientIndex $ClientIndex
    networksense_log_root = Get-ClientLogRoot -ClientIndex $ClientIndex
  }
  Write-JsonFile -Value $descriptor -Path (Join-Path $comfyDir "player.json")
  return $descriptor
}

function Copy-ClientInitScript {
  param([int]$ClientIndex)

  $clientInit = Join-Path (Get-ClientHome -ClientIndex $ClientIndex) "init.d"
  New-Item -ItemType Directory -Force -Path $clientInit | Out-Null
  Copy-Item `
    -LiteralPath (Join-Path $autonomousRoot "client-init\20-comfy-valheim-autostart.sh") `
    -Destination (Join-Path $clientInit "20-comfy-valheim-autostart.sh") `
    -Force
}

function Get-ComposeArgs {
  param([string[]]$Tail)

  $args = @(
    "compose",
    "--profile",
    "clients",
    "--env-file",
    $script:EnvPath,
    "-f",
    $composePath,
    "-p",
    $DockerProject
  )
  $args += $Tail
  return $args
}

function Get-ComposeRecords {
  $output = @(& $script:DockerPath @(Get-ComposeArgs -Tail @("ps", "--format", "json")) 2>&1 | ForEach-Object { [string]$_ })
  if ($LASTEXITCODE -ne 0) {
    return @()
  }

  $records = @()
  foreach ($line in $output) {
    if ([string]::IsNullOrWhiteSpace($line)) {
      continue
    }
    try {
      $records += ($line | ConvertFrom-Json)
    } catch {
      try {
        $records = @(($output -join "`n") | ConvertFrom-Json)
        break
      } catch {
        return @()
      }
    }
  }
  return @($records)
}

function Test-ServicesRunning {
  param([string[]]$Services)

  $records = Get-ComposeRecords
  $running = @($records |
    Where-Object { $_.Service -and ($_.State -eq "running" -or $_.Status -match "^Up") } |
    ForEach-Object { [string]$_.Service })
  $missing = @($Services | Where-Object { $running -notcontains $_ })
  return [ordered]@{
    ok = ($missing.Count -eq 0)
    running = $running
    missing = $missing
  }
}

function Read-UrlStatus {
  param([string]$Url)

  try {
    $response = Invoke-WebRequest -UseBasicParsing $Url -TimeoutSec 5
    return [ordered]@{
      ok = $true
      status_code = [int]$response.StatusCode
      url = $Url
    }
  } catch {
    return [ordered]@{
      ok = $false
      status_code = $null
      url = $Url
      error = $_.Exception.Message
    }
  }
}

$DockerPath = Get-DockerCommand
if ([string]::IsNullOrWhiteSpace($EnvPath)) {
  $EnvPath = Find-LatestAutonomousEnv
}
if ([string]::IsNullOrWhiteSpace($EnvPath) -or -not (Test-Path $EnvPath -PathType Leaf)) {
  throw "No autonomous lab env file found. Run run-autonomous-valheim-lab.ps1 once, or pass -EnvPath."
}
$EnvPath = (Resolve-Path $EnvPath).Path

$steps = New-Object System.Collections.Generic.List[object]
$warnings = New-Object System.Collections.Generic.List[string]
$spawnEvents = New-Object System.Collections.Generic.List[object]
$playerDescriptors = New-Object System.Collections.Generic.List[object]

$sourceHome = Get-ClientHome -ClientIndex $SourceClient
$sourceGames = Get-ClientGames -ClientIndex $SourceClient
$sourceInstall = Get-ClientInstallRoot -ClientIndex $SourceClient
$sourceComplete = (Test-Path $sourceInstall -PathType Container)

if (-not (Test-Path $sourceHome -PathType Container)) {
  throw "Source client home does not exist: $sourceHome"
}
if (-not (Test-Path $sourceGames -PathType Container)) {
  throw "Source client games directory does not exist: $sourceGames"
}
if (-not $sourceComplete -and -not $AllowIncompleteSource) {
  throw "Source client is not a player image yet. Missing Valheim install: $sourceInstall. Use -AllowIncompleteSource only for container/noVNC shakedown."
}
if (-not $sourceComplete) {
  $warnings.Add("Source client is incomplete; this run can spawn Steam-Headless desktops but not real Valheim players.") | Out-Null
}

for ($index = 1; $index -le $Players; $index++) {
  Copy-ClientInitScript -ClientIndex $index
  $playerDescriptors.Add((Write-PlayerDescriptor -ClientIndex $index -SourceIndex $SourceClient)) | Out-Null
}

if ($ClonePlayers) {
  for ($index = 1; $index -le $Players; $index++) {
    if ($index -eq $SourceClient) {
      continue
    }

    $service = Get-ClientService -ClientIndex $index
    $rmStep = Invoke-Captured -FilePath $DockerPath -ArgumentList (Get-ComposeArgs -Tail @("rm", "-sf", $service)) -Name "remove-$service-before-clone"
    $steps.Add($rmStep) | Out-Null
    if ($rmStep.exit_code -ne 0) {
      $warnings.Add("Could not remove target container $service before clone; continuing with filesystem sync. Output: $($rmStep.output -join ' ')") | Out-Null
    }

    $homeSteps = @(Sync-ClientHomeSubset -SourceIndex $SourceClient -TargetIndex $index)
    foreach ($homeStep in $homeSteps) {
      $steps.Add($homeStep) | Out-Null
      if ($homeStep.exit_code -ne 0) {
        throw "Failed to clone selected client home state to client $index."
      }
    }

    Reset-StateDirectory -Path (Get-ClientGames -ClientIndex $index)
    $gamesStep = Sync-Directory -Source $sourceGames -Destination (Get-ClientGames -ClientIndex $index) -Name "clone-games-$($SourceClient)-to-$index"
    $steps.Add($gamesStep) | Out-Null
    if ($gamesStep.exit_code -ne 0) {
      throw "Failed to clone client games to client $index."
    }

    Copy-ClientInitScript -ClientIndex $index
    $playerDescriptors.Add((Write-PlayerDescriptor -ClientIndex $index -SourceIndex $SourceClient)) | Out-Null
  }
}

$requestedServices = @("valheim-server", "comfy-gateway")
for ($index = 1; $index -le $Players; $index++) {
  $requestedServices += (Get-ClientService -ClientIndex $index)
}

if ($Start) {
  $baseStep = Invoke-Captured -FilePath $DockerPath -ArgumentList (Get-ComposeArgs -Tail @("up", "-d", "valheim-server", "comfy-gateway")) -Name "ensure-server-gateway"
  $steps.Add($baseStep) | Out-Null
  if ($baseStep.exit_code -ne 0) {
    $state = Test-ServicesRunning -Services @("valheim-server", "comfy-gateway")
    if (-not $state.ok) {
      throw "Server/gateway are not running after compose up: $($baseStep.output -join ' ')"
    }
    $warnings.Add("compose up for server/gateway returned non-zero, but services are running.") | Out-Null
  }

  for ($index = 1; $index -le $Players; $index++) {
    $service = Get-ClientService -ClientIndex $index
    $startedAt = Get-Date
    $upStep = Invoke-Captured -FilePath $DockerPath -ArgumentList (Get-ComposeArgs -Tail @("up", "-d", $service)) -Name "spawn-$service"
    $steps.Add($upStep) | Out-Null
    $state = Test-ServicesRunning -Services @($service)
    $event = [ordered]@{
      client = Get-ClientName -ClientIndex $index
      service = $service
      started_at_utc = $startedAt.ToUniversalTime().ToString("o")
      compose_exit_code = $upStep.exit_code
      running = [bool]$state.ok
      no_vnc_url = "http://127.0.0.1:$((8080 + $index))"
      install_root = Get-ClientInstallRoot -ClientIndex $index
      install_exists = Test-Path (Get-ClientInstallRoot -ClientIndex $index) -PathType Container
      networksense_log_root = Get-ClientLogRoot -ClientIndex $index
      networksense_log_exists = Test-Path (Get-ClientLogRoot -ClientIndex $index) -PathType Container
    }
    $spawnEvents.Add($event) | Out-Null
    Write-JsonFile -Value $event -Path (Join-Path $rawDir "spawn-$service.json")

    if (-not $state.ok) {
      throw "Spawned $service but it is not running."
    }

    if ($index -lt $Players -and $SpawnDelaySeconds -gt 0) {
      Start-Sleep -Seconds $SpawnDelaySeconds
    }
  }

  if ($ObserveSeconds -gt 0) {
    Start-Sleep -Seconds $ObserveSeconds
  }
}

$composeRecords = Get-ComposeRecords
Write-JsonFile -Value $composeRecords -Path (Join-Path $rawDir "docker-compose-ps.json")

$statsStep = Invoke-Captured -FilePath $DockerPath -ArgumentList @("stats", "--no-stream", "--format", "json") -Name "docker-stats"
$steps.Add($statsStep) | Out-Null
if ($statsStep.exit_code -eq 0) {
  $stats = @()
  foreach ($line in $statsStep.output) {
    if ([string]::IsNullOrWhiteSpace($line)) {
      continue
    }
    try {
      $stats += ($line | ConvertFrom-Json)
    } catch {
    }
  }
  Write-JsonFile -Value $stats -Path (Join-Path $rawDir "docker-stats.json")
}

$serviceState = Test-ServicesRunning -Services $requestedServices
$gatewayHealth = Read-UrlStatus -Url "http://127.0.0.1:8720/healthz"
$novncHealth = @()
for ($index = 1; $index -le $Players; $index++) {
  $novncHealth += (Read-UrlStatus -Url "http://127.0.0.1:$((8080 + $index))/")
}

$clientStates = @()
for ($index = 1; $index -le $Players; $index++) {
  $clientStates += [ordered]@{
    client = Get-ClientName -ClientIndex $index
    service = Get-ClientService -ClientIndex $index
    install_root = Get-ClientInstallRoot -ClientIndex $index
    install_exists = Test-Path (Get-ClientInstallRoot -ClientIndex $index) -PathType Container
    networksense_log_root = Get-ClientLogRoot -ClientIndex $index
    networksense_log_exists = Test-Path (Get-ClientLogRoot -ClientIndex $index) -PathType Container
    player_descriptor = Join-Path (Get-ClientHome -ClientIndex $index) ".comfy\player.json"
  }
}

if ($StopAfter -and $Start) {
  $clientServices = @()
  for ($index = 1; $index -le $Players; $index++) {
    $clientServices += (Get-ClientService -ClientIndex $index)
  }
  $stopStep = Invoke-Captured -FilePath $DockerPath -ArgumentList (Get-ComposeArgs -Tail (@("stop") + $clientServices)) -Name "stop-swarm-clients"
  $steps.Add($stopStep) | Out-Null
}

$status = if (-not $Start) {
  "staged_not_started"
} elseif (-not $sourceComplete) {
  "started_waiting_for_player_image"
} elseif (-not $serviceState.ok) {
  "started_missing_services"
} elseif (($clientStates | Where-Object { -not $_.networksense_log_exists }).Count -gt 0) {
  "started_waiting_for_networksense"
} else {
  "swarm_started"
}

$summary = [ordered]@{
  schema_version = 1
  status = $status
  run_id = $runId
  generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
  players = $Players
  source_client = Get-ClientName -ClientIndex $SourceClient
  source_complete = $sourceComplete
  clone_players = [bool]$ClonePlayers
  allow_incomplete_source = [bool]$AllowIncompleteSource
  spawn_delay_seconds = $SpawnDelaySeconds
  observe_seconds = $ObserveSeconds
  docker_project = $DockerProject
  compose_file = $composePath
  env_file = $EnvPath
  service_state = $serviceState
  gateway_health = $gatewayHealth
  novnc_health = $novncHealth
  spawn_events = @($spawnEvents.ToArray())
  client_states = $clientStates
  player_descriptors = @($playerDescriptors.ToArray())
  warnings = @($warnings.ToArray())
  steps = @($steps.ToArray())
}

Write-JsonFile -Value $summary -Path (Join-Path $telemetryDir "yolo-swarm-summary.json")

$runbookLines = New-Object System.Collections.Generic.List[string]
$runbookLines.Add("# Valheim YOLO Swarm") | Out-Null
$runbookLines.Add("") | Out-Null
$runbookLines.Add("Status: $status") | Out-Null
$runbookLines.Add("") | Out-Null
$runbookLines.Add("Gateway health: http://127.0.0.1:8720/healthz") | Out-Null
$runbookLines.Add("") | Out-Null
$runbookLines.Add("Client noVNC URLs:") | Out-Null
for ($index = 1; $index -le $Players; $index++) {
  $runbookLines.Add(("- client{0:00}: http://127.0.0.1:{1}" -f $index, (8080 + $index))) | Out-Null
}
$runbookLines.Add("") | Out-Null
$runbookLines.Add("Source player image:") | Out-Null
$runbookLines.Add("") | Out-Null
$runbookLines.Add('```text') | Out-Null
$runbookLines.Add($sourceInstall) | Out-Null
$runbookLines.Add('```') | Out-Null
$runbookLines.Add("") | Out-Null
$runbookLines.Add("Run summary:") | Out-Null
$runbookLines.Add("") | Out-Null
$runbookLines.Add('```text') | Out-Null
$runbookLines.Add((Join-Path $telemetryDir "yolo-swarm-summary.json")) | Out-Null
$runbookLines.Add('```') | Out-Null
$runbookLines | Set-Content -Encoding UTF8 (Join-Path $rawDir "operator-runbook.md")

Write-Output "Created YOLO swarm packet: $runDir"
Write-Output "Status: $status"
Write-Output "Next: inspect telemetry\yolo-swarm-summary.json and raw\operator-runbook.md"
