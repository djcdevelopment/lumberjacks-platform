#Requires -Version 5.1

Set-StrictMode -Version 2.0

$script:QuestRepository = 'djcdevelopment/comfy-quest'
$script:QuestReleaseSchema = 'comfy-quest-release-manifest/v1'
$script:QuestLockSchema = 'lumberjacks-quest-release-lock/v1'
$script:QuestAssetNames = @(
    'questlab.html',
    'quest-lab.zip',
    'quest-picker.html',
    'quest-picker.zip'
)
$script:QuestReleaseFileNames = @(
    'questlab.html',
    'quest-lab.zip',
    'quest-picker.html',
    'quest-picker.zip',
    'release-manifest.json',
    'SHA256SUMS'
)
$script:QuestDestinations = [ordered]@{
    'questlab.html' = 'Lumberjacks/src/Game.Gateway/Community/questlab.html'
    'quest-lab.zip' = 'tools/workbench/dist/quest-lab.zip'
    'quest-picker.html' = 'tools/workbench/dist/quest-picker.html'
    'quest-picker.zip' = 'tools/workbench/dist/quest-picker.zip'
}
$script:QuestManifestDestination = 'Lumberjacks/docs/workbench/quest-release.manifest.json'

function Get-QuestReleaseAssetNames {
    return @($script:QuestAssetNames)
}

function Get-QuestReleaseFileNames {
    return @($script:QuestReleaseFileNames)
}

function Get-QuestReleaseSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-QuestBytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-QuestCondition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Test-QuestObjectProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    return $null -ne $Object.PSObject.Properties[$Name]
}

function Read-QuestJsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )
    try {
        $text = [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false, $true)))
        $value = $text | ConvertFrom-Json
    }
    catch {
        throw "$Label is not valid UTF-8 JSON: $($_.Exception.Message)"
    }
    Assert-QuestCondition ($null -ne $value -and $value -isnot [array]) "$Label must be a JSON object."
    return $value
}

function ConvertFrom-QuestJsonBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$Label
    )
    try {
        $text = (New-Object Text.UTF8Encoding($false, $true)).GetString($Bytes)
        $value = $text | ConvertFrom-Json
    }
    catch {
        throw "$Label is not valid UTF-8 JSON: $($_.Exception.Message)"
    }
    Assert-QuestCondition ($null -ne $value -and $value -isnot [array]) "$Label must be a JSON object."
    return $value
}

function Get-QuestVersionFromTag {
    param([Parameter(Mandatory = $true)][string]$ReleaseTag)
    $match = [regex]::Match($ReleaseTag, '^quest-v([0-9]+\.[0-9]+\.[0-9]+)-split-proof$')
    Assert-QuestCondition $match.Success 'Release tag must be quest-v<stable-semver>-split-proof.'
    return $match.Groups[1].Value
}

