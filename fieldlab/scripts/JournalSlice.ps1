#Requires -Version 5.1
<#
.SYNOPSIS
Delta-slice capture for the append-only ComfyNetworkSense telemetry journals.

.DESCRIPTION
The cutover journals are append-only and are never truncated between runs, so
copying a whole journal into every run directory stores run N's capture as the
concatenation of runs 1..N. Measured on 2026-09-08: 318 captures of
zdo-journal-cutover.jsonl held 14.9 GB whose unique content was a single 101 MB
journal, and consecutive runs produced byte-identical 101 MB files.

Every consumer filters rows to its own run:

    if ($row.run_id -eq $RunId) { $rows += $row }

so the carried-over history is read by nothing. These helpers copy only the
bytes appended since the previous capture and write a sidecar recording the byte
range, so the full journal is still reconstructable by concatenating slices in
run order.

Slices are cut on newline boundaries: the end offset is snapped back to the last
complete line in the copied region, so a journal caught mid-write never yields a
torn JSON line and never drops or duplicates one.

Only journals whose consumers filter by run_id are eligible (see
$script:JournalSliceFamilies). Everything else keeps whole-file copy semantics.
#>

# Deliberately no Set-StrictMode here: this file is dot-sourced into the run
# scripts, so a mode set here would change their semantics too.

# Journals whose every consumer filters rows to the requested run id. Adding a
# name here is a claim that no summarizer reads carried-over history from it.
$script:JournalSliceFamilies = @(
    'zdo-journal-cutover.jsonl',
    'native-network-use.jsonl',
    'routed-rpc-cutover.jsonl',
    'ownership-lease-cutover.jsonl',
    'motion-authority-cutover.jsonl',
    'direct-control-cutover.jsonl',
    'logical-peer-cutover.jsonl',
    'world-zone-cutover.jsonl',
    'socket-quarantine-cutover.jsonl',
    'ship-cutover.jsonl',
    'saddle-cutover.jsonl',
    'container-cutover.jsonl',
    'creature-ai-cutover.jsonl',
    'lumberjacks-game-session.jsonl',
    'native-cutover-scenario-receipts.jsonl',
    'native-autotest-receipts.jsonl'
)

function Test-JournalSliceEligible {
    <#
    .SYNOPSIS
    True when $Name is an append-only journal safe to capture as a delta slice.
    #>
    param([Parameter(Mandatory)][string] $Name)
    return $script:JournalSliceFamilies -contains $Name
}

function Get-JournalLedgerPath {
    param([Parameter(Mandatory)][string] $EvidenceRoot)
    return (Join-Path $EvidenceRoot '.journal-offsets.json')
}

function Read-JournalLedger {
    param([Parameter(Mandatory)][string] $LedgerPath)
    if (-not (Test-Path -LiteralPath $LedgerPath -PathType Leaf)) {
        return @{}
    }
    try {
        $raw = Get-Content -LiteralPath $LedgerPath -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
        $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
        $table = @{}
        foreach ($property in $parsed.PSObject.Properties) {
            $table[$property.Name] = $property.Value
        }
        return $table
    } catch {
        # A corrupt ledger must not strand a run: fall back to full copies.
        Write-Warning "Journal offset ledger unreadable ($LedgerPath): $($_.Exception.Message). Capturing full journals."
        return @{}
    }
}

function Save-JournalLedger {
    param(
        [Parameter(Mandatory)][string] $LedgerPath,
        [Parameter(Mandatory)][hashtable] $Ledger)

    $directory = Split-Path -Parent $LedgerPath
    if ($directory -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $temporary = "$LedgerPath.tmp"
    ($Ledger | ConvertTo-Json -Depth 6) |
        Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $LedgerPath -Force
}

function Get-JournalHeadFingerprint {
    <#
    .SYNOPSIS
    SHA256 over the first 4 KiB, used to notice a journal replaced in place.
    #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [int] $Bytes = 4096)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $buffer = New-Object byte[] $Bytes
        $read = $stream.Read($buffer, 0, $Bytes)
        if ($read -le 0) { return '' }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha.ComputeHash($buffer, 0, $read)
            return [BitConverter]::ToString($hash).Replace('-', '')
        } finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

function Resolve-JournalSliceStart {
    <#
    .SYNOPSIS
    Decide where this capture starts, and why.

    .OUTPUTS
    Hashtable with StartOffset, FullCopy and Reason.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()] $Entry,
        [Parameter(Mandatory)][long] $CurrentSize,
        [Parameter(Mandatory)][AllowEmptyString()][string] $HeadFingerprint)

    if ($null -eq $Entry) {
        return @{ StartOffset = [long]0; FullCopy = $true; Reason = 'first_capture' }
    }

    $recorded = [long]$Entry.offset

    if ($CurrentSize -lt $recorded) {
        return @{ StartOffset = [long]0; FullCopy = $true; Reason = 'truncated' }
    }

    $recordedHead = ''
    if ($Entry.PSObject.Properties.Name -contains 'head_sha256' -and $Entry.head_sha256) {
        $recordedHead = [string]$Entry.head_sha256
    }
    if ($recordedHead -and $HeadFingerprint -and $recordedHead -ne $HeadFingerprint) {
        return @{ StartOffset = [long]0; FullCopy = $true; Reason = 'rotated' }
    }

    return @{ StartOffset = $recorded; FullCopy = $false; Reason = 'delta' }
}

