[CmdletBinding()]
param(
    [ValidateSet('Verify', 'GatewayImage', 'AllImages')]
    [string] $Target = 'Verify',
    [string] $ReleaseId,
    [string] $ImageTag
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Invoke-DockerBuild([string] $DockerTarget, [string] $Tag, [string[]] $Arguments = @()) {
        $args = @('build', '--target', $DockerTarget, '-t', $Tag) + $Arguments + @('.')
        & docker @args
        if ($LASTEXITCODE -ne 0) { throw "Docker target '$DockerTarget' failed with exit code $LASTEXITCODE" }
    }

    switch ($Target) {
        'Verify' {
            Invoke-DockerBuild 'verify' 'lumberjacks-build:verify'
        }
        'GatewayImage' {
            if ([string]::IsNullOrWhiteSpace($ReleaseId)) { throw '-ReleaseId is required for GatewayImage' }
            if ([string]::IsNullOrWhiteSpace($ImageTag)) { $ImageTag = "lumberjacks-gateway:$ReleaseId" }
            Invoke-DockerBuild 'gateway' $ImageTag @(
                '--build-arg', "LUMBERJACKS_EXPECTED_MOD_RELEASE=$ReleaseId",
                '--build-arg', 'LUMBERJACKS_REQUIRE_RELEASE=1')
        }
        'AllImages' {
            if ([string]::IsNullOrWhiteSpace($ReleaseId)) { throw '-ReleaseId is required for AllImages' }
            foreach ($service in @('gateway', 'eventlog', 'progression', 'operatorapi')) {
                $tag = "lumberjacks-${service}:$ReleaseId"
                if ($service -eq 'gateway') {
                    Invoke-DockerBuild $service $tag @(
                        '--build-arg', "LUMBERJACKS_EXPECTED_MOD_RELEASE=$ReleaseId",
                        '--build-arg', 'LUMBERJACKS_REQUIRE_RELEASE=1')
                } else {
                    Invoke-DockerBuild $service $tag
                }
            }
        }
    }
}
finally {
    Pop-Location
}