function Assert-QuestExactNames {
    param(
        [Parameter(Mandatory = $true)][string[]]$Actual,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $actualSorted = @($Actual | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $different = Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actualSorted
    if ($different) {
        throw "$Label drifted: expected [$($expectedSorted -join ', ')], got [$($actualSorted -join ', ')]."
    }
}

function Read-QuestSha256Sums {
    param([Parameter(Mandatory = $true)][string]$Path)
    $result = @{}
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($Path, (New-Object Text.UTF8Encoding($false, $true)))) {
        $lineNumber++
        $match = [regex]::Match($line, '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$')
        Assert-QuestCondition $match.Success "SHA256SUMS line $lineNumber is malformed."
        $name = $match.Groups[2].Value
        Assert-QuestCondition (-not $result.ContainsKey($name)) "SHA256SUMS repeats $name."
        $result[$name] = $match.Groups[1].Value
    }
    Assert-QuestExactNames @($result.Keys) $script:QuestAssetNames 'SHA256SUMS asset set'
    return $result
}

function Get-QuestArtifactMap {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Label
    )
    Assert-QuestCondition (Test-QuestObjectProperty $Manifest 'artifacts') "$Label has no artifacts array."
    $result = [ordered]@{}
    foreach ($row in @($Manifest.artifacts)) {
        Assert-QuestCondition ($null -ne $row -and $row -isnot [string]) "$Label contains a malformed artifact row."
        foreach ($property in @('name', 'sha256', 'bytes')) {
            Assert-QuestCondition (Test-QuestObjectProperty $row $property) "$Label artifact row is missing $property."
        }
        $name = [string]$row.name
        Assert-QuestCondition ($script:QuestAssetNames -contains $name) "$Label names unexpected artifact '$name'."
        Assert-QuestCondition (-not $result.Contains($name)) "$Label repeats artifact '$name'."
        $digest = [string]$row.sha256
        Assert-QuestCondition ($digest -cmatch '^[0-9a-f]{64}$') "$Label artifact '$name' has an invalid SHA-256."
        Assert-QuestCondition ($row.bytes -is [int] -or $row.bytes -is [long]) "$Label artifact '$name' bytes must be an integer."
        $bytes = [long]$row.bytes
        Assert-QuestCondition ($bytes -gt 0) "$Label artifact '$name' bytes must be positive."
        $result[$name] = [pscustomobject][ordered]@{
            name = $name
            sha256 = $digest
            bytes = $bytes
        }
    }
    Assert-QuestExactNames @($result.Keys) $script:QuestAssetNames "$Label artifact set"
    return $result
}

function Assert-QuestManifestIdentity {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$ExpectedTag
    )
    $version = Get-QuestVersionFromTag $ExpectedTag
    Assert-QuestCondition ([string]$Manifest.schema -ceq $script:QuestReleaseSchema) "Unexpected Quest release schema '$($Manifest.schema)'."
    Assert-QuestCondition ([string]$Manifest.repository -ceq $script:QuestRepository) "Unexpected Quest release repository '$($Manifest.repository)'."
    Assert-QuestCondition ([string]$Manifest.release_tag -ceq $ExpectedTag) 'Quest release manifest tag does not match the requested tag.'
    Assert-QuestCondition ([string]$Manifest.version -ceq $version) 'Quest release manifest version does not match its tag.'
    Assert-QuestCondition ([string]$Manifest.revision -cmatch '^[0-9a-f]{40}$') 'Quest release revision must be a full lowercase commit SHA.'
    Assert-QuestCondition (Test-QuestObjectProperty $Manifest 'quest_lab') 'Quest release manifest has no Quest Lab identity.'
    $questLab = $Manifest.quest_lab
    Assert-QuestCondition ([string]$questLab.package_schema -ceq 'comfy-quest-package/v1') 'Quest Lab package schema drifted.'
    Assert-QuestCondition ([string]$questLab.plugin_version -ceq $version) 'Quest Lab plugin version does not match the release tag.'
    Assert-QuestCondition (-not [string]::IsNullOrWhiteSpace([string]$questLab.release_id) -and [string]$questLab.release_id -cne 'dev') 'Quest Lab release ID is missing or unbaked.'
    Assert-QuestCondition ([string]$questLab.dll_sha256 -cmatch '^[0-9a-f]{64}$') 'Quest Lab DLL SHA-256 is invalid.'
    Assert-QuestCondition (($questLab.dll_bytes -is [int] -or $questLab.dll_bytes -is [long]) -and [long]$questLab.dll_bytes -gt 0) 'Quest Lab DLL byte count is invalid.'
    return $version
}

