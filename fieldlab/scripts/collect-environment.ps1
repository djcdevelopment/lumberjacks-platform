param(
  [Parameter(Mandatory = $true)]
  [string]$OutPath
)

$ErrorActionPreference = "Stop"

$outDir = Split-Path -Parent $OutPath
if ($outDir) {
  New-Item -ItemType Directory -Force $outDir | Out-Null
}

function Get-CommandVersion {
  param([string]$Command, [string[]]$Args = @("--version"))

  $cmd = Get-Command $Command -ErrorAction SilentlyContinue
  if (-not $cmd) {
    return $null
  }

  try {
    return (& $Command @Args 2>&1 | Select-Object -First 5) -join "`n"
  } catch {
    return "found_but_version_failed: $($_.Exception.Message)"
  }
}

$gpu = Get-CimInstance Win32_VideoController | Select-Object Name, AdapterRAM, DriverVersion
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed
$disk = Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID, DriveType, Size, FreeSpace

$gitStatus = $null
try {
  $gitStatus = git status --short
} catch {
  $gitStatus = "git status failed: $($_.Exception.Message)"
}

$gitCommit = $null
try {
  $gitCommit = git rev-parse HEAD
} catch {
  $gitCommit = "git rev-parse failed: $($_.Exception.Message)"
}

$envInfo = [ordered]@{
  schema_version = 1
  captured_at_utc = (Get-Date).ToUniversalTime().ToString("o")
  machine_name = $env:COMPUTERNAME
  user = $env:USERNAME
  os = @{
    caption = $os.Caption
    version = $os.Version
    build_number = $os.BuildNumber
    total_visible_memory_kb = $os.TotalVisibleMemorySize
    free_physical_memory_kb = $os.FreePhysicalMemory
  }
  cpu = $cpu
  gpu = $gpu
  disk = $disk
  git = @{
    commit = $gitCommit
    status_short = $gitStatus
  }
  tools = @{
    dotnet = Get-CommandVersion "dotnet"
    dotnet9_local = if (Test-Path "C:\work\dotnet9\dotnet.exe") { Get-CommandVersion "C:\work\dotnet9\dotnet.exe" } else { $null }
    node = Get-CommandVersion "node"
    npm = Get-CommandVersion "npm"
    python = Get-CommandVersion "python"
    java = Get-CommandVersion "java"
    docker = Get-CommandVersion "docker"
    git = Get-CommandVersion "git"
  }
}

$envInfo | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $OutPath
