#Requires -Version 5.1
<#
.SYNOPSIS
Reduce one C10a autonomous-creature run into a fail-closed acceptance receipt.

.DESCRIPTION
Correlates OMEN, i5, and AM4. A pass means a real unridden Lox executed
MonsterAI only at the canonical owner in epochs 1, 2, and 4; the other client
was owner-gated while presenting canonical motion; autonomous execution
resumed promptly after native saddle release and disconnect reclaim; native
network use stayed zero; and the one tagged Lox was destroyed.
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

function Detail-Text([object] $Row, [string] $Name) {
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

function Find-StateEpoch(
    [object[]] $Rows,
    [string] $State,
    [long] $Epoch) {
    @($Rows | Where-Object {
        [string]$_.state -eq $State -and
        (Detail-Long $_ 'epoch') -eq $Epoch
    } | Sort-Object { [DateTimeOffset]$_.timestamp_utc } |
        Select-Object -First 1)[0]
}

function Passed-Action([object[]] $Rows, [string] $ActionId) {
    $null -ne (Find-State $Rows 'probe_passed' $ActionId)
}

function Probe-Metrics([object[]] $Rows, [string] $ActionId) {
    $row = Find-State $Rows 'probe_passed' $ActionId
    [ordered]@{
        action_id = $ActionId
        mode = Detail-Text $row 'mode'
        owner_ticks = Detail-Long $row 'owner_ticks'
        blocked_ticks = Detail-Long $row 'blocked_ticks'
        distance = Detail-Double $row 'distance'
        snapshot_advance = Detail-Long $row 'snapshot_advance'
        owner = Detail-Long $row 'owner'
        epoch = Detail-Long $row 'epoch'
        rider_observed = Detail-Text $row 'rider_observed'
        authority_changed = Detail-Text $row 'authority_changed'
    }
}

function Drive-Passed([object] $Metrics, [long] $Epoch) {
    $null -ne $Metrics -and $Metrics.mode -eq 'drive' -and
    $Metrics.epoch -eq $Epoch -and
    $null -ne $Metrics.owner_ticks -and $Metrics.owner_ticks -ge 40 -and
    $null -ne $Metrics.blocked_ticks -and $Metrics.blocked_ticks -eq 0 -and
    $null -ne $Metrics.distance -and $Metrics.distance -ge 1.0 -and
    $null -ne $Metrics.snapshot_advance -and $Metrics.snapshot_advance -ge 20 -and
    $Metrics.rider_observed -eq 'false' -and
    $Metrics.authority_changed -eq 'false'
}

function Observe-Passed([object] $Metrics, [long] $Epoch) {
    $null -ne $Metrics -and $Metrics.mode -eq 'observe' -and
    $Metrics.epoch -eq $Epoch -and
    $null -ne $Metrics.owner_ticks -and $Metrics.owner_ticks -eq 0 -and
    $null -ne $Metrics.blocked_ticks -and $Metrics.blocked_ticks -ge 40 -and
    $null -ne $Metrics.distance -and $Metrics.distance -ge 1.0 -and
    $null -ne $Metrics.snapshot_advance -and $Metrics.snapshot_advance -ge 20 -and
    $Metrics.rider_observed -eq 'false' -and
    $Metrics.authority_changed -eq 'false'
}

function Seconds-Between([object] $Before, [object] $After) {
    if ($null -eq $Before -or $null -eq $After) { return $null }
    ([DateTimeOffset]$After.timestamp_utc -
        [DateTimeOffset]$Before.timestamp_utc).TotalSeconds
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

$omenAi = Read-JsonLines 'omen\creature-ai-cutover.jsonl'
$i5Ai = Read-JsonLines 'i5\creature-ai-cutover.jsonl'
$omenSaddle = Read-JsonLines 'omen\saddle-cutover.jsonl'
$i5Saddle = Read-JsonLines 'i5\saddle-cutover.jsonl'
$serverSaddle = Read-JsonLines 'server\saddle-cutover.jsonl'
$omenNative = Read-JsonLines 'omen\native-network-use.jsonl'
$i5Native = Read-JsonLines 'i5\native-network-use.jsonl'
$serverNative = Read-JsonLines 'server\native-network-use.jsonl'
$composition = Read-JsonFile 'composition.json'
$provenance = Read-JsonFile 'gateway-image-provenance.json'
$cleanup = Read-JsonFile 'residue-cleanup.json'

$spawn = Find-State $serverSaddle 'saddle_spawned' 'omen-c10a-creature-spawn'
$omenPeer = Detail-Long $spawn 'owner'
$transfers = @($serverSaddle |
    Where-Object { [string]$_.state -eq 'saddle_owner_transferred' } |
    Sort-Object { [DateTimeOffset]$_.timestamp_utc })
$transferFacts = @($transfers | ForEach-Object {
    [pscustomobject]@{
        action_id = [string]$_.action_id
        old_owner = Detail-Long $_ 'old_owner'
        new_owner = Detail-Long $_ 'new_owner'
        epoch = Detail-Long $_ 'epoch'
    }
})
$i5Peer = if ($transferFacts.Count -gt 0) {
    $transferFacts[0].new_owner
} else { $null }
$reclaim = @($serverSaddle | Where-Object {
    [string]$_.state -eq 'saddle_disconnect_reclaimed' -and
    (Detail-Long $_ 'departed') -eq $omenPeer -and
    (Detail-Long $_ 'fallback_owner') -eq $i5Peer -and
    (Detail-Long $_ 'epoch') -eq 4
} | Select-Object -First 1)[0]

$metrics = @(
    [pscustomobject](Probe-Metrics $omenAi 'omen-c10a-creature-ai-initial')
    [pscustomobject](Probe-Metrics $i5Ai 'i5-c10a-creature-ai-initial-observe')
    [pscustomobject](Probe-Metrics $i5Ai 'i5-c10a-creature-ai-i5')
    [pscustomobject](Probe-Metrics $omenAi 'omen-c10a-creature-ai-i5-observe')
    [pscustomobject](Probe-Metrics $i5Ai 'i5-c10a-creature-ai-reclaim')
    [pscustomobject](Probe-Metrics $omenAi 'omen-c10a-creature-ai-reclaim-observe')
)

$epoch2Release = @($i5Saddle | Where-Object {
    [string]$_.state -eq 'vanilla_release_observed' -and
    (Detail-Long $_ 'owner') -eq $i5Peer -and
    (Detail-Long $_ 'user_before') -eq $i5Peer -and
    (Detail-Long $_ 'user_after') -eq 0
} | Sort-Object { [DateTimeOffset]$_.timestamp_utc } |
    Select-Object -First 1)[0]
$epoch2FirstAi = Find-StateEpoch $i5Ai 'first_autonomous_owner_ai_tick' 2
$epoch4Applied = Find-StateEpoch $i5Saddle 'saddle_reclaim_applied' 4
$epoch4FirstAi = Find-StateEpoch $i5Ai 'first_autonomous_owner_ai_tick' 4
$epoch2RecoverySeconds = Seconds-Between $epoch2Release $epoch2FirstAi
$epoch4RecoverySeconds = Seconds-Between $epoch4Applied $epoch4FirstAi

$serverSnapshotEpochs = @($serverSaddle |
    Where-Object { [string]$_.state -eq 'snapshot_server_accepted' } |
    ForEach-Object { Detail-Long $_ 'epoch' } |
    Where-Object { $null -ne $_ } |
    Sort-Object -Unique)
$cleanupEffect = if ($cleanup -and $cleanup.receipt) {
    [string]$cleanup.receipt.effect
} else { '' }

$requiredOmenSaddleActions = @(
    'omen-c10a-creature-spawn',
    'omen-c10a-creature-wait',
    'omen-c10a-creature-rendezvous',
    'omen-c10a-creature-observe-transfer',
    'omen-c10a-creature-transfer-released',
    'omen-c10a-creature-disconnect-reclaim')
$requiredI5SaddleActions = @(
    'i5-c10a-creature-wait',
    'i5-c10a-creature-rendezvous',
    'i5-c10a-creature-transfer',
    'i5-c10a-creature-observe-reclaim')

$checks = [ordered]@{
    both_creature_streams_and_three_saddle_streams_present =
        $omenAi.Count -gt 0 -and $i5Ai.Count -gt 0 -and
        $omenSaddle.Count -gt 0 -and $i5Saddle.Count -gt 0 -and
        $serverSaddle.Count -gt 0
    no_creature_or_saddle_failures = @(
        @($omenAi) + @($i5Ai) + @($omenSaddle) +
        @($i5Saddle) + @($serverSaddle) | Where-Object {
            [string]$_.state -in @(
                'probe_failed', 'snapshot_rejected',
                'saddle_transfer_rejected', 'stale_epoch_probe_failed')
        }).Count -eq 0
    actual_tamed_lox_spawned =
        $null -ne $spawn -and $omenPeer -gt 0 -and
        [string]$spawn.detail -match '(?:^| )prefab=Lox(?: |$)' -and
        [string]$spawn.detail -match '(?:^| )tamed=true(?: |$)'
    all_authority_transition_actions_passed =
        @($requiredOmenSaddleActions | Where-Object {
            -not (Passed-Action $omenSaddle $_)
        }).Count -eq 0 -and
        @($requiredI5SaddleActions | Where-Object {
            -not (Passed-Action $i5Saddle $_)
        }).Count -eq 0
    exact_owner_epoch_sequence =
        $transferFacts.Count -eq 2 -and
        $transferFacts[0].old_owner -eq $omenPeer -and
        $transferFacts[0].new_owner -eq $i5Peer -and
        $transferFacts[0].epoch -eq 2 -and
        $transferFacts[1].old_owner -eq $i5Peer -and
        $transferFacts[1].new_owner -eq $omenPeer -and
        $transferFacts[1].epoch -eq 3 -and
        $null -ne $reclaim
    initial_omen_owner_executes_ai = Drive-Passed $metrics[0] 1
    initial_i5_replica_is_ai_gated = Observe-Passed $metrics[1] 1
    transferred_i5_owner_executes_ai = Drive-Passed $metrics[2] 2
    transferred_omen_replica_is_ai_gated = Observe-Passed $metrics[3] 2
    reclaimed_i5_owner_executes_ai = Drive-Passed $metrics[4] 4
    reclaimed_omen_replica_is_ai_gated = Observe-Passed $metrics[5] 4
    autonomous_ai_resumed_within_two_seconds_after_release =
        $null -ne $epoch2RecoverySeconds -and
        $epoch2RecoverySeconds -ge 0.0 -and $epoch2RecoverySeconds -le 2.0
    autonomous_ai_resumed_within_two_seconds_after_reclaim =
        $null -ne $epoch4RecoverySeconds -and
        $epoch4RecoverySeconds -ge 0.0 -and $epoch4RecoverySeconds -le 2.0
    canonical_server_snapshots_cover_all_authority_epochs =
        @(1, 2, 3, 4 | Where-Object {
            $_ -notin $serverSnapshotEpochs
        }).Count -eq 0
    stale_transfer_and_snapshot_fences_hit_on_both_clients =
        @($omenSaddle | Where-Object {
            [string]$_.state -eq 'transfer_stale_epoch_rejected'
        }).Count -ge 2 -and
        @($i5Saddle | Where-Object {
            [string]$_.state -eq 'transfer_stale_epoch_rejected'
        }).Count -ge 2 -and
        @($omenSaddle | Where-Object {
            [string]$_.state -eq 'snapshot_stale_epoch_rejected'
        }).Count -ge 2 -and
        @($i5Saddle | Where-Object {
            [string]$_.state -eq 'snapshot_stale_epoch_rejected'
        }).Count -ge 2
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
    exactly_one_tagged_mount_destroyed =
        $cleanup -and -not $cleanup.cleanup_error -and
        $cleanupEffect -match '(?:^| )matched=1(?: |$)' -and
        $cleanupEffect -match '(?:^| )destroyed=1(?: |$)' -and
        $cleanupEffect -match '(?:^| )skipped_live_owner=0(?: |$)' -and
        $cleanupEffect -match '(?:^| )mount=1(?: |$)'
}

$failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
$summary = [ordered]@{
    schema_version = 1
    receipt_type = 'c10a_creature_physical_summary'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    checks = $checks
    failed_checks = @($failed | ForEach-Object Key)
    peers = [ordered]@{ omen = $omenPeer; i5 = $i5Peer }
    transfers = $transferFacts
    probe_metrics = $metrics
    recovery_seconds = [ordered]@{
        epoch_2_after_release = $epoch2RecoverySeconds
        epoch_4_after_reclaim = $epoch4RecoverySeconds
    }
    server_snapshot_epochs = $serverSnapshotEpochs
    cleanup_effect = $cleanupEffect
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'c10a-creature-summary.json'
}
$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.File]::WriteAllText(
    $absoluteOutput,
    ($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 12
if ($failed.Count -gt 0) { exit 1 }