function Get-QuestSafeZipEntries {
    param(
        [Parameter(Mandatory = $true)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $result = @{}
    foreach ($entry in $Archive.Entries) {
        $name = [string]$entry.FullName
        $parts = @($name -split '/')
        $unsafe = [string]::IsNullOrWhiteSpace($name) -or
            $name.StartsWith('/') -or
            $name.Contains('\') -or
            [IO.Path]::IsPathRooted($name) -or
            ($parts -contains '..')
        Assert-QuestCondition (-not $unsafe) "$Label contains unsafe ZIP entry '$name'."
        Assert-QuestCondition (-not $result.ContainsKey($name)) "$Label repeats ZIP entry '$name'."
        $result[$name] = $entry
    }
    return $result
}

function Read-QuestZipEntryBytes {
    param([Parameter(Mandatory = $true)][IO.Compression.ZipArchiveEntry]$Entry)
    $inputStream = $Entry.Open()
    $memory = New-Object IO.MemoryStream
    try {
        $inputStream.CopyTo($memory)
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $inputStream.Dispose()
    }
}

function Assert-QuestPayloadContracts {
    param(
        [Parameter(Mandatory = $true)][hashtable]$AssetPaths,
        [Parameter(Mandatory = $true)]$Manifest
    )
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $questlabText = [IO.File]::ReadAllText($AssetPaths['questlab.html'], $strictUtf8)
    Assert-QuestCondition $questlabText.Contains('<title>ComfyQuestLab') 'questlab.html does not look like the generated Quest Lab tome.'

    $pickerBytes = [IO.File]::ReadAllBytes($AssetPaths['quest-picker.html'])
    $pickerText = $strictUtf8.GetString($pickerBytes)
    Assert-QuestCondition $pickerText.Contains('SAMPLE-CATALOG: synthetic demonstration data only') 'quest-picker.html is not the synthetic public artifact.'

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $pickerArchive = [IO.Compression.ZipFile]::OpenRead($AssetPaths['quest-picker.zip'])
    try {
        $pickerEntries = Get-QuestSafeZipEntries $pickerArchive 'quest-picker.zip'
        Assert-QuestCondition $pickerEntries.ContainsKey('quest-picker.html') 'quest-picker.zip has no quest-picker.html.'
        $zippedPicker = Read-QuestZipEntryBytes $pickerEntries['quest-picker.html']
        Assert-QuestCondition ([Convert]::ToBase64String($pickerBytes) -ceq [Convert]::ToBase64String($zippedPicker)) 'Standalone Quest Picker differs from the ZIP entry.'
    }
    finally {
        $pickerArchive.Dispose()
    }

    $labArchive = [IO.Compression.ZipFile]::OpenRead($AssetPaths['quest-lab.zip'])
    try {
        $labEntries = Get-QuestSafeZipEntries $labArchive 'quest-lab.zip'
        Assert-QuestCondition $labEntries.ContainsKey('manifest.json') 'quest-lab.zip has no manifest.json.'
        Assert-QuestCondition $labEntries.ContainsKey('ComfyQuestLab.dll') 'quest-lab.zip has no ComfyQuestLab.dll.'
        $packageManifest = ConvertFrom-QuestJsonBytes (Read-QuestZipEntryBytes $labEntries['manifest.json']) 'quest-lab.zip manifest.json'
        $dllBytes = Read-QuestZipEntryBytes $labEntries['ComfyQuestLab.dll']
    }
    finally {
        $labArchive.Dispose()
    }

    $questLab = $Manifest.quest_lab
    Assert-QuestCondition ([string]$packageManifest.schema -ceq [string]$questLab.package_schema) 'Quest Lab ZIP schema does not match the release manifest.'
    Assert-QuestCondition ([string]$packageManifest.tool -ceq 'quest-lab') 'Quest Lab ZIP names the wrong tool.'
    Assert-QuestCondition ([string]$packageManifest.version -ceq [string]$questLab.plugin_version) 'Quest Lab ZIP version does not match the release manifest.'
    Assert-QuestCondition ([string]$packageManifest.release_id -ceq [string]$questLab.release_id) 'Quest Lab ZIP release ID does not match the release manifest.'
    $dllRows = @($packageManifest.files | Where-Object { $null -ne $_ -and [string]$_.path -ceq 'ComfyQuestLab.dll' })
    Assert-QuestCondition ($dllRows.Count -eq 1) 'Quest Lab ZIP manifest must identify exactly one plugin DLL.'
    $dllSha = Get-QuestBytesSha256 $dllBytes
    Assert-QuestCondition ([string]$dllRows[0].sha256 -ceq $dllSha -and [long]$dllRows[0].bytes -eq $dllBytes.Length) 'Quest Lab ZIP manifest DLL record does not match the DLL.'
    Assert-QuestCondition ([string]$questLab.dll_sha256 -ceq $dllSha -and [long]$questLab.dll_bytes -eq $dllBytes.Length) 'Quest release DLL identity does not match the Quest Lab ZIP.'
}

function Test-QuestReleaseBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedTag
    )
    $releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
    Assert-QuestCondition (Test-Path -LiteralPath $releaseRoot -PathType Container) "Quest release directory does not exist: $releaseRoot"
    $children = @(Get-ChildItem -LiteralPath $releaseRoot -Force)
    Assert-QuestCondition (@($children | Where-Object { $_.PSIsContainer }).Count -eq 0) 'Quest release directory must not contain subdirectories.'
    Assert-QuestExactNames @($children.Name) $script:QuestReleaseFileNames 'Published Quest release file set'

    $manifestPath = Join-Path $releaseRoot 'release-manifest.json'
    $manifest = Read-QuestJsonFile $manifestPath 'release-manifest.json'
    $version = Assert-QuestManifestIdentity $manifest $ExpectedTag
    $artifactMap = Get-QuestArtifactMap $manifest 'release-manifest.json'
    $checksums = Read-QuestSha256Sums (Join-Path $releaseRoot 'SHA256SUMS')
    $assetPaths = @{}
    foreach ($name in $script:QuestAssetNames) {
        $path = Join-Path $releaseRoot $name
        $actualSha = Get-QuestReleaseSha256 $path
        $actualBytes = (Get-Item -LiteralPath $path).Length
        $row = $artifactMap[$name]
        Assert-QuestCondition ($actualSha -ceq [string]$row.sha256 -and $actualBytes -eq [long]$row.bytes) "release-manifest.json does not match $name."
        Assert-QuestCondition ([string]$checksums[$name] -ceq $actualSha) "SHA256SUMS does not match $name."
        $assetPaths[$name] = $path
    }
    Assert-QuestPayloadContracts $assetPaths $manifest

    return [pscustomobject][ordered]@{
        Tag = $ExpectedTag
        Version = $version
        Revision = [string]$manifest.revision
        PublishedRepository = [string]$manifest.repository
        Manifest = $manifest
        ManifestPath = $manifestPath
        ManifestSha256 = Get-QuestReleaseSha256 $manifestPath
        Artifacts = $artifactMap
        AssetPaths = $assetPaths
    }
}

function ConvertTo-QuestCanonicalTimestamp {
    param([Parameter(Mandatory = $true)]$Timestamp)
    try {
        if ($Timestamp -is [DateTimeOffset]) {
            $parsed = [DateTimeOffset]$Timestamp
        }
        elseif ($Timestamp -is [DateTime]) {
            $dateTime = [DateTime]$Timestamp
            if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
                $dateTime = [DateTime]::SpecifyKind($dateTime, [DateTimeKind]::Utc)
            }
            $parsed = [DateTimeOffset]$dateTime
        }
        else {
            $parsed = [DateTimeOffset]::Parse(
                [string]$Timestamp,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind)
        }
    }
    catch {
        throw "PublishedAt is not a timestamp: $Timestamp"
    }
    return $parsed.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
}

