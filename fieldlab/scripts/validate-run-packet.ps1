param(
  [Parameter(Mandatory = $true)]
  [string]$RunDir,

  [string]$OutPath,

  [switch]$AllowMissingSignature
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $RunDir)) {
  throw "Run directory not found: $RunDir"
}

$requiredFiles = @(
  "experiment.yaml",
  "environment.json",
  "commands.ps1",
  "results.json",
  "report.md",
  "publish.md",
  "quickstart.md"
)

if (-not $AllowMissingSignature) {
  $requiredFiles += "signature.json"
}

$requiredDirectories = @(
  "raw",
  "telemetry",
  "captures"
)

$checks = New-Object System.Collections.Generic.List[object]

foreach ($file in $requiredFiles) {
  $path = Join-Path $RunDir $file
  $checks.Add([ordered]@{
    kind = "file"
    path = $file
    exists = Test-Path $path -PathType Leaf
  })
}

foreach ($directory in $requiredDirectories) {
  $path = Join-Path $RunDir $directory
  $checks.Add([ordered]@{
    kind = "directory"
    path = $directory
    exists = Test-Path $path -PathType Container
  })
}

$missing = @($checks | Where-Object { -not $_.exists })
$status = if ($missing.Count -eq 0) { "pass" } else { "fail" }

$validation = [ordered]@{
  schema_version = 1
  run_dir = $RunDir
  validated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
  status = $status
  allow_missing_signature = [bool]$AllowMissingSignature
  checks = $checks
  missing = @($missing | ForEach-Object { $_.path })
}

if (-not $OutPath) {
  $OutPath = Join-Path $RunDir "validation.json"
}

$validation | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $OutPath

if ($status -ne "pass") {
  exit 1
}

exit 0
