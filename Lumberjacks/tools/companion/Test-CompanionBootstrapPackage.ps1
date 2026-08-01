<#
.SYNOPSIS
Validate a Lumberjacks Companion bootstrap zip before publishing it.

.DESCRIPTION
Checks the generated bootstrap package for the runtime files and operator scripts
required by the Companion UI/API. In particular, every PowerShell script path
emitted by Game.Companion's Wave 0 command surface must exist in the package.

This catches packages that render correct commands but cannot run those commands
after a tester downloads and extracts the bootstrap.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'

$package = Resolve-Path $PackagePath
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ZipEntryText {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$EntryName
    )

    $entry = $Archive.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1
    if (-not $entry) { return $null }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Normalize-ZipPath {
    param([string]$Path)
    return ($Path -replace '/', '\').TrimStart('\')
}

$archive = [IO.Compression.ZipFile]::OpenRead($package.Path)
try {
    $entryNames = @($archive.Entries | ForEach-Object { Normalize-ZipPath $_.FullName })
    $entrySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $entryNames) { [void]$entrySet.Add($name) }

    $requiredStatic = @(
        'Directory.Build.props',
        'Directory.Packages.props',
        'src\Game.Companion\Program.cs',
        'src\Game.Companion\CompanionPage.cs',
        'src\Game.Companion\Dockerfile',
        'src\Game.Companion\Game.Companion.csproj',
        'tools\companion\docker-compose.yml',
        'tools\companion\docker-compose.valheim.yml.example',
        'tools\companion\Start-WorkbenchHostRunner.ps1',
        'tools\companion\bootstrap\Start-LumberjacksCompanion.cmd',
        'tools\companion\bootstrap\Start-LumberjacksCompanion.ps1',
        'tools\i5\Test-I5Link.ps1',
        'tools\workbench\Test-WorkbenchZipPrivacy.ps1',
        'tools\workbench\Test-WorkbenchSupportExport.ps1',
        'tools\workbench\Test-WorkbenchProfileBoundary.ps1',
        'tools\workbench\Test-WorkbenchMcpIdentity.ps1'
    )

    $program = Read-ZipEntryText $archive 'src\Game.Companion\Program.cs'
    if (-not $program) { throw 'src\Game.Companion\Program.cs missing from package' }

    $commandPaths = [regex]::Matches($program, 'tools[\\/][A-Za-z0-9_.\\/ -]+?\.ps1') |
        ForEach-Object { Normalize-ZipPath $_.Value } |
        Sort-Object -Unique

    $required = @($requiredStatic + $commandPaths) | Sort-Object -Unique
    $missing = @($required | Where-Object { -not $entrySet.Contains($_) })

    $result = [ordered]@{
        schema_version = 1
        package = $package.Path
        verdict = if ($missing.Count -eq 0) { 'companion_bootstrap_package_valid' } else { 'companion_bootstrap_package_invalid' }
        required_count = $required.Count
        command_path_count = @($commandPaths).Count
        missing = $missing
        checked_command_paths = @($commandPaths)
    }

    $result | ConvertTo-Json -Depth 6
    if ($missing.Count -gt 0) { throw 'Companion bootstrap package is missing required files.' }
} finally {
    $archive.Dispose()
}
