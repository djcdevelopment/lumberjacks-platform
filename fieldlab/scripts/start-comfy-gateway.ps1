<#
.SYNOPSIS
Start the isolate-owned Dev MCP image for the platform FieldLab.

.DESCRIPTION
Runs the image declared by fieldlab/autonomous/valheim-lab.compose.yml. The
platform never imports or executes MCP source from a sibling checkout.
#>
[CmdletBinding()]
param(
    [string]$EnvFile = '',
    [ValidateRange(1024, 65535)][int]$Port = 8721
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

$compose = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.compose.yml'
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $localEnv = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.env'
    $EnvFile = if (Test-Path -LiteralPath $localEnv) {
        $localEnv
    } else {
        Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.env.example'
    }
}
$env:COMFY_GATEWAY_PORT = [string]$Port
Write-Host "Starting isolate MCP artifact on http://127.0.0.1:$Port/mcp"
& docker compose --env-file $EnvFile -f $compose up comfy-gateway
exit $LASTEXITCODE
