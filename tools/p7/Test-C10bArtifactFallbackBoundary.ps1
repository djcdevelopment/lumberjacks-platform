#Requires -Version 5.1
<#
.SYNOPSIS
Prove the C10b migration-candidate or final no-fallback mod boundary.

.DESCRIPTION
The candidate gate freezes the exact native-cutover escape-hatch inventory so
it cannot drift before the first P7 proof. The final gate reverses the
polarity: every migration control and restoration branch marker must be absent
from both production source and the compiled DLL, while the native-use ledger
and its mapped funnel patches must remain present.

This tool is read-only unless OutputPath is supplied. It never contacts P7,
starts a game, deploys an artifact, or changes a runtime configuration.
#>
[CmdletBinding()]
param(
    [ValidateSet('candidate', 'final')]
    [string] $Stage = 'candidate',

    [string] $SourceRoot = '',

    [string] $DllPath = '',

    [string] $ExpectedReleaseId = 'm7-c10a-20260802-r41',

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $repoRoot 'network\mod\ComfyNetworkSense'
}
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $SourceRoot 'bin\Release\ComfyNetworkSense.dll'
}
if ([string]::IsNullOrWhiteSpace($ExpectedReleaseId) -or
    $ExpectedReleaseId.Length -gt 80 -or
    $ExpectedReleaseId -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'ExpectedReleaseId must be an 80-character-or-shorter safe token.'
}

$sourceDirectory =
    (Resolve-Path -LiteralPath $SourceRoot -ErrorAction Stop).Path.TrimEnd('\', '/')
$dll = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path

function Get-ExactSetMatch([string[]] $Actual, [string[]] $Expected) {
    [string[]] $actualSet = @($Actual | Sort-Object -Unique)
    [string[]] $expectedSet = @($Expected | Sort-Object -Unique)
    if ($actualSet.Count -ne $expectedSet.Count) { return $false }
    for ($index = 0; $index -lt $actualSet.Count; $index++) {
        if ($actualSet[$index] -cne $expectedSet[$index]) { return $false }
    }
    return $true
}

function Get-NamedMatches([string] $Text, [string] $Pattern) {
    return @(
        [regex]::Matches($Text, $Pattern) |
            ForEach-Object { $_.Groups['name'].Value } |
            Sort-Object -Unique
    )
}

function Test-Marker([string] $Text, [string] $Marker) {
    return $Text.IndexOf($Marker, [StringComparison]::Ordinal) -ge 0
}

function Write-Receipt([object] $Receipt) {
    $json = ($Receipt | ConvertTo-Json -Depth 12) + [Environment]::NewLine
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
        $outputDirectory = Split-Path -Parent $absoluteOutput
        if ($outputDirectory) {
            New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
        }
        [IO.File]::WriteAllText(
            $absoluteOutput,
            $json,
            [Text.UTF8Encoding]::new($false))
    }
    $json.TrimEnd()
}

$sourceFiles = @(
    Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File -Filter '*.cs' |
        Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj|Tests)[\\/]'
        } |
        Sort-Object FullName
)
if ($sourceFiles.Count -eq 0) {
    throw "No production C# source files found under $sourceDirectory."
}
$sourceText = @(
    $sourceFiles | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8
    }
) -join "`n"

