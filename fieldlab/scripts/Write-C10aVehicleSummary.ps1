#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one C10a ship run into a fail-closed physical acceptance receipt.

.DESCRIPTION
Correlates the OMEN, i5, and AM4 ship streams. A pass requires both real
drive/observe legs, both helm releases, an authenticated owner transfer whose
canonical helm user is already clear, application of that atomic handoff on
both clients, server snapshot progression under both owners, non-owner replica
application, native-zero ledgers, exact paired Gateway provenance, and clean
composition teardown.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunDirectory,

    [Parameter(Mandatory)]
    [string] $RunId,

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RunDirectory -ErrorAction Stop).Path

function Read-JsonLines([string] $RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return @() }
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $path -Encoding utf8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ([string]$row.run_id -eq $RunId) { $rows += $row }
        } catch { }
    }
    @($rows)
}

function Read-JsonFile([string] $RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json
}

function Detail-Long([object] $Row, [string] $Name) {
    if ($null -eq $Row) { return $null }
    $match = [regex]::Match(
        [string]$Row.detail,
        '(?:^| )' + [regex]::Escape($Name) + '=(-?[0-9]+)(?: |$)')
    if (-not $match.Success) { return $null }
    [long]$match.Groups[1].Value
}

function Detail-Double([object] $Row, [string] $Name) {
    if ($null -eq $Row) { return $null }
    $match = [regex]::Match(
        [string]$Row.detail,
        '(?:^| )' + [regex]::Escape($Name) +
            '=(-?[0-9]+(?:\.[0-9]+)?)(?: |$)')
    if (-not $match.Success) { return $null }
    [double]::Parse(
        $match.Groups[1].Value,
        [Globalization.CultureInfo]::InvariantCulture)
}

function Detail-Bool([object] $Row, [string] $Name) {
    if ($null -eq $Row) { return $null }
    $match = [regex]::Match(
        [string]$Row.detail,
        '(?:^| )' + [regex]::Escape($Name) + '=(true|false)(?: |$)',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) { return $null }
    [bool]::Parse($match.Groups[1].Value)
}

function Find-State(
    [object[]] $Rows,
    [string] $State,
    [string] $ActionId = '') {
    @($Rows | Where-Object {
        [string]$_.state -eq $State -and
        ([string]::IsNullOrWhiteSpace($ActionId) -or
         [string]$_.action_id -eq $ActionId)
    } | Select-Object -First 1)[0]
}

function Passed-Action([object[]] $Rows, [string] $ActionId) {
    $null -ne (Find-State $Rows 'probe_passed' $ActionId)
}

function Probe-Metrics([object[]] $Rows, [string] $ActionId) {
    $row = Find-State $Rows 'probe_passed' $ActionId
    [ordered]@{
        action_id = $ActionId
        distance = Detail-Double $row 'distance'
        rudder = Detail-Double $row 'rudder'
        speed_changed = Detail-Bool $row 'speed_changed'
        control_granted = Detail-Bool $row 'control_granted'
        remote_owner = Detail-Bool $row 'remote_owner'
        local_owner = Detail-Bool $row 'local_owner'
    }
}

function Drive-Passed([object] $Metrics) {
    $null -ne $Metrics.distance -and $Metrics.distance -ge 3.0 -and
    $null -ne $Metrics.rudder -and [Math]::Abs($Metrics.rudder) -ge 0.02 -and
    $Metrics.speed_changed -eq $true -and
    $Metrics.control_granted -eq $true -and
    $Metrics.remote_owner -eq $true
}

function Observe-Passed([object] $Metrics) {
    $null -ne $Metrics.distance -and $Metrics.distance -ge 3.0 -and
    $null -ne $Metrics.rudder -and [Math]::Abs($Metrics.rudder) -ge 0.02 -and
    $Metrics.speed_changed -eq $true -and
    $Metrics.control_granted -eq $false -and
    $Metrics.local_owner -eq $true
}

function Native-Ledger-Clean([object[]] $Rows) {
    $summaries = @($Rows | Where-Object { [string]$_.event -eq 'summary' })
    if ($summaries.Count -eq 0) { return $false }
    @($summaries | Where-Object {
        [long]$_.native_total -ne 0 -or
        [long]$_.poison_trips -ne 0 -or
        [long]$_.writer_dropped_rows -ne 0 -or
        [long]$_.writer_faults -ne 0
    }).Count -eq 0
}