function New-UnpinnedQuestReleaseLock {
    return [pscustomobject][ordered]@{
        schema = $script:QuestLockSchema
        repository = $script:QuestRepository
        state = 'unpinned'
        release_tag = $null
        release_manifest_sha256 = $null
        revision = $null
        version = $null
        published_at = $null
        artifacts = @()
        destinations = @()
    }
}

function New-PinnedQuestReleaseLock {
    param(
        [Parameter(Mandatory = $true)]$Verification,
        [Parameter(Mandatory = $true)][string]$PublishedAt
    )
    $artifacts = @()
    foreach ($name in $script:QuestAssetNames) {
        $row = $Verification.Artifacts[$name]
        $artifacts += [pscustomobject][ordered]@{
            name = [string]$row.name
            sha256 = [string]$row.sha256
            bytes = [long]$row.bytes
        }
    }
    $destinations = @()
    foreach ($name in $script:QuestAssetNames) {
        $destinations += [pscustomobject][ordered]@{
            asset = $name
            path = [string]$script:QuestDestinations[$name]
        }
    }
    return [pscustomobject][ordered]@{
        schema = $script:QuestLockSchema
        repository = $script:QuestRepository
        state = 'pinned'
        release_tag = [string]$Verification.Tag
        release_manifest_sha256 = [string]$Verification.ManifestSha256
        revision = [string]$Verification.Revision
        version = [string]$Verification.Version
        published_at = ConvertTo-QuestCanonicalTimestamp $PublishedAt
        artifacts = $artifacts
        destinations = $destinations
    }
}

