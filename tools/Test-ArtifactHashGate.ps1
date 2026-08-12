#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1') -DefineOnly
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null

$verifier = Join-Path $repoRoot 'infra\gcp\p7\scripts\Test-ModReleaseArtifact.ps1'
$fixtureRoot = Join-Path $repoRoot 'tests\fixtures\mod-release'
$fixture = [IO.File]::ReadAllText((Join-Path $fixtureRoot 'fixture.json')) | ConvertFrom-Json
if ([string]$fixture.schema -ne 'lumberjacks-mod-release-fixture/v1') {
    throw 'mod release fixture has an unexpected schema'
}

# The artifact verifier is deliberately data-only. Keep the prohibition mechanical
# so a later edit cannot quietly turn the no-deploy proof into an environment probe.
$verifierText = [IO.File]::ReadAllText($verifier)
$prohibitedInvocation = '(?im)(?:^|[;&|])\s*(?:&\s*)?(?:git|ssh|scp|docker|dotnet|msbuild)\b'
if ($verifierText -match $prohibitedInvocation -or
    $verifierText -match '(?im)\b(?:Start-Process|Invoke-Command|Invoke-Expression|New-PSSession|Enter-PSSession)\b') {
    throw 'mod artifact verifier contains a prohibited external or remote command'
}
$tokens = $null
$parseErrors = $null
$verifierAst = [Management.Automation.Language.Parser]::ParseFile(
    $verifier,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "mod artifact verifier has parser errors: $($parseErrors[0].Message)"
}
$prohibitedCommands = @(
    'git', 'ssh', 'scp', 'docker', 'dotnet', 'msbuild',
    'Start-Process', 'Invoke-Command', 'Invoke-Expression',
    'New-PSSession', 'Enter-PSSession', 'Invoke-WebRequest', 'Invoke-RestMethod'
)
$commandAsts = @($verifierAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst]
}, $true))
$dynamicInvocations = @($commandAsts | Where-Object {
    $_.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -or
    ([string]::IsNullOrWhiteSpace($_.GetCommandName()) -and
        $_.InvocationOperator -ne [Management.Automation.Language.TokenKind]::Dot)
})
$forbiddenCommandsFound = @($commandAsts | Where-Object {
    $_.GetCommandName() -in $prohibitedCommands
})
if ($dynamicInvocations.Count -gt 0 -or $forbiddenCommandsFound.Count -gt 0) {
    throw 'mod artifact verifier contains a dynamic or prohibited command invocation'
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-mod-release-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $dllPath = Join-Path $sandbox 'ComfyNetworkSense.dll'
    $source = [IO.File]::ReadAllText((Join-Path $fixtureRoot 'ComfyNetworkSense.fixture.cs'))
    Add-Type -TypeDefinition $source -Language CSharp -OutputAssembly $dllPath

    $releaseIdentityLib = Join-Path $repoRoot 'infra\gcp\p7\scripts\lib\ReleaseIdentity.ps1'
    . $releaseIdentityLib
    $managed = Get-ManagedAssemblyIdentity -DllPath $dllPath
    $dllHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $dllBytes = [long](Get-Item -LiteralPath $dllPath).Length

    $sourceFiles = @(
        [ordered]@{
            path = 'network/mod/ComfyNetworkSense/ComfyNetworkSense.cs'
            sha256 = '1111111111111111111111111111111111111111111111111111111111111111'
            bytes = 123
        }
    )
    $sourceComposite = @($sourceFiles | ForEach-Object {
        '{0} {1} {2}' -f $_.sha256, $_.bytes, $_.path
    }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $sourceHash = ([BitConverter]::ToString(
            $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sourceComposite + "`n"))
        )).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }

    $packages = @(
        [ordered]@{
            id = 'Comfy.Quest.Contracts'
            version = '0.1.0-local'
            source = 'packages-local'
            path = 'packages-local/Comfy.Quest.Contracts.0.1.0-local.nupkg'
            sha256 = '2222222222222222222222222222222222222222222222222222222222222222'
            bytes = 456
        },
        [ordered]@{
            id = 'Comfy.Transport.Contracts'
            version = '0.1.0-local'
            source = 'packages-local'
            path = 'packages-local/Comfy.Transport.Contracts.0.1.0-local.nupkg'
            sha256 = '3333333333333333333333333333333333333333333333333333333333333333'
            bytes = 789
        }
    )
    $compileInputScopes = [ordered]@{
        'BepInEx/core/0Harmony.dll' = 'bepinex'
        'BepInEx/core/BepInEx.dll' = 'bepinex'
        'valheim_Data/Managed/assembly_valheim.dll' = 'valheim'
        'valheim_Data/Managed/assembly_utils.dll' = 'valheim'
        'valheim_Data/Managed/System.Runtime.Serialization.dll' = 'valheim'
        'valheim_Data/Managed/UnityEngine.dll' = 'unity'
        'valheim_Data/Managed/UnityEngine.CoreModule.dll' = 'unity'
        'valheim_Data/Managed/UnityEngine.IMGUIModule.dll' = 'unity'
        'valheim_Data/Managed/UnityEngine.JSONSerializeModule.dll' = 'unity'
        'valheim_Data/Managed/UnityEngine.InputLegacyModule.dll' = 'unity'
        'valheim_Data/Managed/UnityEngine.PhysicsModule.dll' = 'unity'
        'valheim_Data/Managed/UnityEngine.TextRenderingModule.dll' = 'unity'
    }
    $compileInputs = @($compileInputScopes.Keys | ForEach-Object {
        [ordered]@{
            scope = $compileInputScopes[$_]
            path = $_
            sha256 = '4444444444444444444444444444444444444444444444444444444444444444'
            bytes = 100
        }
    })
    $configHash = '5555555555555555555555555555555555555555555555555555555555555555'
    $receipt = [ordered]@{
        schema = 'comfy-mod-boundary-receipt/v1'
        generated_utc = '2026-08-12T00:00:00Z'
        repository = 'djcdevelopment/networksense'
        tag = [string]$fixture.tag
        source_revision = [string]$fixture.source_revision
        source_clean = $true
        plugin_version = [string]$fixture.plugin_version
        baked_release_id = [string]$fixture.release_id
        dll = [ordered]@{
            name = 'ComfyNetworkSense.dll'
            sha256 = $dllHash
            bytes = $dllBytes
            assembly_version = $managed.AssemblyVersion
            file_version = $managed.FileVersion
            module_version_id = $managed.ModuleVersionId
        }
        source = [ordered]@{ sha256 = $sourceHash; files = $sourceFiles }
        build = [ordered]@{
            configuration = 'Release'
            dotnet_sdk = 'fixture'
            dependency_profile = [string]$fixture.dependency_profile
            copy_to_plugins_disabled = $true
            live_plugin_sha256_before = ''
            live_plugin_sha256_after = ''
            nuget_config = [ordered]@{
                path = 'nuget.interim.config'
                sha256 = $configHash
                bytes = 42
            }
            packages = $packages
            compile_inputs = $compileInputs
        }
        checks = [ordered]@{
            tag_matches_plugin_version = $true
            manifest_matches_plugin_version = $true
            assembly_matches_source = $true
            source_boundary_recorded = $true
            live_plugin_unchanged = $true
        }
        result = 'pass'
    }
    $receiptPath = Join-Path $sandbox 'boundary-receipt.json'
    [IO.File]::WriteAllText(
        $receiptPath,
        ($receipt | ConvertTo-Json -Depth 30) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $receiptHash = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $manifest = [ordered]@{
        schema = 'comfy-mod-release-manifest/v1'
        repository = 'djcdevelopment/networksense'
        tag = [string]$fixture.tag
        source_revision = [string]$fixture.source_revision
        plugin = [ordered]@{
            name = 'ComfyNetworkSense'
            guid = 'djcdevelopment.valheim.comfynetworksense'
            version = [string]$fixture.plugin_version
            baked_release_id = [string]$fixture.release_id
        }
        artifact = [ordered]@{
            name = 'ComfyNetworkSense.dll'
            sha256 = $dllHash
            bytes = $dllBytes
            assembly_version = $managed.AssemblyVersion
            file_version = $managed.FileVersion
        }
        boundary_receipt = [ordered]@{
            name = 'boundary-receipt.json'
            sha256 = $receiptHash
        }
        build = [ordered]@{
            configuration = 'Release'
            dotnet_sdk = 'fixture'
            dependency_profile = [string]$fixture.dependency_profile
            nuget_config = [ordered]@{ path = 'nuget.interim.config'; sha256 = $configHash }
        }
    }
    $manifestPath = Join-Path $sandbox 'release-manifest.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 30) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $sandbox 'SHA256SUMS'),
        (@(
            "$dllHash  ComfyNetworkSense.dll",
            "$manifestHash  release-manifest.json",
            "$receiptHash  boundary-receipt.json"
        ) -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))

    $verified = & $verifier `
        -ArtifactDirectory $sandbox `
        -ExpectedTag ([string]$fixture.tag) `
        -ExpectedSourceRevision ([string]$fixture.source_revision) `
        -ExpectedReleaseId ([string]$fixture.release_id) `
        -ExpectedDependencyProfile ([string]$fixture.dependency_profile)
    if ([string]$verified.Verdict -ne 'passed' -or [string]$verified.Sha256 -ne $dllHash) {
        throw 'G6 positive release artifact fixture did not produce the expected receipt'
    }

    $stream = [IO.File]::Open($dllPath, [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try { $stream.WriteByte(0x5a) }
    finally { $stream.Dispose() }

    $tamperRejected = $false
    try {
        & $verifier `
            -ArtifactDirectory $sandbox `
            -ExpectedTag ([string]$fixture.tag) `
            -ExpectedSourceRevision ([string]$fixture.source_revision) `
            -ExpectedReleaseId ([string]$fixture.release_id) `
            -ExpectedDependencyProfile ([string]$fixture.dependency_profile) | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch [regex]::Escape([string]$fixture.expected_tamper_error)) { throw }
        $tamperRejected = $true
    }
    if (-not $tamperRejected) { throw 'G6 tampered DLL negative fixture unexpectedly passed' }

    [pscustomobject]@{
        Schema = 'lumberjacks-boundary-guard/v2'
        Guard = 'G6'
        PositiveFixture = 'synthetic comfy-mod-release-manifest/v1 bundle'
        NegativeFixture = 'same bundle with one byte appended to ComfyNetworkSense.dll'
        ArtifactSha256 = $dllHash
        TamperExecuted = $true
        TamperRejected = $true
        VerifierPure = $true
        Verdict = 'passed'
    } | ConvertTo-Json -Compress
}
finally {
    $sandboxFull = [IO.Path]::GetFullPath($sandbox)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($sandboxFull.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $sandboxFull)) {
        Remove-Item -LiteralPath $sandboxFull -Recurse -Force
    }
}
