#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $PSScriptRoot 'QuestRelease.psm1') -Force
$fixturePath = Join-Path $PSScriptRoot 'fixtures\quest-release-v1.json'
$fixture = [IO.File]::ReadAllText($fixturePath) | ConvertFrom-Json
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-quest-import-test-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$checks = New-Object Collections.Generic.List[string]
$lf = [string][char]10

function Assert-Test {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Label)
    try {
        & $Action
        throw "$Label unexpectedly passed."
    }
    catch {
        if ($_.Exception.Message -eq "$Label unexpectedly passed.") { throw }
        $checks.Add($Label + ': rejected')
    }
}

function Get-BytesSha256 {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Write-TestZip {
    param([string]$Path, [hashtable]$Entries)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($name in @($Entries.Keys | Sort-Object)) {
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::NoCompression)
                $entry.LastWriteTime = [DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
                $entryStream = $entry.Open()
                try {
                    $bytes = [byte[]]$Entries[$name]
                    $entryStream.Write($bytes, 0, $bytes.Length)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$fixtureTimestamp = '2030-01-02T03:04:05Z'
Assert-Test ((ConvertTo-QuestCanonicalTimestamp $fixture.published_at) -ceq $fixtureTimestamp) 'JSON-coerced fixture timestamp did not remain canonical.'
$fixtureDateTime = [DateTime]::Parse(
    $fixtureTimestamp,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind)
Assert-Test ((ConvertTo-QuestCanonicalTimestamp $fixtureTimestamp) -ceq $fixtureTimestamp) 'String timestamp did not remain canonical.'
Assert-Test ((ConvertTo-QuestCanonicalTimestamp $fixtureDateTime) -ceq $fixtureTimestamp) 'DateTime timestamp did not remain canonical.'
$checks.Add('PowerShell 5 string and PowerShell 7 DateTime timestamps: passed')

function New-ReleaseFixture {
    param([string]$Path)
    New-Item -ItemType Directory -Path $Path | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'questlab.html'), [string]$fixture.questlab_html, $utf8)
    [IO.File]::WriteAllText((Join-Path $Path 'quest-picker.html'), [string]$fixture.quest_picker_html, $utf8)
    $dll = [Convert]::FromBase64String([string]$fixture.quest_lab_dll_base64)
    $packageManifest = [pscustomobject][ordered]@{
        schema = 'comfy-quest-package/v1'
        tool = 'quest-lab'
        version = [string]$fixture.version
        release_id = [string]$fixture.release_id
        files = @([pscustomobject][ordered]@{
            path = 'ComfyQuestLab.dll'
            sha256 = Get-BytesSha256 $dll
            bytes = $dll.Length
        })
    }
    $packageJson = $packageManifest | ConvertTo-Json -Depth 6
    $packageJson = $packageJson.Replace(([string][char]13 + [char]10), $lf) + $lf
    $packageBytes = $utf8.GetBytes($packageJson)
    Write-TestZip (Join-Path $Path 'quest-lab.zip') @{
        'ComfyQuestLab.dll' = $dll
        'manifest.json' = $packageBytes
    }
    $pickerBytes = [IO.File]::ReadAllBytes((Join-Path $Path 'quest-picker.html'))
    Write-TestZip (Join-Path $Path 'quest-picker.zip') @{ 'quest-picker.html' = $pickerBytes }

    $records = @()
    foreach ($name in @($fixture.expected_assets)) {
        $assetPath = Join-Path $Path $name
        $records += [pscustomobject][ordered]@{
            name = [string]$name
            sha256 = Get-QuestReleaseSha256 $assetPath
            bytes = (Get-Item -LiteralPath $assetPath).Length
        }
    }
    $manifest = [pscustomobject][ordered]@{
        schema = 'comfy-quest-release-manifest/v1'
        repository = 'djcdevelopment/comfy-quest'
        release_tag = [string]$fixture.release_tag
        revision = [string]$fixture.revision
        version = [string]$fixture.version
        quest_lab = [pscustomobject][ordered]@{
            package_schema = [string]$packageManifest.schema
            plugin_version = [string]$fixture.version
            release_id = [string]$fixture.release_id
            dll_sha256 = Get-BytesSha256 $dll
            dll_bytes = $dll.Length
        }
        artifacts = $records
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    $manifestJson = $manifestJson.Replace(([string][char]13 + [char]10), $lf) + $lf
    [IO.File]::WriteAllText((Join-Path $Path 'release-manifest.json'), $manifestJson, $utf8)
    $sumLines = @($records | ForEach-Object { [string]$_.sha256 + '  ' + [string]$_.name })
    [IO.File]::WriteAllText((Join-Path $Path 'SHA256SUMS'), ($sumLines -join $lf) + $lf, $utf8)
}

New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $releaseDir = Join-Path $temporary 'release'
    New-ReleaseFixture $releaseDir
    $verification = Test-QuestReleaseBundle -ReleaseDirectory $releaseDir -ExpectedTag ([string]$fixture.release_tag)
    Assert-Test ($verification.Artifacts.Count -eq 4) 'Positive fixture did not verify exactly four Quest assets.'
    $checks.Add('valid four-asset release: passed')

    $fakeRepo = Join-Path $temporary 'platform'
    New-Item -ItemType Directory -Path $fakeRepo | Out-Null
    $catalogPath = Join-Path $fakeRepo 'Lumberjacks\docs\workbench\workbench.json'
    $lockPath = Join-Path $fakeRepo 'Lumberjacks\docs\workbench\quest-release.lock.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $catalogPath) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Lumberjacks\docs\workbench\workbench.json') -Destination $catalogPath
    $installParameters = @{
        Verification = $verification
        PublishedAt = [string]$fixture.published_at
        ExpectedManifestSha256 = [string]$verification.ManifestSha256
        RepoRoot = $fakeRepo
        LockPath = $lockPath
        CatalogPath = $catalogPath
    }
    [void](Install-QuestReleaseBundle @installParameters)
    $result = Test-QuestReleaseLock -RepoRoot $fakeRepo -LockPath $lockPath -CatalogPath $catalogPath
    Assert-Test ($result.State -ceq 'pinned' -and $result.Assets -eq 4) 'Installed fixture did not pass deterministic lock check.'
    $lockBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($lockPath))
    [void](Test-QuestReleaseLock -RepoRoot $fakeRepo -LockPath $lockPath -CatalogPath $catalogPath)
    $lockAfter = [Convert]::ToBase64String([IO.File]::ReadAllBytes($lockPath))
    Assert-Test ($lockBefore -ceq $lockAfter) 'Check mode mutated the lock.'
    $checks.Add('pin, vendor, catalog update, deterministic check: passed')

    $mismatchRepo = Join-Path $temporary 'manifest-mismatch-platform'
    New-Item -ItemType Directory -Path $mismatchRepo | Out-Null
    $mismatchCatalog = Join-Path $mismatchRepo 'Lumberjacks\docs\workbench\workbench.json'
    $mismatchLock = Join-Path $mismatchRepo 'Lumberjacks\docs\workbench\quest-release.lock.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $mismatchCatalog) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Lumberjacks\docs\workbench\workbench.json') -Destination $mismatchCatalog
    $mismatchParameters = @{
        Verification = $verification
        PublishedAt = [string]$fixture.published_at
        ExpectedManifestSha256 = ('f' * 64)
        RepoRoot = $mismatchRepo
        LockPath = $mismatchLock
        CatalogPath = $mismatchCatalog
    }
    Assert-Rejected { Install-QuestReleaseBundle @mismatchParameters | Out-Null } 'expected manifest hash mismatch'
    Assert-Test (-not (Test-Path -LiteralPath $mismatchLock)) 'Manifest hash mismatch created a lock.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $mismatchRepo 'tools\workbench\dist'))) 'Manifest hash mismatch staged assets.'

    $tamperedStage = Join-Path $fakeRepo 'tools\workbench\dist\quest-lab.zip'
    [IO.File]::AppendAllText($tamperedStage, 'tamper', $utf8)
    Assert-Rejected { Test-QuestReleaseLock -RepoRoot $fakeRepo -LockPath $lockPath -CatalogPath $catalogPath | Out-Null } 'staged ZIP tamper'

    $tamperedRelease = Join-Path $temporary 'tampered-release'
    Copy-Item -LiteralPath $releaseDir -Destination $tamperedRelease -Recurse
    [IO.File]::AppendAllText((Join-Path $tamperedRelease 'questlab.html'), 'tamper', $utf8)
    Assert-Rejected { Test-QuestReleaseBundle -ReleaseDirectory $tamperedRelease -ExpectedTag ([string]$fixture.release_tag) | Out-Null } 'source asset tamper'

    $extraRelease = Join-Path $temporary 'extra-release'
    Copy-Item -LiteralPath $releaseDir -Destination $extraRelease -Recurse
    [IO.File]::WriteAllText((Join-Path $extraRelease 'unexpected.txt'), 'extra', $utf8)
    Assert-Rejected { Test-QuestReleaseBundle -ReleaseDirectory $extraRelease -ExpectedTag ([string]$fixture.release_tag) | Out-Null } 'extra release asset'

    $unpinnedPath = Join-Path $temporary 'unpinned.json'
    [IO.File]::WriteAllText($unpinnedPath, (ConvertTo-QuestLockJson (New-UnpinnedQuestReleaseLock)), $utf8)
    $unpinned = Test-QuestReleaseLock -RepoRoot $fakeRepo -LockPath $unpinnedPath -CatalogPath $catalogPath
    Assert-Test ($unpinned.State -ceq 'unpinned' -and $unpinned.Assets -eq 0) 'Unpinned fixture made a false release claim.'
    $checks.Add('unpinned state makes no live-release claim: passed')

    [pscustomobject][ordered]@{
        schema = 'lumberjacks-quest-release-import-test/v1'
        verdict = 'passed'
        checks = @($checks)
    } | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemporary.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected test path '$resolvedTemporary'."
        }
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