function ConvertTo-QuestLockJson {
    param([Parameter(Mandatory = $true)]$Lock)
    return ($Lock | ConvertTo-Json -Depth 10 -Compress) + [char]10
}

function Get-QuestCanonicalLock {
    param([Parameter(Mandatory = $true)]$Lock)
    Assert-QuestCondition ([string]$Lock.schema -ceq $script:QuestLockSchema) "Unexpected Quest lock schema '$($Lock.schema)'."
    Assert-QuestCondition ([string]$Lock.repository -ceq $script:QuestRepository) "Unexpected Quest lock repository '$($Lock.repository)'."
    $state = [string]$Lock.state
    Assert-QuestCondition ($state -ceq 'unpinned' -or $state -ceq 'pinned') "Unexpected Quest lock state '$state'."
    if ($state -ceq 'unpinned') {
        foreach ($name in @('release_tag', 'release_manifest_sha256', 'revision', 'version', 'published_at')) {
            Assert-QuestCondition ($null -eq $Lock.$name) "Unpinned Quest lock must leave $name null."
        }
        Assert-QuestCondition (@($Lock.artifacts).Count -eq 0) 'Unpinned Quest lock must contain no artifacts.'
        Assert-QuestCondition (@($Lock.destinations).Count -eq 0) 'Unpinned Quest lock must contain no destinations.'
        return New-UnpinnedQuestReleaseLock
    }

    [void](Get-QuestVersionFromTag ([string]$Lock.release_tag))
    Assert-QuestCondition ([string]$Lock.release_manifest_sha256 -cmatch '^[0-9a-f]{64}$') 'Pinned Quest lock manifest SHA-256 is invalid.'
    Assert-QuestCondition ([string]$Lock.revision -cmatch '^[0-9a-f]{40}$') 'Pinned Quest lock revision is invalid.'
    Assert-QuestCondition ([string]$Lock.version -ceq (Get-QuestVersionFromTag ([string]$Lock.release_tag))) 'Pinned Quest lock version does not match its tag.'
    $publishedAt = ConvertTo-QuestCanonicalTimestamp $Lock.published_at
    $artifactMap = Get-QuestArtifactMap $Lock 'Quest lock'
    $destinations = @($Lock.destinations)
    Assert-QuestCondition ($destinations.Count -eq $script:QuestAssetNames.Count) 'Pinned Quest lock must name exactly four destinations.'
    $destinationMap = @{}
    foreach ($destination in $destinations) {
        $asset = [string]$destination.asset
        Assert-QuestCondition ($script:QuestDestinations.Contains($asset)) "Pinned Quest lock names unexpected destination asset '$asset'."
        Assert-QuestCondition (-not $destinationMap.ContainsKey($asset)) "Pinned Quest lock repeats destination '$asset'."
        Assert-QuestCondition ([string]$destination.path -ceq [string]$script:QuestDestinations[$asset]) "Pinned Quest lock destination for '$asset' drifted."
        $destinationMap[$asset] = [string]$destination.path
    }
    Assert-QuestExactNames @($destinationMap.Keys) $script:QuestAssetNames 'Pinned Quest lock destinations'

    return [pscustomobject][ordered]@{
        schema = $script:QuestLockSchema
        repository = $script:QuestRepository
        state = 'pinned'
        release_tag = [string]$Lock.release_tag
        release_manifest_sha256 = [string]$Lock.release_manifest_sha256
        revision = [string]$Lock.revision
        version = [string]$Lock.version
        published_at = $publishedAt
        artifacts = @($script:QuestAssetNames | ForEach-Object { $artifactMap[$_] })
        destinations = @($script:QuestAssetNames | ForEach-Object {
            [pscustomobject][ordered]@{ asset = $_; path = [string]$destinationMap[$_] }
        })
    }
}

