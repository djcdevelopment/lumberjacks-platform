param(
  [string]$Profile = "",
  [int]$MemoryGb = 0,
  [int]$Processors = 0,
  [switch]$Apply,
  [switch]$ShutdownWsl,
  [switch]$ClearLimits,
  [switch]$Json
)

$ErrorActionPreference = "Stop"

function Get-ProfileSpec {
  param([string]$Name)

  $profiles = @{
    host_full = @{
      memory_gb = $null
      processors = $null
      purpose = "Best-case host baseline; no FieldLab WSL cap."
    }
    server_8c_32gb = @{
      memory_gb = 32
      processors = 8
      purpose = "Healthy dedicated-server envelope."
    }
    server_4c_16gb = @{
      memory_gb = 16
      processors = 4
      purpose = "Practical constrained-host envelope."
    }
    server_2c_8gb = @{
      memory_gb = 8
      processors = 2
      purpose = "Stress envelope for degradation behavior."
    }
    client_low_priority = @{
      memory_gb = $null
      processors = $null
      purpose = "Client-side process profile; no FieldLab WSL cap."
    }
  }

  if ($profiles.ContainsKey($Name)) {
    return [ordered]@{
      known = $true
      id = $Name
      memory_gb = $profiles[$Name].memory_gb
      processors = $profiles[$Name].processors
      purpose = $profiles[$Name].purpose
    }
  }

  return [ordered]@{
    known = $false
    id = $Name
    memory_gb = $null
    processors = $null
    purpose = "Unknown profile; explicit MemoryGb/Processors overrides are required to apply limits."
  }
}

function Get-Wsl2Value {
  param(
    [string[]]$Lines,
    [string]$Key
  )

  $inWsl2 = $false
  $keyPattern = "^\s*{0}\s*=\s*(.+?)\s*$" -f [regex]::Escape($Key)

  foreach ($line in $Lines) {
    if ($line -match "^\s*\[(.+?)\]\s*$") {
      $inWsl2 = ($matches[1] -ieq "wsl2")
      continue
    }

    if ($inWsl2 -and $line -match $keyPattern) {
      return $matches[1].Trim()
    }
  }

  return $null
}

function Set-Wsl2Value {
  param(
    [string[]]$Lines,
    [string]$Key,
    [AllowNull()][string]$Value
  )

  $output = New-Object System.Collections.Generic.List[string]
  $inWsl2 = $false
  $foundWsl2 = $false
  $handledKey = $false
  $keyPattern = "^\s*{0}\s*=" -f [regex]::Escape($Key)

  foreach ($line in $Lines) {
    if ($line -match "^\s*\[(.+?)\]\s*$") {
      if ($inWsl2 -and -not $handledKey -and $null -ne $Value) {
        $output.Add("$Key=$Value")
        $handledKey = $true
      }

      $inWsl2 = ($matches[1] -ieq "wsl2")
      if ($inWsl2) {
        $foundWsl2 = $true
      }

      $output.Add($line)
      continue
    }

    if ($inWsl2 -and $line -match $keyPattern) {
      if ($null -ne $Value) {
        $output.Add("$Key=$Value")
      }
      $handledKey = $true
      continue
    }

    $output.Add($line)
  }

  if ($foundWsl2) {
    if ($inWsl2 -and -not $handledKey -and $null -ne $Value) {
      $output.Add("$Key=$Value")
    }
  } elseif ($null -ne $Value) {
    if ($output.Count -gt 0) {
      $output.Add("")
    }
    $output.Add("[wsl2]")
    $output.Add("$Key=$Value")
  }

  return @($output.ToArray())
}

function Test-Wsl2TargetMatch {
  param(
    [string[]]$Lines,
    [AllowNull()][string]$TargetMemory,
    [AllowNull()][string]$TargetProcessors,
    [bool]$Clear
  )

  if ([string]::IsNullOrWhiteSpace($TargetMemory)) {
    $TargetMemory = $null
  }

  if ([string]::IsNullOrWhiteSpace($TargetProcessors)) {
    $TargetProcessors = $null
  }

  $currentMemory = Get-Wsl2Value -Lines $Lines -Key "memory"
  $currentProcessors = Get-Wsl2Value -Lines $Lines -Key "processors"

  if ($Clear) {
    return ($null -eq $currentMemory -and $null -eq $currentProcessors)
  }

  if ($null -eq $TargetMemory -and $null -eq $TargetProcessors) {
    return $true
  }

  $memoryMatches = ($null -eq $TargetMemory) -or ($currentMemory -ieq $TargetMemory)
  $processorMatches = ($null -eq $TargetProcessors) -or ($currentProcessors -ieq $TargetProcessors)
  return ($memoryMatches -and $processorMatches)
}

if ([string]::IsNullOrWhiteSpace($Profile)) {
  $Profile = if ($env:FIELDLAB_RESOURCE_PROFILE) { $env:FIELDLAB_RESOURCE_PROFILE } else { "host_full" }
}

if ($MemoryGb -le 0 -and $env:FIELDLAB_WSL_MEMORY_GB) {
  [int]$parsedMemory = 0
  if ([int]::TryParse($env:FIELDLAB_WSL_MEMORY_GB, [ref]$parsedMemory)) {
    $MemoryGb = $parsedMemory
  }
}

