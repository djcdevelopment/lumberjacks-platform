[CmdletBinding()]
param(
  [string] $SshTarget = "comfy-p7",
  [int] $LocalPort = 14000,
  [string] $RemoteHost = "127.0.0.1",
  [int] $RemotePort = 4000,
  [int] $ReadyTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$healthUrl = "http://127.0.0.1:$LocalPort/health"

try {
  $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
  if ($health.status -eq "ok" -and $health.service -eq "gateway") {
    [pscustomobject]@{ AlreadyRunning = $true; ProcessId = $null; Health = $healthUrl }
    return
  }
} catch {
  # Expected when the tunnel is not running yet.
}

$listener = Get-NetTCPConnection -LocalPort $LocalPort -State Listen -ErrorAction SilentlyContinue
if ($listener) {
  throw "Local TCP port $LocalPort is already in use and does not serve Lumberjacks health"
}

$arguments = @(
  "-N",
  "-o", "ExitOnForwardFailure=yes",
  "-o", "ServerAliveInterval=15",
  "-o", "ServerAliveCountMax=3",
  "-L", "${LocalPort}:${RemoteHost}:${RemotePort}",
  $SshTarget
)
$process = Start-Process ssh.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
do {
  Start-Sleep -Milliseconds 500
  if ($process.HasExited) {
    throw "SSH gateway tunnel exited before becoming ready (exit $($process.ExitCode))"
  }
  try {
    $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
    if ($health.status -eq "ok" -and $health.service -eq "gateway") {
      [pscustomobject]@{ AlreadyRunning = $false; ProcessId = $process.Id; Health = $healthUrl }
      return
    }
  } catch {
    # Keep polling until the bounded deadline.
  }
} while ((Get-Date) -lt $deadline)

Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
throw "Timed out waiting for the SSH gateway tunnel at $healthUrl"