function Get-QuestUpdatedToolBlock {
    param(
        [Parameter(Mandatory = $true)][string]$Block,
        [Parameter(Mandatory = $true)][string]$ToolId,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [Parameter(Mandatory = $true)][long]$Bytes,
        [Parameter(Mandatory = $true)][string]$PublishedAt
    )
    $accessStart = $Block.IndexOf('"access": {', [StringComparison]::Ordinal)
    Assert-QuestCondition ($accessStart -ge 0) "Workbench tool '$ToolId' has no access object."
    $accessEndMarker = [string][char]10 + '      },'
    $accessEnd = $Block.IndexOf($accessEndMarker, $accessStart, [StringComparison]::Ordinal)
    Assert-QuestCondition ($accessEnd -gt $accessStart) "Workbench tool '$ToolId' access object could not be bounded."
    $access = $Block.Substring($accessStart, $accessEnd - $accessStart)
    $replacements = @(
        @{ Pattern = '(?m)^(\s*"sha256"\s*:\s*)"[0-9a-f]{64}"(,\s*)$'; Value = '"' + $Sha256 + '"' },
        @{ Pattern = '(?m)^(\s*"size_bytes"\s*:\s*)[0-9]+(,\s*)$'; Value = [string]$Bytes },
        @{ Pattern = '(?m)^(\s*"published_at"\s*:\s*)"[^"]+"(\s*)$'; Value = '"' + $PublishedAt + '"' }
    )
    foreach ($replacement in $replacements) {
        $regex = New-Object Text.RegularExpressions.Regex([string]$replacement.Pattern)
        Assert-QuestCondition ($regex.Matches($access).Count -eq 1) "Workbench tool '$ToolId' access field shape drifted."
        $value = [string]$replacement.Value
        $access = $regex.Replace(
            $access,
            { param($match) $match.Groups[1].Value + $value + $match.Groups[2].Value },
            1)
    }
    return $Block.Substring(0, $accessStart) + $access + $Block.Substring($accessEnd)
}

function Get-QuestUpdatedWorkbenchCatalogText {
    param(
        [Parameter(Mandatory = $true)][string]$CatalogText,
        [Parameter(Mandatory = $true)]$Verification,
        [Parameter(Mandatory = $true)][string]$PublishedAt
    )
    $canonicalPublishedAt = ConvertTo-QuestCanonicalTimestamp $PublishedAt
    $updated = $CatalogText
    foreach ($mapping in @(
        @{ Id = 'quest-picker'; Asset = 'quest-picker.zip' },
        @{ Id = 'quest-lab'; Asset = 'quest-lab.zip' }
    )) {
        $needle = '"id": "' + $mapping.Id + '"'
        $start = $updated.IndexOf($needle, [StringComparison]::Ordinal)
        Assert-QuestCondition ($start -ge 0) "Workbench catalog has no tool '$($mapping.Id)'."
        $nextMarker = [string][char]10 + '    },' + [char]10 + '    {'
        $next = $updated.IndexOf($nextMarker, $start, [StringComparison]::Ordinal)
        if ($next -lt 0) { $next = $updated.Length }
        $block = $updated.Substring($start, $next - $start)
        $row = $Verification.Artifacts[$mapping.Asset]
        $newBlock = Get-QuestUpdatedToolBlock $block $mapping.Id ([string]$row.sha256) ([long]$row.bytes) $canonicalPublishedAt
        $updated = $updated.Substring(0, $start) + $newBlock + $updated.Substring($next)
    }
    try { $catalog = $updated | ConvertFrom-Json }
    catch { throw "Updated Workbench catalog is not valid JSON: $($_.Exception.Message)" }
    foreach ($mapping in @(
        @{ Id = 'quest-picker'; Asset = 'quest-picker.zip' },
        @{ Id = 'quest-lab'; Asset = 'quest-lab.zip' }
    )) {
        $tool = @($catalog.tools | Where-Object { [string]$_.id -ceq $mapping.Id })
        Assert-QuestCondition ($tool.Count -eq 1) "Updated Workbench catalog must contain one '$($mapping.Id)' tool."
        $row = $Verification.Artifacts[$mapping.Asset]
        Assert-QuestCondition ([string]$tool[0].access.sha256 -ceq [string]$row.sha256) 'Updated Workbench catalog SHA-256 did not round-trip.'
        Assert-QuestCondition ([long]$tool[0].access.size_bytes -eq [long]$row.bytes) 'Updated Workbench catalog byte count did not round-trip.'
        $catalogPublishedAt = ConvertTo-QuestCanonicalTimestamp $tool[0].access.published_at
        Assert-QuestCondition ($catalogPublishedAt -ceq $canonicalPublishedAt) 'Updated Workbench catalog timestamp did not round-trip.'
    }
    return $updated
}