function Snapshot-Sequences([object[]] $Rows, [long] $Owner) {
    @($Rows | Where-Object {
        [string]$_.state -eq 'snapshot_applied' -and
        (Detail-Long $_ 'sender') -eq $Owner
    } | ForEach-Object { Detail-Long $_ 'sequence' } |
        Where-Object { $null -ne $_ } | Sort-Object -Unique)
}

$omen = Read-JsonLines 'omen\ship-cutover.jsonl'
$i5 = Read-JsonLines 'i5\ship-cutover.jsonl'
$server = Read-JsonLines 'server\ship-cutover.jsonl'
$omenNative = Read-JsonLines 'omen\native-network-use.jsonl'
$i5Native = Read-JsonLines 'i5\native-network-use.jsonl'
$serverNative = Read-JsonLines 'server\native-network-use.jsonl'
$coverage = Read-JsonFile 'c10a-vehicle-scenario-coverage.json'
$composition = Read-JsonFile 'composition.json'
$provenance = Read-JsonFile 'gateway-image-provenance.json'
$deploy = Read-JsonFile 'am4-deploy.json'
$cleanup = Read-JsonFile 'residue-cleanup.json'

$spawn = Find-State $server 'ship_spawned' 'omen-c10a-ship-spawn'
$initialOwner = Detail-Long $spawn 'owner'
$transfer = Find-State $server 'ship_owner_transferred' 'omen-c10a-ship-transfer-i5'
$transferredOwner = Detail-Long $transfer 'new_owner'
$canonicalHelmUser = Detail-Long $transfer 'canonical_helm_user'
$omenHandoff = Find-State $omen 'ship_owner_applied' 'omen-c10a-ship-transfer-i5'
$i5Handoff = Find-State $i5 'ship_owner_applied' 'omen-c10a-ship-transfer-i5'

$i5Drive = [pscustomobject](Probe-Metrics $i5 'i5-c10a-ship-drive-omen')
$omenObserve = [pscustomobject](Probe-Metrics $omen 'omen-c10a-ship-observe-i5')
$omenDrive = [pscustomobject](Probe-Metrics $omen 'omen-c10a-ship-drive-i5')
$i5Observe = [pscustomobject](Probe-Metrics $i5 'i5-c10a-ship-observe-omen')

$requiredOmenActions = @(
    'omen-c10a-ship-spawn',
    'omen-c10a-ship-board',
    'omen-c10a-ship-observe-i5',
    'omen-c10a-ship-release-i5',
    'omen-c10a-ship-transfer-i5',
    'omen-c10a-ship-drive-i5')
$requiredI5Actions = @(
    'i5-c10a-ship-wait',
    'i5-c10a-ship-board',
    'i5-c10a-ship-drive-omen',
    'i5-c10a-ship-owner',
    'i5-c10a-ship-observe-omen',
    'i5-c10a-ship-release-omen')

$initialOwnerSequences = Snapshot-Sequences $server $initialOwner
$transferredOwnerSequences = Snapshot-Sequences $server $transferredOwner
$i5InitialReplica = @($i5 | Where-Object {
    [string]$_.state -eq 'snapshot_replica_applied' -and
    (Detail-Long $_ 'owner') -eq $initialOwner
}).Count
$omenTransferredReplica = @($omen | Where-Object {
    [string]$_.state -eq 'snapshot_replica_applied' -and
    (Detail-Long $_ 'owner') -eq $transferredOwner
}).Count
$cleanupEffect = if ($cleanup -and $cleanup.receipt) {
    [string]$cleanup.receipt.effect
} else { '' }
$allShipRows = @($omen) + @($i5) + @($server)

