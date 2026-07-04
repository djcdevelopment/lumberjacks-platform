param(
  [string]$InstallDir = "C:\work\dotnet9",

  [string]$Channel = "9.0"
)

$ErrorActionPreference = "Stop"

$dotnetExe = Join-Path $InstallDir "dotnet.exe"
if (Test-Path $dotnetExe) {
  Write-Host "Local .NET already exists: $dotnetExe"
  & $dotnetExe --version
  exit 0
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null

$scriptPath = Join-Path (Split-Path -Parent $InstallDir) "dotnet-install.ps1"
Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $scriptPath
& $scriptPath -Channel $Channel -InstallDir $InstallDir

& $dotnetExe --version
