param(
  [string]$TargetPath = "C:\work\Lumberjacks",

  [string]$RepositoryUrl = "https://github.com/djcdevelopment/Lumberjacks.git"
)

$ErrorActionPreference = "Stop"

if (Test-Path $TargetPath) {
  Write-Host "Lumberjacks checkout already exists: $TargetPath"
  git -C $TargetPath status --short
  exit 0
}

$parent = Split-Path -Parent $TargetPath
New-Item -ItemType Directory -Force $parent | Out-Null

git clone $RepositoryUrl $TargetPath
git -C $TargetPath status --short
