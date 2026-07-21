$paths = @(
  'clients\\game-client',
  'clients\\admin-web',
  'services\\gateway',
  'services\\simulation',
  'services\\event-log',
  'services\\progression',
  'services\\content-registry',
  'services\\discord-bridge',
  'services\\operator-api',
  'packages\\schemas',
  'packages\\protocol',
  'packages\\sdk-plugin',
  'packages\\sdk-content',
  'packages\\observability',
  'packages\\dev-tools',
  'plugins\\examples',
  'plugins\\core-community-pack',
  'docs'
)

$missing = @()
foreach ($path in $paths) {
  if (-not (Test-Path $path)) {
    $missing += $path
  }
}

if ($missing.Count -gt 0) {
  Write-Error ("Missing scaffold paths: " + ($missing -join ', '))
  exit 1
}

Write-Host "Workspace scaffold looks complete." -ForegroundColor Green
