#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one C10a mount run into a fail-closed physical acceptance receipt.

.DESCRIPTION
Correlates the OMEN, i5, and AM4 saddle streams. A pass requires the exact
owner/epoch choreography, both rendered drive/observe legs, native release,
disconnect reclaim to the known live peer, stale transfer and rider-edge
falsifiers, canonical snapshot progression, native-zero ledgers, exact paired
Gateway provenance, and destruction of the one tagged Lox.
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
            '=(-?(?:[0-9]+(?:\.[0-9]+)?|Infinity|NaN))(?: |$)')
    if (-not $match.Success) { return $null }
    [double]::Parse(
        $match.Groups[1].Value,
        [Globalization.CultureInfo]::InvariantCulture)
}

function Detail-String([object] $Row, [string] $Name) {
    if ($null -eq $Row) { return $null }
    $match = [regex]::Match(
        [string]$Row.detail,
        '(?:^| )' + [regex]::Escape($Name) + '=([^ ]+)(?: |$)')
    if (-not $match.Success) { return $null }
    [string]$match.Groups[1].Value
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
        heading = Detail-Double $row 'heading'
        controls = Detail-Long $row 'controls'
        nonzero_controls = Detail-Long $row 'nonzero_controls'
        riding_ticks = Detail-Long $row 'riding_ticks'
        epoch = Detail-Long $row 'epoch'
        snapshot_advance = Detail-Long $row 'snapshot_advance'
        attachment_samples = Detail-Long $row 'attachment_samples'
        attachment_p95 = Detail-Double $row 'attachment_p95'
        attachment_max = Detail-Double $row 'attachment_max'
    }
}

function Drive-Passed([object] $Metrics) {
    $null -ne $Metrics.distance -and $Metrics.distance -ge 3.0 -and
    $null -ne $Metrics.heading -and $Metrics.heading -ge 15.0 -and
    $null -ne $Metrics.controls -and $Metrics.controls -gt 0 -and
    $null -ne $Metrics.nonzero_controls -and
        $Metrics.nonzero_controls -gt 0 -and
    $null -ne $Metrics.riding_ticks -and $Metrics.riding_ticks -gt 0 -and
    $null -ne $Metrics.snapshot_advance -and
        $Metrics.snapshot_advance -ge 15
}

