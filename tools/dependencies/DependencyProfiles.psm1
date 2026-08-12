Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-XmlDocument {
    param([Parameter(Mandatory)][string] $Path)
    $document = New-Object Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load([IO.Path]::GetFullPath($Path))
    return $document
}

function Convert-XmlDocumentToText {
    param([Parameter(Mandatory)][Xml.XmlDocument] $Document)
    $stream = New-Object IO.MemoryStream
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = New-Object Text.UTF8Encoding($false)
    $settings.Indent = $false
    $settings.NewLineHandling = [Xml.NewLineHandling]::None
    try {
        $writer = [Xml.XmlWriter]::Create($stream, $settings)
        try { $Document.Save($writer) } finally { $writer.Dispose() }
        return [Text.Encoding]::UTF8.GetString($stream.ToArray())
    }
    finally { $stream.Dispose() }
}

function Set-CentralPackageVersion {
    param(
        [Parameter(Mandatory)][Xml.XmlDocument] $Document,
        [Parameter(Mandatory)][string] $PackageId,
        [Parameter(Mandatory)][string] $Version
    )
    $nodes = @($Document.SelectNodes("//PackageVersion[@Include='$PackageId']"))
    if ($nodes.Count -ne 1) {
        throw "Expected exactly one central version for $PackageId; found $($nodes.Count)."
    }
    if ($nodes[0].GetAttribute('Version') -eq $Version) { return $false }
    $nodes[0].SetAttribute('Version', $Version)
    return $true
}

function Set-NuGetPackageSources {
    param(
        [Parameter(Mandatory)][Xml.XmlDocument] $Document,
        [Parameter(Mandatory)][object[]] $Sources
    )
    $packageSources = $Document.SelectSingleNode('/configuration/packageSources')
    if ($null -eq $packageSources) { throw 'nuget.config has no packageSources element.' }
    $current = @(@($packageSources.SelectNodes('add')) | ForEach-Object {
        [pscustomobject]@{ key = $_.GetAttribute('key'); value = $_.GetAttribute('value') }
    })
    $same = $current.Count -eq $Sources.Count
    if ($same) {
        for ($index = 0; $index -lt $Sources.Count; $index++) {
            if ($current[$index].key -cne [string]$Sources[$index].key -or
                $current[$index].value -cne [string]$Sources[$index].value) {
                $same = $false
                break
            }
        }
    }
    if ($same) { return $false }

    foreach ($child in @($packageSources.ChildNodes)) {
        if ($child.NodeType -eq [Xml.XmlNodeType]::Element -and $child.LocalName -eq 'clear') { continue }
        [void]$packageSources.RemoveChild($child)
    }
    foreach ($source in $Sources) {
        [void]$packageSources.AppendChild($Document.CreateWhitespace("`r`n    "))
        $add = $Document.CreateElement('add')
        $add.SetAttribute('key', [string]$source.key)
        $add.SetAttribute('value', [string]$source.value)
        [void]$packageSources.AppendChild($add)
    }
    [void]$packageSources.AppendChild($Document.CreateWhitespace("`r`n  "))
    return $true
}

