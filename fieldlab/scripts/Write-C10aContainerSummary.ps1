#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one C10a container run into a fail-closed physical acceptance receipt.

.DESCRIPTION
A pass requires one and only one canonical item grant across OMEN and i5, one
stale loser, exact duplicate receipt replay without a second credit, journaled
empty inventory on both fresh processes, clean native-poison ledgers, exact
paired Gateway provenance, and destruction of the one tagged wood chest.
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
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $path -Encoding utf8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $row = $line | ConvertFrom-Json -ErrorAction Stop
            if ([string]$row.run_id -eq $RunId) { [void]$rows.Add($row) }
        } catch { }
    }
    @($rows.ToArray())
}

function Read-JsonFile([string] $RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json
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

function Detail-Long([object] $Row, [string] $Name) {
    if ($null -eq $Row) { return $null }
    $match = [regex]::Match(
        [string]$Row.detail,
        '(?:^| )' + [regex]::Escape($Name) + '=(-?[0-9]+)(?: |$)')
    if (-not $match.Success) { return $null }
    [long]$match.Groups[1].Value
}

function Detail-Text([object] $Row, [string] $Name) {
    if ($null -eq $Row) { return $null }
    $match = [regex]::Match(
        [string]$Row.detail,
        '(?:^| )' + [regex]::Escape($Name) + '=([^ ]+)(?: |$)')
    if (-not $match.Success) { return $null }
    [string]$match.Groups[1].Value
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

$omen = Read-JsonLines 'omen\container-cutover.jsonl'
$i5 = Read-JsonLines 'i5\container-cutover.jsonl'
$server = Read-JsonLines 'server\container-cutover.jsonl'
$omenJournal = Read-JsonLines 'omen\zdo-journal-cutover.jsonl'
$i5Journal = Read-JsonLines 'i5\zdo-journal-cutover.jsonl'
$serverJournal = Read-JsonLines 'server\zdo-journal-cutover.jsonl'
$omenNative = Read-JsonLines 'omen\native-network-use.jsonl'
$i5Native = Read-JsonLines 'i5\native-network-use.jsonl'
$serverNative = Read-JsonLines 'server\native-network-use.jsonl'
$composition = Read-JsonFile 'composition.json'
$provenance = Read-JsonFile 'gateway-image-provenance.json'
$cleanup = Read-JsonFile 'residue-cleanup.json'

$action = 'c10a-container-contended-take'
$omenDecision = Find-State $omen 'probe_passed' $action
$i5Decision = Find-State $i5 'probe_passed' $action
$clientDecisions = @($omenDecision, $i5Decision)
$commits = @($server | Where-Object {
    [string]$_.state -eq 'transaction_committed' -and
    [string]$_.action_id -eq $action
})
$rejects = @($server | Where-Object {
    [string]$_.state -eq 'transaction_rejected' -and
    [string]$_.action_id -eq $action
})
$duplicateReplays = @($server | Where-Object {
    [string]$_.state -eq 'transaction_duplicate_replayed' -and
    [string]$_.action_id -eq $action
})
$serverOwnerBlocks = @($server | Where-Object {
    [string]$_.state -eq 'native_owner_reassignment_suppressed'
})
$serverOwnedBlocks = @($serverOwnerBlocks | Where-Object {
    (Detail-Long $_ 'held_owner') -ne 0
})
$ownerZeroBlocks = @($serverOwnerBlocks | Where-Object {
    (Detail-Long $_ 'held_owner') -eq 0
})
$spawn = Find-State $server 'container_spawned' 'omen-c10a-container-spawn'
$uid = Detail-Text $spawn 'uid'
$barrier = Find-State $server 'contention_barrier_released' $action
$heldContenders = @($server | Where-Object {
    [string]$_.state -eq 'transaction_contender_held' -and
    [string]$_.action_id -eq $action
})
$mutationPosts = @($serverJournal | Where-Object {
    [string]$_.state -eq 'mutation_posted' -and
    (Detail-Text $_ 'uid') -eq $uid -and
    (Detail-Text $_ 'receipt_required') -eq 'True'
})
$mutationAccepts = @($serverJournal | Where-Object {
    [string]$_.state -eq 'canonical_mutation_accepted'
})
$acceptedCanaryPosts = @()
foreach ($post in $mutationPosts) {
    $sourceSequence = Detail-Long $post 'source_seq'
    $matching = @($mutationAccepts | Where-Object {
        (Detail-Long $_ 'source_seq') -eq $sourceSequence -and
        (Detail-Long $_ 'recipients') -eq 2 -and
        (Detail-Text $_ 'result') -eq 'delta'
    })
    if ($matching.Count -eq 1) { $acceptedCanaryPosts += $post }
}
$omenFreshSnapshots = @($omenJournal | Where-Object {
    [string]$_.state -eq 'typed_apply_progress' -and
    (Detail-Text $_ 'uid') -eq $uid -and
    (Detail-Text $_ 'kind') -eq 'snapshot'
})
$i5FreshSnapshots = @($i5Journal | Where-Object {
    [string]$_.state -eq 'typed_apply_progress' -and
    (Detail-Text $_ 'uid') -eq $uid -and
    (Detail-Text $_ 'kind') -eq 'snapshot'
})
$omenInterestQueues = @($omenJournal | Where-Object {
    [string]$_.state -eq 'canonical_interest_queued'
})
$i5InterestQueues = @($i5Journal | Where-Object {
    [string]$_.state -eq 'canonical_interest_queued'
})
$omenInterestReceipts = @($omenJournal | Where-Object {
    [string]$_.state -eq 'interest_registered'
})
$i5InterestReceipts = @($i5Journal | Where-Object {
    [string]$_.state -eq 'interest_registered'
})
$cleanupEffect = if ($cleanup -and $cleanup.receipt) {
    [string]$cleanup.receipt.effect
} else { '' }

$acceptedClients = @($clientDecisions | Where-Object {
    (Detail-Text $_ 'accepted') -eq 'true'
})
$staleClients = @($clientDecisions | Where-Object {
    (Detail-Text $_ 'accepted') -eq 'false' -and
    (Detail-Text $_ 'result') -eq 'stale_revision'
})
$omenReconstructed =
    Find-State $omen 'probe_passed' 'omen-c10a-container-reconstructed'
$i5Reconstructed =
    Find-State $i5 'probe_passed' 'i5-c10a-container-reconstructed'
$inventoryDelta = 0
$inventoryFacts = @()
foreach ($decision in $clientDecisions) {
    $before = Detail-Long $decision 'inventory_before'
    $after = Detail-Long $decision 'inventory_after'
    $granted = Detail-Long $decision 'granted'
    if ($null -ne $before -and $null -ne $after) {
        $inventoryDelta += ($after - $before)
    }
    $inventoryFacts += [pscustomobject]@{
        role = [string]$decision.role
        accepted = Detail-Text $decision 'accepted'
        result = Detail-Text $decision 'result'
        granted = $granted
        inventory_before = $before
        inventory_after = $after
    }
}

$checks = [ordered]@{
    all_three_container_streams_present =
        $omen.Count -gt 0 -and $i5.Count -gt 0 -and $server.Count -gt 0
    no_container_failures = @(
        @($omen) + @($i5) + @($server) | Where-Object {
            [string]$_.state -in @(
                'probe_failed', 'container_spawn_rejected',
                'transaction_request_rejected')
        }).Count -eq 0
    actual_seeded_wood_container_spawned =
        $null -ne $spawn -and $null -ne $uid -and
        (Detail-Long $spawn 'revision') -eq 1 -and
        (Detail-Long $spawn 'count') -eq 1 -and
        (Detail-Text $spawn 'item_prefab') -eq 'Raspberry'
    canonical_inventory_payload_explicit_on_seed_and_commit =
        (Detail-Text $spawn 'inventory_serialization') -eq 'explicit' -and
        $commits.Count -eq 1 -and
        (Detail-Text $commits[0] 'inventory_serialization') -eq 'explicit'
    server_barrier_held_both_distinct_clients_before_mutation =
        $null -ne $barrier -and
        (Detail-Long $barrier 'distinct_peers') -eq 2 -and
        (Detail-Long $barrier 'total_copies') -eq 4 -and
        (Detail-Text $barrier 'mutation_held_until_release') -eq 'true' -and
        $heldContenders.Count -eq 2 -and
        @($heldContenders | ForEach-Object {
            Detail-Long $_ 'sender'
        } | Sort-Object -Unique).Count -eq 2
    seeded_and_empty_mutations_fanned_to_both_clients =
        $mutationPosts.Count -eq 2 -and
        $acceptedCanaryPosts.Count -eq 2 -and
        @($mutationPosts | ForEach-Object {
            Detail-Long $_ 'object_revision'
        } | Sort-Object -Unique).Count -eq 2
    exactly_one_server_commit =
        $commits.Count -eq 1 -and
        (Detail-Text $commits[0] 'accepted') -eq 'True' -and
        (Detail-Text $commits[0] 'result') -eq 'committed' -and
        (Detail-Long $commits[0] 'expected_revision') -eq 1 -and
        (Detail-Long $commits[0] 'revision') -eq 2 -and
        (Detail-Long $commits[0] 'remaining') -eq 0 -and
        (Detail-Long $commits[0] 'granted') -eq 1 -and
        (Detail-Long $commits[0] 'owner') -eq 0
    exactly_one_stale_server_loser =
        $rejects.Count -eq 1 -and
        (Detail-Text $rejects[0] 'accepted') -eq 'False' -and
        (Detail-Text $rejects[0] 'result') -eq 'stale_revision' -and
        (Detail-Long $rejects[0] 'expected_revision') -eq 1 -and
        (Detail-Long $rejects[0] 'revision') -eq 2 -and
        (Detail-Long $rejects[0] 'remaining') -eq 0 -and
        (Detail-Long $rejects[0] 'granted') -eq 0
    exact_duplicate_replay_for_both_clients =
        $duplicateReplays.Count -eq 2 -and
        @($duplicateReplays | ForEach-Object {
            Detail-Long $_ 'sender'
        } | Sort-Object -Unique).Count -eq 2
    client_xor_winner_and_stale_loser =
        $acceptedClients.Count -eq 1 -and $staleClients.Count -eq 1
    total_real_player_inventory_gain_is_one =
        $inventoryDelta -eq 1 -and
        @($inventoryFacts | Where-Object {
            $null -eq $_.inventory_before -or
            $null -eq $_.inventory_after -or
            ($_.inventory_after - $_.inventory_before) -ne $_.granted
        }).Count -eq 0
    both_clients_suppressed_native_takeall_and_replayed_duplicate =
        @($clientDecisions | Where-Object {
            (Detail-Text $_ 'native_takeall_suppressed') -ne 'true' -or
            (Detail-Text $_ 'duplicate_replayed') -ne 'true'
        }).Count -eq 0
    server_suppressed_owner_reassignment_for_both_clients =
        @($serverOwnedBlocks | ForEach-Object {
            Detail-Long $_ 'attempted_owner'
        } | Sort-Object -Unique).Count -ge 2 -and
        @($ownerZeroBlocks | ForEach-Object {
            Detail-Long $_ 'attempted_owner'
        } | Sort-Object -Unique).Count -ge 2 -and
        @($serverOwnerBlocks | Where-Object {
            (Detail-Text $_ 'source') -ne 'ZDOMan.ReleaseNearbyZDOS'
        }).Count -eq 0
    fresh_processes_forced_durable_interest_refresh =
        $omenInterestQueues.Count -ge 2 -and
        $i5InterestQueues.Count -ge 2 -and
        (Detail-Text $omenInterestQueues[-1] 'refresh') -eq 'true' -and
        (Detail-Text $i5InterestQueues[-1] 'refresh') -eq 'true' -and
        (Detail-Long $omenInterestReceipts[-1] 'snapshot_count') -gt 0 -and
        (Detail-Long $i5InterestReceipts[-1] 'snapshot_count') -gt 0
    both_fresh_processes_received_exact_container_snapshot =
        $omenFreshSnapshots.Count -ge 1 -and $i5FreshSnapshots.Count -ge 1
    both_fresh_processes_reconstructed_revision_two_empty =
        $null -ne $omenReconstructed -and
        $null -ne $i5Reconstructed -and
        (Detail-Long $omenReconstructed 'revision') -eq 2 -and
        (Detail-Long $i5Reconstructed 'revision') -eq 2 -and
        (Detail-Long $omenReconstructed 'count') -eq 0 -and
        (Detail-Long $i5Reconstructed 'count') -eq 0 -and
        (Detail-Long $omenReconstructed 'actual_inventory') -eq 0 -and
        (Detail-Long $i5Reconstructed 'actual_inventory') -eq 0 -and
        (Detail-Long $omenReconstructed 'owner') -eq 0 -and
        (Detail-Long $i5Reconstructed 'owner') -eq 0
    native_zero_ledgers_clean =
        (Native-Ledger-Clean $omenNative) -and
        (Native-Ledger-Clean $i5Native) -and
        (Native-Ledger-Clean $serverNative)
    exact_paired_gateway_image =
        $provenance -and [bool]$provenance.exact_image_match -and
        [string]$provenance.result -eq 'passed' -and
        [string]$provenance.mod_release -eq [string]$composition.mod_release
    composition_completed =
        $composition -and [string]$composition.result -eq 'completed'
    exactly_one_tagged_container_destroyed =
        $cleanup -and -not $cleanup.cleanup_error -and
        $cleanupEffect -match '(?:^| )matched=1(?: |$)' -and
        $cleanupEffect -match '(?:^| )destroyed=1(?: |$)' -and
        $cleanupEffect -match '(?:^| )skipped_live_owner=0(?: |$)' -and
        $cleanupEffect -match '(?:^| )container=1(?: |$)'
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_container_physical_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    container_uid = $uid
    inventory = $inventoryFacts
    server_commit_count = $commits.Count
    server_stale_reject_count = $rejects.Count
    duplicate_replay_count = $duplicateReplays.Count
    total_inventory_delta = $inventoryDelta
    cleanup_effect = $cleanupEffect
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'c10a-container-summary.json'
}
$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.File]::WriteAllText(
    $absoluteOutput,
    ($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 12
if ($failed.Count -gt 0) { exit 1 }
