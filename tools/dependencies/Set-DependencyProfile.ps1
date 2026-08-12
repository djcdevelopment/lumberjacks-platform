<#
.SYNOPSIS
Switches the Quest dependency profile as one rollback-safe transaction.

.DESCRIPTION
The interim profile uses only the checked-in 0.1.0-local rehearsal packages.
The public profile removes the local feed and pins both Quest packages to exact
[0.1.0]. The transaction also guarantees that Companion and Companion.Tests
consume Studio as a PackageReference, never a sibling ProjectReference.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('interim', 'public')]
    [string] $Profile,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null
Import-Module (Join-Path $PSScriptRoot 'DependencyProfiles.psm1') -Force

$profilePath = Join-Path $repoRoot "Lumberjacks\dependency-profiles\$Profile.json"
if ($Check) {
    Invoke-DependencyProfile -RepositoryRoot $repoRoot -ProfilePath $profilePath -Check |
        ConvertTo-Json -Depth 5
    exit 0
}
if (-not $PSCmdlet.ShouldProcess($repoRoot, "activate dependency profile '$Profile'")) { exit 0 }
if ($Profile -eq 'public') {
    foreach ($packageId in @('Comfy.Quest.Contracts', 'Comfy.Quest.Studio')) {
        $lowerId = $packageId.ToLowerInvariant()
        $url = "https://api.nuget.org/v3-flatcontainer/$lowerId/0.1.0/$lowerId.0.1.0.nupkg"
        try {
            $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 30
        }
        catch {
            throw "Public profile remains blocked: NuGet.org does not serve exact $packageId 0.1.0. $($_.Exception.Message)"
        }
        if ([int]$response.StatusCode -ne 200) {
            throw "Public profile remains blocked: NuGet.org returned HTTP $($response.StatusCode) for exact $packageId 0.1.0."
        }
    }
}
Invoke-DependencyProfile -RepositoryRoot $repoRoot -ProfilePath $profilePath |
    ConvertTo-Json -Depth 5