$configPath = Join-Path $sourceDirectory 'Config\PluginConfig.cs'
$runtimePath = Join-Path $sourceDirectory 'ComfyNetworkSense.cs'
$requestPath = Join-Path $sourceDirectory `
    'Core\Services\NativeAutotestRequest.cs'
$requiredSourcePaths = @($configPath, $runtimePath, $requestPath)
$requiredSourceFilesPresent =
    @($requiredSourcePaths | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0

$configText = if (Test-Path -LiteralPath $configPath) {
    Get-Content -LiteralPath $configPath -Raw -Encoding utf8
} else { '' }
$runtimeText = if (Test-Path -LiteralPath $runtimePath) {
    Get-Content -LiteralPath $runtimePath -Raw -Encoding utf8
} else { '' }
$requestText = if (Test-Path -LiteralPath $requestPath) {
    Get-Content -LiteralPath $requestPath -Raw -Encoding utf8
} else { '' }

$candidateConfigControls = @(
    'NativeNetworkLedgerEnabled'
    'NativeNetworkPoisonEnabled'
    'DirectControlCutoverEnabled'
    'RoutedRpcCutoverEnabled'
    'ZdoJournalCutoverEnabled'
    'ZdoJournalCanonicalSessionEnabled'
    'OwnershipLeaseCutoverEnabled'
    'WorldZoneCutoverEnabled'
    'MotionAuthorityCutoverEnabled'
    'SocketQuarantineCutoverEnabled'
    'LogicalPeerCutoverEnabled'
)
$candidateRuntimeControls = @(
    'nativeNetworkPoisonEnabled'
    'directControlCutoverEnabled'
    'routedRpcCutoverEnabled'
    'zdoJournalCutoverEnabled'
    'zdoJournalCanonicalSessionEnabled'
    'ownershipLeaseCutoverEnabled'
    'worldZoneCutoverEnabled'
    'motionAuthorityCutoverEnabled'
    'logicalPeerCutoverEnabled'
)
$candidateRequestControls = @(
    'native_network_poison'
    'routed_rpc_cutover'
    'zdo_journal_cutover'
    'zdo_journal_canonical_session'
    'ownership_lease_cutover'
    'world_zone_cutover'
    'motion_authority_cutover'
    'socket_quarantine_cutover'
)
$candidateActiveControls = @(
    'ActiveRoutedRpcCutover'
    'ActiveZdoJournalCutover'
    'ActiveZdoJournalCanonicalSession'
    'ActiveOwnershipLeaseCutover'
    'ActiveWorldZoneCutover'
    'ActiveMotionAuthorityCutover'
    'ActiveSocketQuarantineCutover'
)

$allBooleanConfigNames = Get-NamedMatches $configText `
    'public\s+static\s+ConfigEntry<bool>\s+(?<name>[A-Za-z0-9_]+)\s*\{'
$observedConfigControls = @(
    $allBooleanConfigNames | Where-Object {
        $_ -match 'CutoverEnabled$' -or
        $_ -match '^NativeNetwork.*Enabled$' -or
        $_ -eq 'ZdoJournalCanonicalSessionEnabled'
    }
)
$allRuntimeKeys = Get-NamedMatches $runtimeText `
    'case\s+"(?<name>[^"]+)"\s*:'
$observedRuntimeControls = @(
    $allRuntimeKeys | Where-Object {
        ($_ -match 'cutover' -and $_ -ne 'cutoverResidueCleanup') -or
        $_ -match '^nativeNetwork(?:Ledger|Poison)' -or
        $_ -eq 'zdoJournalCanonicalSessionEnabled'
    }
)
$allRequestBooleanFields = Get-NamedMatches $requestText `
    'public\s+bool\s+(?<name>[a-z0-9_]+)\s*;'
$observedRequestControls = @(
    $allRequestBooleanFields | Where-Object {
        $_ -match 'cutover' -or
        $_ -eq 'native_network_poison' -or
        $_ -eq 'zdo_journal_canonical_session'
    }
)
$allActiveBooleanProperties = Get-NamedMatches $requestText `
    'public\s+static\s+bool\s+(?<name>Active[A-Za-z0-9_]+)\s*=>'
$observedActiveControls = @(
    $allActiveBooleanProperties | Where-Object {
        $_ -match 'Cutover$' -or
        $_ -eq 'ActiveZdoJournalCanonicalSession'
    }
)

$migrationMarkers = @(
    $candidateConfigControls
    $candidateRuntimeControls
    $candidateRequestControls
    $candidateActiveControls
    'SetPoisonOverride'
    'SocketQuarantineCutoverRunner'
    'native_direct_pulse_generator_stopped'
    'selected_routed_rpc_native_path_restored'
    'zdo_semantics_restored_to_http_lab_seam'
    'selected_ownership_native_path_restored'
    'native_world_and_zone_paths_restored'
    'canonical_motion_authority_disarmed'
    'logical_peer_adapter_disarmed'
) | Sort-Object -Unique
$retainedGuardMarkers = @(
    'NativeNetworkLedger'
    'NativeNetworkEvidenceRunId'
    'NativeSteamSocketPatches'
    'NativeHandshakeLedgerPatches'
    'NativeZdoLedgerPatches'
    'NativeRoutedRpcLedgerPatches'
    'native-network-use.jsonl'
    'native_total'
    'poison_trips'
)

$dllBytes = [IO.File]::ReadAllBytes($dll)
$dllAscii = [Text.Encoding]::UTF8.GetString($dllBytes)
$evenLength = $dllBytes.Length - ($dllBytes.Length % 2)
$dllUnicodeEven = [Text.Encoding]::Unicode.GetString($dllBytes, 0, $evenLength)
$oddLength = $dllBytes.Length - 1
if (($oddLength % 2) -ne 0) { $oddLength-- }
$dllUnicodeOdd = if ($oddLength -gt 0) {
    [Text.Encoding]::Unicode.GetString($dllBytes, 1, $oddLength)
} else { '' }
function Test-DllMarker([string] $Marker) {
    return (Test-Marker $dllAscii $Marker) -or
        (Test-Marker $dllUnicodeEven $Marker) -or
        (Test-Marker $dllUnicodeOdd $Marker)
}

$releaseIdentityLibrary = Join-Path $repoRoot `
    'infra\gcp\p7\scripts\lib\ReleaseIdentity.ps1'
