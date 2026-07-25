[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateSet('m7-e00-lab-truth','m7-e01-relevance-shape','m7-e02-recipient-fanout','m7-e03-motion-fingerprints','cre-e01-runtime-envelope','cre-e02-gateway-pressure-route','cre-e03-transport-faults')] [string]$Experiment,
    [ValidateSet('pure','gateway','gateway_durable','gateway_udp')] [string]$Driver = 'pure',
    [switch]$RunTwice,
    [switch]$ForceTimeout
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$experimentFamily = if ($Experiment.StartsWith('cre-', [StringComparison]::OrdinalIgnoreCase)) { 'creative-runtime' } else { 'm7' }
$scenario = Join-Path $repoRoot "fieldlab\experiments\$experimentFamily\$Experiment\scenario.yaml"
$runsRoot = Join-Path $repoRoot "fieldlab\experiments\$experimentFamily\$Experiment\runs"
$revision = (& git -C $repoRoot rev-parse --short HEAD).Trim()
$dirtyState = -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --porcelain))
if (-not (Test-Path -LiteralPath $scenario -PathType Leaf)) { throw "missing scenario: $scenario" }

& docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet build AuthorityLab.sln
if ($LASTEXITCODE -ne 0) { throw 'AuthorityLab build failed' }

function Invoke-Lab {
    param([string]$RunDirectory)
    $relativeScenario = $scenario.Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
    $relativeOutput = $RunDirectory.Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
    $arguments = @('run','--rm','-v',"${repoRoot}:/repo",'-w','/repo/tools/authority-lab','mcr.microsoft.com/dotnet/sdk:9.0','dotnet','run','--no-build','--project','src/AuthorityLab','--','run','--scenario',"/repo/$relativeScenario",'--output',"/repo/$relativeOutput",'--source-revision',$revision,'--driver',$Driver)
    if ($dirtyState) { $arguments += '--dirty-state' }
    if ($ForceTimeout) { $arguments += '--force-timeout' }
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw "AuthorityLab run failed for $RunDirectory" }
    & docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet run --no-build --project src/AuthorityLab -- check --run "/repo/$relativeOutput"
    if ($LASTEXITCODE -ne 0) { throw "AuthorityLab check failed for $RunDirectory" }
}

New-Item -ItemType Directory -Force -Path $runsRoot | Out-Null
$runId = "$Driver-$((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))"
$first = Join-Path $runsRoot $runId
Invoke-Lab -RunDirectory $first

if ($RunTwice) {
    $second = Join-Path $runsRoot ($runId + '-repeat')
    Invoke-Lab -RunDirectory $second
    $firstRelative = $first.Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
    $secondRelative = $second.Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
    & docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet run --no-build --project src/AuthorityLab -- compare --left "/repo/$firstRelative" --right "/repo/$secondRelative" --output "/repo/$($secondRelative)/comparison"
    if ($LASTEXITCODE -ne 0) { throw 'normalized comparison failed' }
}
Write-Host "AuthorityLab complete: $Experiment"