function Get-JournalSlicePlan {
    <#
    .SYNOPSIS
    Decide the start offset for the next capture of $Key.

    .DESCRIPTION
    Split out from the copy so the remote (scp) path can ask where to start
    before it fetches anything.
    #>
    param(
        [Parameter(Mandatory)][string] $LedgerPath,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][long] $CurrentSize,
        [AllowEmptyString()][string] $HeadFingerprint = '',
        [switch] $FullCopy)

    if ($FullCopy) {
        return @{ StartOffset = [long]0; FullCopy = $true; Reason = 'forced_full' }
    }

    $ledger = Read-JournalLedger -LedgerPath $LedgerPath
    $entry = if ($ledger.ContainsKey($Key)) { $ledger[$Key] } else { $null }

    $plan = Resolve-JournalSliceStart `
        -Entry $entry -CurrentSize $CurrentSize -HeadFingerprint $HeadFingerprint
    if ([long]$plan.StartOffset -gt $CurrentSize) {
        $plan = @{ StartOffset = [long]0; FullCopy = $true; Reason = 'offset_past_end' }
    }
    return $plan
}

function Write-JournalSliceOutput {
    <#
    .SYNOPSIS
    Trim a fetched region to whole lines, then write the slice, its sidecar and
    the ledger entry.

    .DESCRIPTION
    Shared by the local and remote capture paths so the line-boundary rule and
    the provenance record have exactly one implementation.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]] $Payload,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $LedgerPath,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][string] $RunId,
        [Parameter(Mandatory)][long] $StartOffset,
        [Parameter(Mandatory)][long] $SourceSize,
        [Parameter(Mandatory)][string] $SourceLabel,
        [AllowEmptyString()][string] $HeadFingerprint = '',
        [bool] $FullCopy = $false,
        [string] $Reason = 'delta')

    $destinationDirectory = Split-Path -Parent $Destination
    if ($destinationDirectory -and
        -not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    $lastNewline = [Array]::LastIndexOf($Payload, [byte]0x0A)
    if ($lastNewline -ge 0) {
        $keep = $lastNewline + 1
        if ($keep -ne $Payload.Length) {
            $trimmed = New-Object byte[] $keep
            [Array]::Copy($Payload, $trimmed, $keep)
            $Payload = $trimmed
        }
    } elseif ($Payload.Length -gt 0) {
        # No line terminator in the region: a single line is still being
        # written. Take nothing this run; the next capture picks it up whole.
        $Payload = New-Object byte[] 0
    }

    $end = $StartOffset + $Payload.Length
    [System.IO.File]::WriteAllBytes($Destination, $Payload)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sliceHash = [BitConverter]::ToString($sha.ComputeHash($Payload)).Replace('-', '')
    } finally { $sha.Dispose() }

    $sidecar = [ordered]@{
        schema_version = 1
        receipt_type   = 'journal_slice'
        run_id         = $RunId
        generated_utc  = [DateTimeOffset]::UtcNow.ToString('o')
        source         = $SourceLabel
        journal        = Split-Path -Leaf $Destination
        start_offset   = $StartOffset
        end_offset     = $end
        bytes          = $Payload.Length
        source_size    = $SourceSize
        sha256         = $sliceHash
        full_copy      = $FullCopy
        reason         = $Reason
    }
    ($sidecar | ConvertTo-Json -Depth 4) |
        Set-Content -LiteralPath "$Destination.slice.json" -Encoding UTF8

    $ledger = Read-JournalLedger -LedgerPath $LedgerPath
    $ledger[$Key] = [ordered]@{
        offset      = $end
        size        = $SourceSize
        head_sha256 = $HeadFingerprint
        run_id      = $RunId
        updated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        source      = $SourceLabel
    }
    Save-JournalLedger -LedgerPath $LedgerPath -Ledger $ledger

    return $sidecar
}

function Copy-JournalSlice {
    <#
    .SYNOPSIS
    Copy the bytes appended to a local $Source since the previous capture.

    .DESCRIPTION
    Writes the slice to $Destination and a provenance sidecar to
    "$Destination.slice.json", then advances the ledger. The end offset is
    snapped back to the last newline so slices always hold whole JSON lines.

    Returns a hashtable describing the capture, or $null when $Source is absent.
    #>
    param(
        [Parameter(Mandatory)][string] $Source,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $LedgerPath,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][string] $RunId,
        [switch] $FullCopy)

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { return $null }

    $currentSize = [long](Get-Item -LiteralPath $Source).Length
    $headFingerprint = if ($currentSize -gt 0) {
        Get-JournalHeadFingerprint -Path $Source
    } else { '' }

    $plan = Get-JournalSlicePlan `
        -LedgerPath $LedgerPath -Key $Key -CurrentSize $currentSize `
        -HeadFingerprint $headFingerprint -FullCopy:$FullCopy

    $start = [long]$plan.StartOffset
    $length = $currentSize - $start
    $payload = New-Object byte[] 0
    if ($length -gt 0) {
        $stream = [System.IO.File]::Open(
            $Source,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite)
        try {
            [void]$stream.Seek($start, [System.IO.SeekOrigin]::Begin)
            $payload = New-Object byte[] $length
            $filled = 0
            while ($filled -lt $length) {
                $read = $stream.Read($payload, $filled, [int]($length - $filled))
                if ($read -le 0) { break }
                $filled += $read
            }
            if ($filled -ne $length) {
                $trimmed = New-Object byte[] $filled
                [Array]::Copy($payload, $trimmed, $filled)
                $payload = $trimmed
            }
        } finally { $stream.Dispose() }
    }

    return (Write-JournalSliceOutput `
        -Payload $payload `
        -Destination $Destination `
        -LedgerPath $LedgerPath `
        -Key $Key `
        -RunId $RunId `
        -StartOffset $start `
        -SourceSize $currentSize `
        -SourceLabel $Source `
        -HeadFingerprint $headFingerprint `
        -FullCopy ([bool]$plan.FullCopy) `
        -Reason ([string]$plan.Reason))
}