. $releaseIdentityLibrary
$artifactRelease = Get-AssemblyMetadataValue `
    -DllPath $dll `
    -Key 'LumberjacksModReleaseId'
$sourceReleaseMatch = [regex]::Match(
    $runtimeText,
    'public\s+const\s+string\s+ReleaseId\s*=\s*"(?<release>[^"]+)"\s*;')
$sourceRelease = if ($sourceReleaseMatch.Success) {
    $sourceReleaseMatch.Groups['release'].Value
} else { '' }

$expectedConfigControls = if ($Stage -eq 'candidate') {
    $candidateConfigControls
} else { @() }
$expectedRuntimeControls = if ($Stage -eq 'candidate') {
    $candidateRuntimeControls
} else { @() }
$expectedRequestControls = if ($Stage -eq 'candidate') {
    $candidateRequestControls
} else { @() }
$expectedActiveControls = if ($Stage -eq 'candidate') {
    $candidateActiveControls
} else { @() }

$migrationEvidence = @(
    foreach ($marker in $migrationMarkers) {
        $sourcePresent = Test-Marker $sourceText $marker
        $dllPresent = Test-DllMarker $marker
        [ordered]@{
            marker = $marker
            source_present = $sourcePresent
            dll_present = $dllPresent
            expected_present = $Stage -eq 'candidate'
            passed = if ($Stage -eq 'candidate') {
                $sourcePresent -and $dllPresent
            } else {
                -not $sourcePresent -and -not $dllPresent
            }
        }
    }
)
$retainedEvidence = @(
    foreach ($marker in $retainedGuardMarkers) {
        $sourcePresent = Test-Marker $sourceText $marker
        $dllPresent = Test-DllMarker $marker
        [ordered]@{
            marker = $marker
            source_present = $sourcePresent
            dll_present = $dllPresent
            passed = $sourcePresent -and $dllPresent
        }
    }
)

$checks = [ordered]@{
    required_source_files_present = $requiredSourceFilesPresent
    source_release_exact = $sourceRelease -eq $ExpectedReleaseId
    dll_release_exact = $artifactRelease -eq $ExpectedReleaseId
    source_and_dll_release_aligned = $sourceRelease -eq $artifactRelease
    config_control_inventory_exact =
        Get-ExactSetMatch $observedConfigControls $expectedConfigControls
    runtime_control_inventory_exact =
        Get-ExactSetMatch $observedRuntimeControls $expectedRuntimeControls
    request_control_inventory_exact =
        Get-ExactSetMatch $observedRequestControls $expectedRequestControls
    active_control_inventory_exact =
        Get-ExactSetMatch $observedActiveControls $expectedActiveControls
    migration_marker_polarity_exact =
        @($migrationEvidence | Where-Object { -not $_.passed }).Count -eq 0
    retained_native_guard_complete =
        @($retainedEvidence | Where-Object { -not $_.passed }).Count -eq 0
}
$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'c10b_artifact_fallback_boundary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    stage = $Stage
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    expected_release_id = $ExpectedReleaseId
    source_release_id = $sourceRelease
    dll_release_id = $artifactRelease
    dll_sha256 =
        (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    source_root = $sourceDirectory
    dll_path = $dll
    production_source_file_count = $sourceFiles.Count
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    inventory = [ordered]@{
        config_controls = @($observedConfigControls)
        runtime_controls = @($observedRuntimeControls)
        request_controls = @($observedRequestControls)
        active_request_controls = @($observedActiveControls)
    }
    migration_markers = $migrationEvidence
    retained_native_guard_markers = $retainedEvidence
}

Write-Receipt $receipt
if ($failed.Count -gt 0) { exit 4 }
exit 0