function Test-IsQuestStudioProjectReference {
    param([string] $Include)
    $normalized = $Include.Replace('/', '\')
    return $normalized -match '(?i)(?:^|\\)(?:Quest\.Studio\\Quest\.Studio|Comfy\.Quest\.Studio)\.csproj$'
}

function Set-PackageReference {
    param(
        [Parameter(Mandatory)][Xml.XmlDocument] $Document,
        [Parameter(Mandatory)][string] $PackageId,
        [switch] $RemoveStudioProjectReference
    )
    $changed = $false
    if ($RemoveStudioProjectReference) {
        foreach ($node in @($Document.SelectNodes('//ProjectReference'))) {
            if (Test-IsQuestStudioProjectReference $node.GetAttribute('Include')) {
                [void]$node.ParentNode.RemoveChild($node)
                $changed = $true
            }
        }
    }
    $references = @($Document.SelectNodes("//PackageReference[@Include='$PackageId']"))
    if ($references.Count -gt 1) { throw "Duplicate PackageReference for $PackageId." }
    if ($references.Count -eq 0) {
        $itemGroup = @($Document.SelectNodes('/Project/ItemGroup')) | Select-Object -First 1
        if ($null -eq $itemGroup) {
            $itemGroup = $Document.CreateElement('ItemGroup')
            [void]$Document.DocumentElement.AppendChild($itemGroup)
        }
        [void]$itemGroup.AppendChild($Document.CreateWhitespace("`r`n    "))
        $reference = $Document.CreateElement('PackageReference')
        $reference.SetAttribute('Include', $PackageId)
        [void]$itemGroup.AppendChild($reference)
        [void]$itemGroup.AppendChild($Document.CreateWhitespace("`r`n  "))
        $references = @($reference)
        $changed = $true
    }
    foreach ($reference in $references) {
        if ($reference.HasAttribute('Version')) {
            $reference.RemoveAttribute('Version')
            $changed = $true
        }
    }
    return $changed
}

function Read-DependencyProfile {
    param([Parameter(Mandatory)][string] $Path)
    $profile = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($profile.schema -cne 'lumberjacks-dependency-profile/v1') {
        throw "Unsupported dependency profile schema in $Path."
    }
    if ($profile.name -notin @('interim', 'public')) {
        throw "Unsupported dependency profile name '$($profile.name)'."
    }
    $expectedVersion = if ($profile.name -eq 'public') { '[0.1.0]' } else { '[0.1.0-local]' }
    foreach ($packageId in @('Comfy.Quest.Contracts', 'Comfy.Quest.Studio')) {
        $property = $profile.packages.PSObject.Properties[$packageId]
        if ($null -eq $property -or [string]$property.Value -cne $expectedVersion) {
            throw "$($profile.name) profile must pin $packageId to exact $expectedVersion."
        }
    }
    if ($profile.companion_tests_studio_reference -cne 'PackageReference') {
        throw 'Companion.Tests must consume Studio through a PackageReference.'
    }
    $sources = @($profile.package_sources)
    $expectedKeys = if ($profile.name -eq 'public') { @('nuget.org') } else { @('packages-local', 'nuget.org') }
    if (($sources.key -join ',') -cne ($expectedKeys -join ',')) {
        throw "$($profile.name) profile has an unsafe package-source set."
    }
    return $profile
}

function Invoke-DependencyProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $ProfilePath,
        [switch] $Check
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $lumberjacksRoot = Join-Path $root 'Lumberjacks'
    $profile = Read-DependencyProfile $ProfilePath
    $paths = [ordered]@{
        'Lumberjacks/Directory.Packages.props' = (Join-Path $lumberjacksRoot 'Directory.Packages.props')
        'nuget.config' = (Join-Path $root 'nuget.config')
        'Lumberjacks/nuget.config' = (Join-Path $lumberjacksRoot 'nuget.config')
        'Lumberjacks/src/Game.Companion/Game.Companion.csproj' = (Join-Path $lumberjacksRoot 'src\Game.Companion\Game.Companion.csproj')
        'Lumberjacks/tests/Game.Companion.Tests/Game.Companion.Tests.csproj' = (Join-Path $lumberjacksRoot 'tests\Game.Companion.Tests\Game.Companion.Tests.csproj')
    }
    $documents = [ordered]@{}
    foreach ($relative in @($paths.Keys)) { $documents[$relative] = Read-XmlDocument $paths[$relative] }

    $changed = New-Object Collections.Generic.List[string]
    $versions = $documents['Lumberjacks/Directory.Packages.props']
    foreach ($packageId in @('Comfy.Quest.Contracts', 'Comfy.Quest.Studio')) {
        if (Set-CentralPackageVersion $versions $packageId ([string]$profile.packages.PSObject.Properties[$packageId].Value)) {
            $changed.Add('Lumberjacks/Directory.Packages.props')
        }
    }
    foreach ($relative in @('nuget.config', 'Lumberjacks/nuget.config')) {
        if (Set-NuGetPackageSources $documents[$relative] @($profile.package_sources)) {
            $changed.Add($relative)
        }
    }
    $companion = $documents['Lumberjacks/src/Game.Companion/Game.Companion.csproj']
    foreach ($packageId in @('Comfy.Quest.Contracts', 'Comfy.Quest.Studio')) {
        if (Set-PackageReference $companion $packageId -RemoveStudioProjectReference) {
            $changed.Add('Lumberjacks/src/Game.Companion/Game.Companion.csproj')
        }
    }
    $tests = $documents['Lumberjacks/tests/Game.Companion.Tests/Game.Companion.Tests.csproj']
    if (Set-PackageReference $tests 'Comfy.Quest.Studio' -RemoveStudioProjectReference) {
        $changed.Add('Lumberjacks/tests/Game.Companion.Tests/Game.Companion.Tests.csproj')
    }
    $uniqueChanged = @($changed | Select-Object -Unique)

    if ($Check) {
        if ($uniqueChanged.Count -gt 0) {
            throw "Dependency profile '$($profile.name)' is not active; would change: $($uniqueChanged -join ', ')."
        }
    }
    elseif ($uniqueChanged.Count -gt 0) {
        $original = @{}
        $pending = @{}
        try {
            foreach ($relative in @($documents.Keys)) {
                if ($uniqueChanged -notcontains $relative) { continue }
                $path = $paths[$relative]
                $original[$path] = [IO.File]::ReadAllText($path)
                $temporary = "$path.repin-$([guid]::NewGuid().ToString('N'))"
                [IO.File]::WriteAllText($temporary, (Convert-XmlDocumentToText $documents[$relative]), (New-Object Text.UTF8Encoding($false)))
                $pending[$path] = $temporary
            }
            foreach ($path in @($pending.Keys)) { Move-Item -LiteralPath $pending[$path] -Destination $path -Force }
        }
        catch {
            foreach ($path in @($original.Keys)) {
                [IO.File]::WriteAllText($path, $original[$path], (New-Object Text.UTF8Encoding($false)))
            }
            foreach ($temporary in @($pending.Values)) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
            throw
        }
    }

    return [pscustomobject]@{
        Schema = 'lumberjacks-dependency-repin/v1'
        Profile = [string]$profile.name
        ChangedFiles = $uniqueChanged
        Check = [bool]$Check
        Verdict = 'passed'
    }
}

Export-ModuleMember -Function Invoke-DependencyProfile
