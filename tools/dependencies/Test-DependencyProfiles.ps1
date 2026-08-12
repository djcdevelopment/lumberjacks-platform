[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $PSScriptRoot 'DependencyProfiles.psm1') -Force
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-dependencies-' + [guid]::NewGuid().ToString('N'))

try {
    foreach ($relative in @(
        'nuget.config',
        'Lumberjacks\nuget.config',
        'Lumberjacks\Directory.Packages.props',
        'Lumberjacks\src\Game.Companion\Game.Companion.csproj',
        'Lumberjacks\tests\Game.Companion.Tests\Game.Companion.Tests.csproj'
    )) {
        $source = Join-Path $repoRoot $relative
        $target = Join-Path $fixture $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }

    # Recreate the pre-split Studio reach-in. Public activation must remove it
    # and replace it with the centrally pinned PackageReference in one pass.
    $testsPath = Join-Path $fixture 'Lumberjacks\tests\Game.Companion.Tests\Game.Companion.Tests.csproj'
    $testsText = Get-Content -LiteralPath $testsPath -Raw
    $testsText = $testsText -replace '<PackageReference Include="Comfy\.Quest\.Studio"\s*/>', '<ProjectReference Include="..\..\src\Quest.Studio\Quest.Studio.csproj" />'
    [IO.File]::WriteAllText($testsPath, $testsText, (New-Object Text.UTF8Encoding($false)))

    $public = Invoke-DependencyProfile `
        -RepositoryRoot $fixture `
        -ProfilePath (Join-Path $repoRoot 'Lumberjacks\dependency-profiles\public.json')
    if ($public.ChangedFiles.Count -lt 4) { throw 'Public profile did not coordinate every expected seam.' }
    Invoke-DependencyProfile `
        -RepositoryRoot $fixture `
        -ProfilePath (Join-Path $repoRoot 'Lumberjacks\dependency-profiles\public.json') `
        -Check | Out-Null
    $publicTests = [xml](Get-Content -LiteralPath $testsPath -Raw)
    if (@($publicTests.SelectNodes('//ProjectReference[contains(@Include,"Quest.Studio")]')).Count -ne 0) {
        throw 'Public profile retained the Studio ProjectReference.'
    }
    if (@($publicTests.SelectNodes('//PackageReference[@Include="Comfy.Quest.Studio"]')).Count -ne 1) {
        throw 'Public profile did not add the Studio PackageReference.'
    }

    Invoke-DependencyProfile `
        -RepositoryRoot $fixture `
        -ProfilePath (Join-Path $repoRoot 'Lumberjacks\dependency-profiles\interim.json') | Out-Null
    Invoke-DependencyProfile `
        -RepositoryRoot $fixture `
        -ProfilePath (Join-Path $repoRoot 'Lumberjacks\dependency-profiles\interim.json') `
        -Check | Out-Null
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

[pscustomobject]@{
    Schema = 'lumberjacks-dependency-profile-test/v1'
    Profiles = @('public', 'interim')
    ProjectReferenceConversion = 'passed'
    Verdict = 'passed'
} | ConvertTo-Json -Compress