function Join-JournalSlices {
    <#
    .SYNOPSIS
    Rebuild a full journal by concatenating its slices in byte-range order.

    .DESCRIPTION
    Walks $EvidenceRoot for "<Journal>.slice.json" sidecars under the given
    actor directory name, orders them by start_offset and concatenates the
    slices into $Destination. Reports any gap between consecutive ranges.
    #>
    param(
        [Parameter(Mandatory)][string] $EvidenceRoot,
        [Parameter(Mandatory)][string] $Journal,
        [Parameter(Mandatory)][string] $Destination,
        [string] $Actor = '')

    $pattern = "$Journal.slice.json"
    $sidecars = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue)
    if ($Actor) {
        $sidecars = @($sidecars | Where-Object { $_.Directory.Name -eq $Actor })
    }

    $records = @()
    foreach ($sidecar in $sidecars) {
        try {
            $meta = Get-Content -LiteralPath $sidecar.FullName -Raw | ConvertFrom-Json
            $slicePath = Join-Path $sidecar.Directory.FullName $meta.journal
            if (Test-Path -LiteralPath $slicePath -PathType Leaf) {
                $records += [pscustomobject]@{
                    Start = [long]$meta.start_offset
                    End   = [long]$meta.end_offset
                    Path  = $slicePath
                }
            }
        } catch { }
    }

    $ordered = @($records | Sort-Object Start, End)
    $gaps = @()
    $cursor = [long]0
    $output = [System.IO.File]::Create($Destination)
    try {
        foreach ($record in $ordered) {
            if ($record.End -le $cursor) { continue }
            if ($record.Start -gt $cursor) {
                $gaps += "$cursor..$($record.Start)"
                $cursor = $record.Start
            }
            $bytes = [System.IO.File]::ReadAllBytes($record.Path)
            $skip = [int]($cursor - $record.Start)
            if ($skip -lt $bytes.Length) {
                $output.Write($bytes, $skip, $bytes.Length - $skip)
            }
            $cursor = $record.End
        }
    } finally { $output.Dispose() }

    return [ordered]@{
        destination = $Destination
        slices      = $ordered.Count
        bytes       = $cursor
        gaps        = $gaps
    }
}
