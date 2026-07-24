[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scenario = Join-Path $repoRoot 'fieldlab\experiments\m7\m7-e00-lab-truth\malformed-scenario.yaml'
$output = Join-Path $repoRoot 'fieldlab\experiments\m7\m7-e00-lab-truth\runs\malformed-input'
$relativeScenario = $scenario.Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
New-Item -ItemType Directory -Force -Path $output | Out-Null

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& docker run --rm -v "${repoRoot}:/repo" -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet run --no-build --project src/AuthorityLab -- run --scenario "/repo/$relativeScenario" --output /repo/fieldlab/experiments/m7/m7-e00-lab-truth/runs/malformed-input 2>$null
$exitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($exitCode -ne 2) { throw "malformed scenario expected exit 2, observed $exitCode" }

$rejection = [ordered]@{
    schema_version = 1
    experiment_id = 'm7-e00-lab-truth'
    scenario_id = 'malformed-unknown-driver'
    run_id = 'malformed-input'
    result_classification = 'harness_failed'
    stop_result = 'rejected_before_execution'
    observed_exit_code = $exitCode
    reason = 'unknown driver rejected before an authority decision was executed'
}
[IO.File]::WriteAllText((Join-Path $output 'rejection.json'), (($rejection | ConvertTo-Json -Depth 6) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host "malformed scenario rejected before execution; receipt=$output\rejection.json"
