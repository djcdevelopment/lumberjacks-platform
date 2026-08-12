[CmdletBinding()]
param(
    [string] $DotNet = 'dotnet',
    [string] $Version = '0.1.0',
    [string] $RepositoryCommit = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($RepositoryCommit)) {
    $RepositoryCommit = [string](& git -C $repoRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the repository commit.' }
    $RepositoryCommit = $RepositoryCommit.Trim()
}
if ($RepositoryCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'RepositoryCommit must be a full 40-hex SHA.'
}

$workflow = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\publish-nuget.yml') -Raw
foreach ($required in @(
    'nuget-v*',
    'nuget-v0.1.0',
    'merge-base --is-ancestor',
    'assert-absent',
    'validate_transport_nupkg.py',
    'RepositoryCommit=',
    'nuget_availability.py wait'
)) {
    if (-not $workflow.Contains($required)) { throw "Publish workflow is missing '$required'." }
}
if ($workflow -match '(?i)skip-duplicate') {
    throw 'Publish workflow must not use blind duplicate skipping.'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('transport-nuget-' + [guid]::NewGuid().ToString('N'))
$packageName = "Comfy.Transport.Contracts.$Version.nupkg"
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    & $DotNet pack (Join-Path $repoRoot 'Lumberjacks\src\Comfy.Transport.Contracts\Comfy.Transport.Contracts.csproj') `
        -c Release `
        -o $temporary `
        "-p:Version=$Version" `
        "-p:RepositoryCommit=$RepositoryCommit" `
        '-p:ContinuousIntegrationBuild=true'
    if ($LASTEXITCODE -ne 0) { throw 'Transport package rehearsal failed.' }

    $package = Join-Path $temporary $packageName
    & python (Join-Path $PSScriptRoot 'validate_transport_nupkg.py') `
        --package $package `
        --version $Version `
        --commit $RepositoryCommit `
        --source-root (Join-Path $repoRoot 'Lumberjacks\src\Comfy.Transport.Contracts')
    if ($LASTEXITCODE -ne 0) { throw 'Transport package validation failed.' }

    $tamperRoot = Join-Path $temporary 'tampered'
    New-Item -ItemType Directory -Path $tamperRoot | Out-Null
    $tampered = Join-Path $tamperRoot $packageName
    Copy-Item -LiteralPath $package -Destination $tampered
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($tampered, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.CreateEntry('unexpected-payload.txt')
        $writer = New-Object IO.StreamWriter($entry.Open())
        try { $writer.Write('tamper') } finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }

    $ErrorActionPreference = 'Continue'
    $tamperOutput = @(& python (Join-Path $PSScriptRoot 'validate_transport_nupkg.py') `
        --package $tampered `
        --version $Version `
        --commit $RepositoryCommit `
        --source-root (Join-Path $repoRoot 'Lumberjacks\src\Comfy.Transport.Contracts') 2>&1)
    $tamperExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($tamperExit -eq 0 -or ($tamperOutput -join "`n") -notmatch 'unexpected entries') {
        throw "Payload tamper was not rejected as expected (exit=$tamperExit)."
    }
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}

[pscustomobject]@{
    Schema = 'lumberjacks-transport-nuget-readiness/v1'
    Package = $packageName
    RepositoryCommit = $RepositoryCommit.ToLowerInvariant()
    PayloadTamperRejected = $true
    BlindDuplicateSkip = $false
    Verdict = 'passed'
} | ConvertTo-Json -Compress

# The expected tamper child failure leaves LASTEXITCODE non-zero.
exit 0