if ($Processors -le 0 -and $env:FIELDLAB_WSL_PROCESSORS) {
  [int]$parsedProcessors = 0
  if ([int]::TryParse($env:FIELDLAB_WSL_PROCESSORS, [ref]$parsedProcessors)) {
    $Processors = $parsedProcessors
  }
}

$spec = Get-ProfileSpec -Name $Profile
$targetMemoryGb = if ($MemoryGb -gt 0) { $MemoryGb } else { $spec.memory_gb }
$targetProcessors = if ($Processors -gt 0) { $Processors } else { $spec.processors }
$targetMemory = if ($targetMemoryGb) { "$($targetMemoryGb)GB" } else { $null }
$targetProcessorValue = if ($targetProcessors) { [string]$targetProcessors } else { $null }

$userProfile = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
$configPath = Join-Path $userProfile ".wslconfig"
$configExists = Test-Path -LiteralPath $configPath
$originalLines = if ($configExists) { @(Get-Content -LiteralPath $configPath) } else { @() }

$desiredLines = @($originalLines)
if ($ClearLimits) {
  $desiredLines = @(Set-Wsl2Value -Lines $desiredLines -Key "memory" -Value $null)
  $desiredLines = @(Set-Wsl2Value -Lines $desiredLines -Key "processors" -Value $null)
} elseif ($targetMemory -or $targetProcessorValue) {
  if ($targetMemory) {
    $desiredLines = @(Set-Wsl2Value -Lines $desiredLines -Key "memory" -Value $targetMemory)
  }
  if ($targetProcessorValue) {
    $desiredLines = @(Set-Wsl2Value -Lines $desiredLines -Key "processors" -Value $targetProcessorValue)
  }
}

$originalText = ($originalLines -join "`n")
$desiredText = ($desiredLines -join "`n")
$wouldChange = ($originalText -ne $desiredText)
$backupPath = $null
$applied = $false
$shutdownExitCode = $null
$shutdownOutput = $null

if ($Apply -and $wouldChange) {
  if ($configExists) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupPath = "$configPath.fieldlab-backup-$stamp"
    Copy-Item -LiteralPath $configPath -Destination $backupPath -Force
  }

  $parent = Split-Path -Parent $configPath
  if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Force $parent | Out-Null
  }

  $desiredLines | Set-Content -LiteralPath $configPath -Encoding UTF8
  $applied = $true
}

if ($Apply -and $wouldChange -and $ShutdownWsl) {
  $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
  if ($wsl) {
    $shutdownOutput = (& $wsl.Source --shutdown 2>&1 | Out-String).Trim()
    $shutdownExitCode = $LASTEXITCODE
  } else {
    $shutdownOutput = "wsl.exe not found"
    $shutdownExitCode = -1
  }
}

$targetKind = if ($ClearLimits) {
  "clear"
} elseif ($targetMemory -or $targetProcessorValue) {
  "limit"
} else {
  "none"
}
$effectiveLines = if ($applied) { $desiredLines } else { $originalLines }
$matchesRequested = if ($targetKind -eq "none") {
  $true
} else {
  Test-Wsl2TargetMatch -Lines $effectiveLines -TargetMemory $targetMemory -TargetProcessors $targetProcessorValue -Clear ([bool]$ClearLimits)
}

$status = if (-not $spec.known -and $targetKind -eq "none") {
  "unknown_profile"
} elseif ($Apply -and $wouldChange -and -not $applied) {
  "apply_failed"
} elseif ($Apply -and $wouldChange -and $ShutdownWsl -and $shutdownExitCode -ne 0) {
  "shutdown_failed"
} elseif ($Apply -and $wouldChange -and -not $ShutdownWsl) {
  "applied_restart_pending"
} elseif ($matchesRequested) {
  "pass"
} else {
  "dry_run_change_pending"
}

$result = [ordered]@{
  schema_version = 1
  status = $status
  profile = $Profile
  known_profile = $spec.known
  purpose = $spec.purpose
  config_path = $configPath
  config_exists = $configExists
  target_kind = $targetKind
  target_memory_gb = $targetMemoryGb
  target_processors = $targetProcessors
  target_memory_value = $targetMemory
  target_processors_value = $targetProcessorValue
  current_memory_value = Get-Wsl2Value -Lines $originalLines -Key "memory"
  current_processors_value = Get-Wsl2Value -Lines $originalLines -Key "processors"
  matches_requested = $matchesRequested
  apply_requested = [bool]$Apply
  applied = $applied
  clear_requested = [bool]$ClearLimits
  would_change = $wouldChange
  backup_path = $backupPath
  requires_wsl_shutdown = $wouldChange
  shutdown_requested = [bool]$ShutdownWsl
  shutdown_exit_code = $shutdownExitCode
  shutdown_output = $shutdownOutput
  notes = @(
    "Default mode is dry-run; pass -Apply to write .wslconfig.",
    "Use -ShutdownWsl only when it is acceptable to stop all running WSL distros.",
    "host_full does not remove existing .wslconfig limits unless -ClearLimits is supplied."
  )
}

if ($Json) {
  $result | ConvertTo-Json -Depth 10
} else {
  [pscustomobject]$result | Format-List
}
