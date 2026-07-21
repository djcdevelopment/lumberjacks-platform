[CmdletBinding()]
param(
  [ValidateSet('status','start','stop','watch')]
  [string] $Action = 'status',
  [string] $SshTarget = 'comfy-p7',
  [int] $LocalPort = 14000,
  [int] $RemotePort = 4000,
  [int] $PollSeconds = 5
)

$ErrorActionPreference = 'Stop'
$healthUrl = "http://127.0.0.1:$LocalPort/health"
$forwardPattern = "-L\s+${LocalPort}:127\.0\.0\.1:${RemotePort}"

function Get-TunnelProcess {
  @(Get-CimInstance Win32_Process -Filter "Name = 'ssh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match $forwardPattern })
}

function Get-TunnelStatus {
  $processes = @(Get-TunnelProcess)
  $health = $null
  try { $health = Invoke-RestMethod $healthUrl -TimeoutSec 2 } catch { }
  [pscustomobject]@{
    action = 'status'
    local_url = $healthUrl
    healthy = [bool]($health.status -eq 'ok' -and $health.service -eq 'gateway')
    listener = [bool](Get-NetTCPConnection -LocalPort $LocalPort -State Listen -ErrorAction SilentlyContinue)
    process_ids = @($processes | ForEach-Object { [int]$_.ProcessId })
    ssh_target = $SshTarget
  }
}

switch ($Action) {
  'status' { Get-TunnelStatus; break }
  'start' { & "$PSScriptRoot\start-gateway-tunnel.ps1" -SshTarget $SshTarget -LocalPort $LocalPort -RemotePort $RemotePort; break }
  'stop' {
    $processes = @(Get-TunnelProcess)
    foreach ($process in $processes) {
      Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue
    }
    [pscustomobject]@{ action = 'stop'; stopped_process_ids = @($processes | ForEach-Object { [int]$_.ProcessId }); status = (Get-TunnelStatus) }
    break
  }
  'watch' {
    while ($true) {
      $status = Get-TunnelStatus
      if (-not $status.healthy) {
        try {
          & "$PSScriptRoot\start-gateway-tunnel.ps1" -SshTarget $SshTarget -LocalPort $LocalPort -RemotePort $RemotePort | Out-Null
          Write-Host "Gateway tunnel restored at $healthUrl" -ForegroundColor Green
        } catch {
          Write-Warning "Gateway tunnel not ready: $($_.Exception.Message)"
        }
      }
      Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
    }
  }
}