function Write-QuestUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $Content, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Copy-QuestVerifiedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($Destination) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        Assert-QuestCondition ((Get-QuestReleaseSha256 $temporary) -ceq $ExpectedSha256) "Staged copy of '$Source' failed SHA-256 verification."
        Move-Item -LiteralPath $temporary -Destination $Destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Install-QuestReleaseBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Verification,
        [Parameter(Mandatory = $true)][string]$PublishedAt,
        [Parameter(Mandatory = $true)][string]$ExpectedManifestSha256,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][string]$CatalogPath
    )
    $expectedManifest = $ExpectedManifestSha256.ToLowerInvariant()
    Assert-QuestCondition ($expectedManifest -cmatch '^[0-9a-f]{64}$') 'Expected manifest SHA-256 must be exactly 64 hexadecimal characters.'
    Assert-QuestCondition ([string]$Verification.ManifestSha256 -ceq $expectedManifest) 'Downloaded Quest release manifest does not match -ExpectedManifestSha256; nothing was installed.'
    $repo = [IO.Path]::GetFullPath($RepoRoot)
    $catalogText = [IO.File]::ReadAllText($CatalogPath, (New-Object Text.UTF8Encoding($false, $true)))
    $updatedCatalog = Get-QuestUpdatedWorkbenchCatalogText $catalogText $Verification $PublishedAt
    $lock = New-PinnedQuestReleaseLock $Verification $PublishedAt
    $lockText = ConvertTo-QuestLockJson $lock

    foreach ($name in $script:QuestAssetNames) {
        $destination = Join-Path $repo ([string]$script:QuestDestinations[$name])
        Copy-QuestVerifiedFile $Verification.AssetPaths[$name] $destination ([string]$Verification.Artifacts[$name].sha256)
    }
    $manifestDestination = Join-Path $repo $script:QuestManifestDestination
    Copy-QuestVerifiedFile $Verification.ManifestPath $manifestDestination ([string]$Verification.ManifestSha256)
    Write-QuestUtf8NoBom $CatalogPath $updatedCatalog
    # The lock is deliberately last: a failed copy or catalog update must never create a false pin.
    Write-QuestUtf8NoBom $LockPath $lockText
    return $lock
}