function Observe-Passed([object] $Metrics) {
    $null -ne $Metrics.distance -and $Metrics.distance -ge 3.0 -and
    $null -ne $Metrics.heading -and $Metrics.heading -ge 15.0 -and
    $null -ne $Metrics.snapshot_advance -and
        $Metrics.snapshot_advance -ge 15 -and
    $null -ne $Metrics.attachment_samples -and
        $Metrics.attachment_samples -ge 30 -and
    $null -ne $Metrics.attachment_p95 -and
        $Metrics.attachment_p95 -le 0.10 -and
    $null -ne $Metrics.attachment_max -and
        $Metrics.attachment_max -le 0.25
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

$omen = Read-JsonLines 'omen\saddle-cutover.jsonl'
$i5 = Read-JsonLines 'i5\saddle-cutover.jsonl'
$server = Read-JsonLines 'server\saddle-cutover.jsonl'
$omenNative = Read-JsonLines 'omen\native-network-use.jsonl'
$i5Native = Read-JsonLines 'i5\native-network-use.jsonl'
$serverNative = Read-JsonLines 'server\native-network-use.jsonl'
$composition = Read-JsonFile 'composition.json'
$provenance = Read-JsonFile 'gateway-image-provenance.json'
$deploy = Read-JsonFile 'am4-deploy.json'
$cleanup = Read-JsonFile 'residue-cleanup.json'
$scenario = Read-JsonFile 'scenario.json'
$omenLifecycle = Read-JsonFile 'omen\lifecycle.json'
$i5Lifecycle = Read-JsonFile 'i5\lifecycle.json'
$isRelevance = [string]$scenario.profile -eq 'c10a-vehicle-relevance'

$spawn = Find-State $server 'saddle_spawned' 'omen-c10a-mount-spawn'
$spawnUid = Detail-String $spawn 'uid'
$omenPeer = Detail-Long $spawn 'owner'
$transfers = @($server |
    Where-Object {
        [string]$_.state -eq 'saddle_owner_transferred' -and
        (Detail-String $_ 'uid') -eq $spawnUid
    } |
    Sort-Object { [DateTimeOffset]$_.timestamp_utc })
$transferFacts = @($transfers | ForEach-Object {
    [pscustomobject]@{
        action_id = [string]$_.action_id
        old_owner = Detail-Long $_ 'old_owner'
        new_owner = Detail-Long $_ 'new_owner'
        user = Detail-Long $_ 'user'
        epoch = Detail-Long $_ 'epoch'
    }
})
$i5Peer = if ($transferFacts.Count -gt 0) {
    $transferFacts[0].new_owner
} else { $null }
$serverPeer = if ($isRelevance -and $transferFacts.Count -gt 3) {
    $transferFacts[3].new_owner
} else { $null }
$reclaim = @($server | Where-Object {
    [string]$_.state -eq 'saddle_disconnect_reclaimed' -and
    (Detail-String $_ 'uid') -eq $spawnUid -and
    (Detail-Long $_ 'departed') -eq $omenPeer -and
    (Detail-Long $_ 'fallback_owner') -eq $i5Peer -and
    (Detail-Long $_ 'epoch') -eq 4
} | Select-Object -First 1)[0]

$i5Drive = [pscustomobject](Probe-Metrics $i5 'i5-c10a-mount-drive-omen')
$omenObserve = [pscustomobject](Probe-Metrics $omen 'omen-c10a-mount-observe-i5')
$omenDrive = [pscustomobject](Probe-Metrics $omen 'omen-c10a-mount-drive-i5')
$i5Observe = [pscustomobject](Probe-Metrics $i5 'i5-c10a-mount-observe-omen')

$requiredOmenActions = @(
    'omen-c10a-mount-spawn',
    'omen-c10a-mount-wait',
    'omen-c10a-mount-rendezvous',
    'omen-c10a-mount-observe-i5',
    'omen-c10a-mount-first-release',
    'omen-c10a-mount-disconnect-reclaim',
    'omen-c10a-mount-drive-i5')
$requiredI5Actions = @(
    'i5-c10a-mount-wait',
    'i5-c10a-mount-rendezvous',
    'i5-c10a-mount-drive-omen',
    'i5-c10a-mount-observe-reclaim',
    'i5-c10a-mount-observe-omen',
    'i5-c10a-mount-second-release')
if ($isRelevance) {
    $requiredOmenActions += @(
        'omen-c10a-server-handoff',
        'omen-c10a-relevance-leave-hold',
        'omen-c10a-relevance-enter-hold')
    $requiredI5Actions += @(
        'i5-c10a-server-observe',
        'i5-c10a-relevance-leave',
        'i5-c10a-relevance-left-hold',
        'i5-c10a-relevance-enter',
        'i5-c10a-relevance-entered-hold')
}

$serverSnapshotEpochs = @($server |
    Where-Object {
        [string]$_.state -eq 'snapshot_server_accepted' -and
        (Detail-String $_ 'uid') -eq $spawnUid
    } |
    ForEach-Object { Detail-Long $_ 'epoch' } |
    Where-Object { $null -ne $_ } |
    Sort-Object -Unique)
$cleanupEffect = if ($cleanup -and $cleanup.receipt) {
    [string]$cleanup.receipt.effect
} else { '' }
$firstRelease = Find-State $omen 'probe_passed' 'omen-c10a-mount-first-release'
$secondRelease = Find-State $i5 'probe_passed' 'i5-c10a-mount-second-release'
$omenReclaim = Find-State $omen 'probe_passed' 'omen-c10a-mount-disconnect-reclaim'
$i5Reclaim = Find-State $i5 'probe_passed' 'i5-c10a-mount-observe-reclaim'
$i5RelevanceLeave = Find-State $i5 'probe_passed' 'i5-c10a-relevance-leave'
$i5RelevanceEnter = Find-State $i5 'probe_passed' 'i5-c10a-relevance-enter'
$i5RelevanceEvents = @($server | Where-Object {
    [string]$_.state -in @(
        'snapshot_relevance_entered', 'snapshot_relevance_left') -and
    (Detail-String $_ 'uid') -eq $spawnUid -and
    ((-not $isRelevance) -or (Detail-Long $_ 'epoch') -eq 6) -and
    (Detail-Long $_ 'peer') -eq $i5Peer
} | Sort-Object { [DateTimeOffset]$_.timestamp_utc })
$i5RelevanceStates = @($i5RelevanceEvents | ForEach-Object { [string]$_.state })
$firstI5Enter = [Array]::IndexOf(
    $i5RelevanceStates, 'snapshot_relevance_entered')
$i5LeaveIndex = [Array]::IndexOf(
    $i5RelevanceStates, 'snapshot_relevance_left')
$lastI5Enter = [Array]::LastIndexOf(
    $i5RelevanceStates, 'snapshot_relevance_entered')
$serverUntaggedAdoption = @($server | Where-Object {
    [string]$_.state -eq 'saddle_authority_adopted' -and
    (Detail-String $_ 'uid') -eq $spawnUid -and
    [string]$_.detail -match '(?:^| )source=(?:existing_mount_scan|snapshot)(?: |$)' -and
    [string]$_.detail -match '(?:^| )run_tag=absent(?: |$)'
} | Select-Object -First 1)[0]
$omenUntaggedAdoption = @($omen | Where-Object {
    [string]$_.state -eq 'saddle_authority_adopted' -and
    (Detail-String $_ 'uid') -eq $spawnUid -and
    [string]$_.detail -match '(?:^| )source=existing_untagged_owner(?: |$)'
} | Select-Object -First 1)[0]
$i5UntaggedAdoption = @($i5 | Where-Object {
    [string]$_.state -eq 'saddle_authority_adopted' -and
    (Detail-String $_ 'uid') -eq $spawnUid -and
    [string]$_.detail -match '(?:^| )source=server_snapshot(?: |$)'
} | Select-Object -First 1)[0]

$checks = [ordered]@{
    scenario_manifest_is_correlated =
        $scenario -and
        [string]$scenario.run_id -eq $RunId -and
        [string]$scenario.profile -in @(
            'c10a-mount', 'c10a-vehicle-relevance')
    all_three_saddle_streams_present =
        $omen.Count -gt 0 -and $i5.Count -gt 0 -and $server.Count -gt 0
    no_probe_or_snapshot_failures = @(
        @($omen) + @($i5) + @($server) | Where-Object {
            [string]$_.state -in @(
                'probe_failed', 'snapshot_rejected',
                'saddle_transfer_rejected', 'stale_epoch_probe_failed')
        }).Count -eq 0
    exact_spawn_owner = $null -ne $spawn -and $omenPeer -gt 0
    relevance_target_is_an_ordinary_untagged_mount =
        (-not $isRelevance) -or (
            $null -ne $spawn -and
            [string]$spawn.detail -match '(?:^| )run_tag=absent(?: |$)')
    untagged_authority_is_discovered_not_preseeded =
        (-not $isRelevance) -or (
            $null -ne $serverUntaggedAdoption -and
            $null -ne $omenUntaggedAdoption -and
            $null -ne $i5UntaggedAdoption)
    all_required_client_actions_passed =
        @($requiredOmenActions | Where-Object {
            -not (Passed-Action $omen $_)
        }).Count -eq 0 -and
        @($requiredI5Actions | Where-Object {
            -not (Passed-Action $i5 $_)
        }).Count -eq 0
    exact_owner_epoch_sequence =
        $transferFacts.Count -eq $(if ($isRelevance) { 4 } else { 3 }) -and
        $transferFacts[0].old_owner -eq $omenPeer -and
        $transferFacts[0].new_owner -eq $i5Peer -and
        $transferFacts[0].epoch -eq 2 -and
        $transferFacts[1].old_owner -eq $i5Peer -and
        $transferFacts[1].new_owner -eq $omenPeer -and
        $transferFacts[1].epoch -eq 3 -and
        $transferFacts[2].old_owner -eq $i5Peer -and
        $transferFacts[2].new_owner -eq $omenPeer -and
        $transferFacts[2].epoch -eq 5 -and
        ((-not $isRelevance) -or (
            $transferFacts[3].old_owner -eq $omenPeer -and
            $transferFacts[3].new_owner -eq $serverPeer -and
            $transferFacts[3].user -eq 0 -and
            $serverPeer -gt 0 -and
            $serverPeer -notin @($omenPeer, $i5Peer) -and
            $transferFacts[3].epoch -eq 6))
    disconnect_reclaimed_to_exact_live_i5_epoch4 =
        $null -ne $reclaim -and
        (Detail-Long $omenReclaim 'owner') -eq $i5Peer -and
        (Detail-Long $omenReclaim 'epoch') -eq 4 -and
        (Detail-Long $i5Reclaim 'owner') -eq $i5Peer -and
        (Detail-Long $i5Reclaim 'epoch') -eq 4
    native_release_retained_rider_ownership =
        (Detail-Long $firstRelease 'owner_retained') -eq $i5Peer -and
        (Detail-Long $firstRelease 'epoch') -eq 2 -and
        (Detail-Long $secondRelease 'owner_retained') -eq $omenPeer -and
        (Detail-Long $secondRelease 'epoch') -eq 5
    first_i5_drive_passed = Drive-Passed $i5Drive
    first_omen_observer_passed = Observe-Passed $omenObserve
    reverse_omen_drive_passed = Drive-Passed $omenDrive
    reverse_i5_observer_passed = Observe-Passed $i5Observe
    canonical_server_snapshots_cover_required_epochs =
        @($(if ($isRelevance) { @(2, 3, 4, 5, 6) } else { @(2, 3, 4, 5) }) |
          Where-Object {
            $_ -notin $serverSnapshotEpochs
        }).Count -eq 0
    dedicated_server_owner_publishes_to_both_clients =
        (-not $isRelevance) -or (
            $serverPeer -gt 0 -and
            @($server | Where-Object {
                [string]$_.state -eq 'snapshot_server_accepted' -and
                (Detail-String $_ 'uid') -eq $spawnUid -and
                (Detail-Long $_ 'epoch') -eq 6 -and
                (Detail-Long $_ 'owner') -eq $serverPeer -and
                [string]$_.detail -match '(?:^| )source=server_owner(?: |$)'
            }).Count -gt 0 -and
            @($omen | Where-Object {
                [string]$_.state -eq 'snapshot_replica_applied' -and
                (Detail-String $_ 'uid') -eq $spawnUid -and
                (Detail-Long $_ 'epoch') -eq 6 -and
                (Detail-Long $_ 'owner') -eq $serverPeer
            }).Count -gt 0 -and
            @($i5 | Where-Object {
                [string]$_.state -eq 'snapshot_replica_applied' -and
                (Detail-String $_ 'uid') -eq $spawnUid -and
                (Detail-Long $_ 'epoch') -eq 6 -and
                (Detail-Long $_ 'owner') -eq $serverPeer
            }).Count -gt 0)
    producer_direct_fanout_is_observed =
        (-not $isRelevance) -or @($server | Where-Object {
            [string]$_.state -eq 'snapshot_relevance_fanout' -and
            (Detail-String $_ 'uid') -eq $spawnUid -and
            (Detail-Long $_ 'epoch') -eq 6 -and
            [string]$_.detail -match '(?:^| )target=direct_per_observer(?: |$)'
        }).Count -gt 0
    physical_i5_aoi_leave_and_reenter =
        (-not $isRelevance) -or (
            $null -ne $i5RelevanceLeave -and
            $null -ne $i5RelevanceEnter -and
            $firstI5Enter -ge 0 -and
            $i5LeaveIndex -gt $firstI5Enter -and
            $lastI5Enter -gt $i5LeaveIndex)
    stale_transfer_fence_hit_on_both_clients =
        @($omen | Where-Object {
            [string]$_.state -eq 'transfer_stale_epoch_rejected' -and
            (Detail-String $_ 'uid') -eq $spawnUid
        }).Count -ge 3 -and
        @($i5 | Where-Object {
            [string]$_.state -eq 'transfer_stale_epoch_rejected' -and
            (Detail-String $_ 'uid') -eq $spawnUid
        }).Count -ge 3
    stale_snapshot_and_rider_edge_fence_hit_on_both_clients =
        @($omen | Where-Object {
            [string]$_.state -eq 'snapshot_stale_epoch_rejected' -and
            (Detail-String $_ 'uid') -eq $spawnUid
        }).Count -ge 3 -and
        @($i5 | Where-Object {
            [string]$_.state -eq 'snapshot_stale_epoch_rejected' -and
            (Detail-String $_ 'uid') -eq $spawnUid
        }).Count -ge 3 -and
        @($server | Where-Object {
            [string]$_.state -eq 'stale_epoch_probe_sent' -and
            (Detail-String $_ 'uid') -eq $spawnUid -and
            [string]$_.detail -match 'stale_rider=[1-9][0-9]*:[1-9][0-9]*'
        }).Count -ge 3
    native_zero_ledgers_clean =
        (Native-Ledger-Clean $omenNative) -and
        (Native-Ledger-Clean $i5Native) -and
        (Native-Ledger-Clean $serverNative)
    exact_server_and_client_mod_artifact =
        $deploy -and [string]$deploy.result -eq 'passed' -and
        [bool]$deploy.plugin_loaded -and [bool]$deploy.server_ready -and
        -not [string]::IsNullOrWhiteSpace([string]$deploy.local_sha256) -and
        [string]$deploy.local_sha256 -eq [string]$deploy.host_sha256 -and
        [string]$deploy.local_sha256 -eq [string]$deploy.container_sha256 -and
        [string]$deploy.local_sha256 -eq [string]$omenLifecycle.plugin_sha256 -and
        [string]$deploy.local_sha256 -eq [string]$i5Lifecycle.plugin_sha256
    exact_paired_gateway_image =
        $provenance -and [bool]$provenance.exact_image_match -and
        [string]$provenance.result -eq 'passed' -and
        [string]$provenance.mod_release -eq [string]$composition.mod_release
    composition_completed =
        $composition -and [string]$composition.result -eq 'completed'
    exactly_one_expected_mount_destroyed =
        $cleanup -and -not $cleanup.cleanup_error -and
        $cleanupEffect -match '(?:^| )matched=1(?: |$)' -and
        $cleanupEffect -match '(?:^| )destroyed=1(?: |$)' -and
        $cleanupEffect -match '(?:^| )skipped_live_owner=0(?: |$)' -and
        $cleanupEffect -match '(?:^| )mount=1(?: |$)'
    relevance_cleanup_is_exact_and_untagged =
        (-not $isRelevance) -or
        $cleanupEffect -match '(?:^| )untagged_tracked=1(?: |$)'
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_mount_physical_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    peers = [ordered]@{ omen = $omenPeer; i5 = $i5Peer; server = $serverPeer }
    transfers = $transferFacts
    probe_metrics = @($i5Drive, $omenObserve, $omenDrive, $i5Observe)
    server_snapshot_epochs = $serverSnapshotEpochs
    relevance = [ordered]@{
        enabled = $isRelevance
        i5_peer = $i5Peer
        states = $i5RelevanceStates
    }
    cleanup_effect = $cleanupEffect
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'c10a-mount-summary.json'
}
$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.File]::WriteAllText(
    $absoluteOutput,
    ($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 12
if ($failed.Count -gt 0) { exit 1 }
