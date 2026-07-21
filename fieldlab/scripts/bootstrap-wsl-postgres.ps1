param(
  [int]$Port = 5433,
  [string]$Database = "game",
  [string]$User = "game",
  [string]$Password = "game",
  [string]$InitSqlPath = "C:\work\Lumberjacks\infra\docker\init.sql",
  [string]$WslUser = "root",
  [string]$ListenAddresses = "*"
)

$ErrorActionPreference = "Stop"

function Convert-ToWslPath {
  param([Parameter(Mandatory = $true)][string]$Path)

  $resolved = (Resolve-Path $Path).Path -replace "\\", "/"
  if ($resolved -match "^([A-Za-z]):/(.*)$") {
    $drive = $Matches[1].ToLowerInvariant()
    return "/mnt/$drive/$($Matches[2])"
  }

  return $resolved
}

function Test-TcpPort {
  param(
    [Parameter(Mandatory = $true)]
    [string]$HostName,

    [Parameter(Mandatory = $true)]
    [int]$Port,

    [int]$TimeoutMs = 1000
  )

  $client = [System.Net.Sockets.TcpClient]::new()
  $asyncResult = $null
  try {
    $asyncResult = $client.BeginConnect($HostName, $Port, $null, $null)
    if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
      return $false
    }
    $client.EndConnect($asyncResult)
    return $client.Connected
  } catch {
    return $false
  } finally {
    if ($asyncResult) {
      $asyncResult.AsyncWaitHandle.Close()
    }
    $client.Close()
    $client.Dispose()
  }
}

function Get-WslIpv4Candidates {
  param([Parameter(Mandatory = $true)][string]$User)

  $previousErrorActionPreference = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    $output = & wsl -u $User -e bash -lc "hostname -I" 2>$null
  } finally {
    $ErrorActionPreference = $previousErrorActionPreference
  }

  if (-not $output) {
    return @()
  }

  return @($output -split "\s+" | Where-Object {
      $_ -match "^\d{1,3}(\.\d{1,3}){3}$" -and $_ -ne "127.0.0.1"
    } | Select-Object -Unique)
}

$bashScript = Convert-ToWslPath (Join-Path $PSScriptRoot "bootstrap-wsl-postgres.sh")
$initSql = Convert-ToWslPath $InitSqlPath

wsl -u $WslUser -e bash $bashScript `
  --port $Port `
  --database $Database `
  --user $User `
  --password $Password `
  --init-sql $initSql `
  --listen-addresses $ListenAddresses

if ($LASTEXITCODE -ne 0) {
  throw "WSL Postgres bootstrap failed with exit code $LASTEXITCODE."
}

$preferredLoopback = if (Test-TcpPort -HostName "127.0.0.1" -Port $Port -TimeoutMs 2000) {
  "127.0.0.1"
} elseif (Test-TcpPort -HostName "localhost" -Port $Port -TimeoutMs 2000) {
  "localhost"
} else {
  $null
}

if ($preferredLoopback) {
  Write-Host "Postgres is reachable from Windows at ${preferredLoopback}:$Port"
  Write-Host "Connection string: Host=$preferredLoopback;Port=$Port;Database=$Database;Username=$User;Password=$Password"
} else {
  $reachableWslHost = $null
  foreach ($candidate in Get-WslIpv4Candidates -User $WslUser) {
    if (Test-TcpPort -HostName $candidate -Port $Port -TimeoutMs 2000) {
      $reachableWslHost = $candidate
      break
    }
  }

  if ($reachableWslHost) {
    Write-Host "Postgres is reachable from Windows at ${reachableWslHost}:$Port"
    Write-Host "Connection string: Host=$reachableWslHost;Port=$Port;Database=$Database;Username=$User;Password=$Password"
    Write-Host "For fieldlab runs without auto-detection, set FIELDLAB_PGHOST=$reachableWslHost"
  } else {
    Write-Warning "Postgres did not answer from Windows at 127.0.0.1:$Port, localhost:$Port, or any detected WSL IPv4 address. Check WSL networking and firewall settings."
  }
}