$checks = [ordered]@{
    all_three_ship_streams_present =
        $omen.Count -gt 0 -and $i5.Count -gt 0 -and $server.Count -gt 0
    no_probe_snapshot_or_transfer_failures = @($allShipRows | Where-Object {
        [string]$_.state -in @(
            'probe_failed', 'snapshot_rejected', 'ship_transfer_rejected')
    }).Count -eq 0
    scenario_choreography_passed =
        $coverage -and [string]$coverage.result -eq 'passed' -and
        @($coverage.checks.PSObject.Properties | Where-Object {
            -not [bool]$_.Value
        }).Count -eq 0
    exact_spawn_and_owner_pair =
        $null -ne $spawn -and $initialOwner -gt 0 -and
        $transferredOwner -gt 0 -and $transferredOwner -ne $initialOwner
    all_required_client_actions_passed =
        @($requiredOmenActions | Where-Object {
            -not (Passed-Action $omen $_)
        }).Count -eq 0 -and
        @($requiredI5Actions | Where-Object {
            -not (Passed-Action $i5 $_)
        }).Count -eq 0
    first_i5_drive_passed = Drive-Passed $i5Drive
    first_omen_observer_passed = Observe-Passed $omenObserve
    reverse_omen_drive_passed = Drive-Passed $omenDrive
    reverse_i5_observer_passed = Observe-Passed $i5Observe
    both_helm_releases_observed =
        (Passed-Action $omen 'omen-c10a-ship-release-i5') -and
        (Passed-Action $i5 'i5-c10a-ship-release-omen')
    canonical_release_preceded_owner_handoff =
        $null -ne $transfer -and
        (Detail-Long $transfer 'old_owner') -eq $initialOwner -and
        $transferredOwner -gt 0 -and $canonicalHelmUser -eq 0
    atomic_release_handoff_applied_on_both_clients =
        $null -ne $omenHandoff -and $null -ne $i5Handoff -and
        (Detail-Long $omenHandoff 'new_owner') -eq $transferredOwner -and
        (Detail-Long $i5Handoff 'new_owner') -eq $transferredOwner -and
        (Detail-Long $omenHandoff 'canonical_helm_user') -eq 0 -and
        (Detail-Long $i5Handoff 'canonical_helm_user') -eq 0
    server_snapshots_cover_both_owners =
        1 -in $initialOwnerSequences -and 25 -in $initialOwnerSequences -and
        1 -in $transferredOwnerSequences -and
        25 -in $transferredOwnerSequences
    nonowner_replica_snapshots_cover_both_owners =
        $i5InitialReplica -gt 0 -and $omenTransferredReplica -gt 0
    native_zero_ledgers_clean =
        (Native-Ledger-Clean $omenNative) -and
        (Native-Ledger-Clean $i5Native) -and
        (Native-Ledger-Clean $serverNative)
    exact_paired_gateway_image =
        $provenance -and [bool]$provenance.exact_image_match -and
        [string]$provenance.result -eq 'passed' -and
        [string]$provenance.mod_release -eq [string]$composition.mod_release
    exact_am4_deploy_ready =
        $deploy -and [string]$deploy.result -eq 'passed' -and
        [bool]$deploy.plugin_loaded -and [bool]$deploy.server_ready -and
        [string]$deploy.local_sha256 -eq [string]$deploy.host_sha256 -and
        [string]$deploy.local_sha256 -eq [string]$deploy.container_sha256
    composition_completed_and_resumed =
        $composition -and [string]$composition.result -eq 'completed' -and
        @($composition.clients).Count -eq 2 -and
        @($composition.clients | Where-Object {
            [string]$_.result -ne 'joined_held_and_stopped' -or
            [long]$_.resume_count -ne 1 -or
            [string]$_.scenario_terminal.state -ne 'scenario_complete'
        }).Count -eq 0
    no_tagged_ship_residue =
        $cleanup -and -not $cleanup.cleanup_error -and
        $cleanupEffect -match '(?:^| )matched=0(?: |$)' -and
        $cleanupEffect -match '(?:^| )destroyed=0(?: |$)' -and
        $cleanupEffect -match '(?:^| )vehicle=0(?: |$)'
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_vehicle_physical_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    peers = [ordered]@{
        initial_owner = $initialOwner
        transferred_owner = $transferredOwner
    }
    handoff = [ordered]@{
        action_id = 'omen-c10a-ship-transfer-i5'
        canonical_helm_user = $canonicalHelmUser
        applied_on_omen = $null -ne $omenHandoff
        applied_on_i5 = $null -ne $i5Handoff
    }
    probe_metrics = @($i5Drive, $omenObserve, $omenDrive, $i5Observe)
    server_snapshot_sequences = [ordered]@{
        initial_owner = $initialOwnerSequences
        transferred_owner = $transferredOwnerSequences
    }
    cleanup_effect = $cleanupEffect
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'c10a-vehicle-summary.json'
}
$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.File]::WriteAllText(
    $absoluteOutput,
    ($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 12
if ($failed.Count -gt 0) { exit 1 }