function Test-QuestReleaseLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][string]$CatalogPath
    )
    $raw = [IO.File]::ReadAllText($LockPath, (New-Object Text.UTF8Encoding($false, $true)))
    $lock = Read-QuestJsonFile $LockPath 'Quest release lock'
    $canonical = Get-QuestCanonicalLock $lock
    $expectedText = ConvertTo-QuestLockJson $canonical
    $normalizedRaw = $raw.Replace(([string][char]13 + [char]10), [string][char]10).TrimEnd([char]13, [char]10) + [char]10
    Assert-QuestCondition ($normalizedRaw -ceq $expectedText) 'Quest release lock is not in deterministic canonical form.'
    if ([string]$canonical.state -ceq 'unpinned') {
        return [pscustomobject][ordered]@{
            State = 'unpinned'
            Repository = $script:QuestRepository
            Assets = 0
            Message = 'No published comfy-quest release is pinned.'
        }
    }

    $repo = [IO.Path]::GetFullPath($RepoRoot)
    $manifestPath = Join-Path $repo $script:QuestManifestDestination
    Assert-QuestCondition (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Pinned Quest manifest is missing: $manifestPath"
    Assert-QuestCondition ((Get-QuestReleaseSha256 $manifestPath) -ceq [string]$canonical.release_manifest_sha256) 'Pinned Quest release manifest hash does not match the lock.'
    $manifest = Read-QuestJsonFile $manifestPath 'Pinned Quest release manifest'
    [void](Assert-QuestManifestIdentity $manifest ([string]$canonical.release_tag))
    Assert-QuestCondition ([string]$manifest.revision -ceq [string]$canonical.revision) 'Pinned Quest manifest revision does not match the lock.'
    $manifestArtifacts = Get-QuestArtifactMap $manifest 'Pinned Quest release manifest'
    $lockArtifacts = Get-QuestArtifactMap $canonical 'Quest lock'
    $assetPaths = @{}
    foreach ($name in $script:QuestAssetNames) {
        $manifestRow = $manifestArtifacts[$name]
        $lockRow = $lockArtifacts[$name]
        Assert-QuestCondition ([string]$manifestRow.sha256 -ceq [string]$lockRow.sha256 -and [long]$manifestRow.bytes -eq [long]$lockRow.bytes) "Pinned Quest manifest and lock disagree for $name."
        $path = Join-Path $repo ([string]$script:QuestDestinations[$name])
        Assert-QuestCondition (Test-Path -LiteralPath $path -PathType Leaf) "Pinned Quest asset is missing: $path"
        Assert-QuestCondition ((Get-QuestReleaseSha256 $path) -ceq [string]$lockRow.sha256) "Pinned Quest asset hash does not match for $name."
        Assert-QuestCondition ((Get-Item -LiteralPath $path).Length -eq [long]$lockRow.bytes) "Pinned Quest asset byte count does not match for $name."
        $assetPaths[$name] = $path
    }
    Assert-QuestPayloadContracts $assetPaths $manifest

    $catalog = Read-QuestJsonFile $CatalogPath 'Workbench catalog'
    foreach ($mapping in @(
        @{ Id = 'quest-picker'; Asset = 'quest-picker.zip' },
        @{ Id = 'quest-lab'; Asset = 'quest-lab.zip' }
    )) {
        $tool = @($catalog.tools | Where-Object { [string]$_.id -ceq $mapping.Id })
        Assert-QuestCondition ($tool.Count -eq 1) "Workbench catalog must contain one '$($mapping.Id)' tool."
        $row = $lockArtifacts[$mapping.Asset]
        Assert-QuestCondition ([string]$tool[0].access.sha256 -ceq [string]$row.sha256) "Workbench '$($mapping.Id)' SHA-256 does not match the Quest lock."
        Assert-QuestCondition ([long]$tool[0].access.size_bytes -eq [long]$row.bytes) "Workbench '$($mapping.Id)' byte count does not match the Quest lock."
        $catalogPublishedAt = ConvertTo-QuestCanonicalTimestamp $tool[0].access.published_at
        Assert-QuestCondition ($catalogPublishedAt -ceq [string]$canonical.published_at) "Workbench '$($mapping.Id)' publication timestamp does not match the Quest lock."
    }

    return [pscustomobject][ordered]@{
        State = 'pinned'
        Repository = $script:QuestRepository
        Tag = [string]$canonical.release_tag
        Revision = [string]$canonical.revision
        ManifestSha256 = [string]$canonical.release_manifest_sha256
        Assets = $script:QuestAssetNames.Count
    }
}

Export-ModuleMember -Function @(
    'Get-QuestReleaseAssetNames',
    'Get-QuestReleaseFileNames',
    'Get-QuestReleaseSha256',
    'ConvertTo-QuestCanonicalTimestamp',
    'Test-QuestReleaseBundle',
    'New-UnpinnedQuestReleaseLock',
    'New-PinnedQuestReleaseLock',
    'ConvertTo-QuestLockJson',
    'Get-QuestUpdatedWorkbenchCatalogText',
    'Install-QuestReleaseBundle',
    'Test-QuestReleaseLock'
)
