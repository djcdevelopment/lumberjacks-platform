#Requires -Version 5.1
<#
.SYNOPSIS
Regression cover for the append-only journal delta-slice capture.

.DESCRIPTION
Guards the property the capture depends on: a slice holds exactly the rows a
summarizer would have selected from the full journal, and the slices together
rebuild that journal byte-for-byte.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'JournalSlice.ps1')

$lab = Join-Path $env:TEMP ('jslice-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $lab -Force | Out-Null
$journal  = Join-Path $lab 'zdo-journal-cutover.jsonl'
$evidence = Join-Path $lab 'captures'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$ledger = Get-JournalLedgerPath -EvidenceRoot $evidence

$pass = 0; $fail = 0
function Check($name, $cond, $detail = '') {
    if ($cond) { $script:pass++; "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name $detail" }
}

function Add-Rows([string] $RunId, [int] $Count) {
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $Count; $i++) {
        [void]$sb.Append(('{"run_id":"' + $RunId + '","seq":' + $i + ',"state":"probe"}' + "`n"))
    }
    [System.IO.File]::AppendAllText($journal, $sb.ToString())
}

"=== eligibility gate ==="
Check 'cutover journal is eligible'  (Test-JournalSliceEligible -Name 'zdo-journal-cutover.jsonl')
Check 'perf-sections stays full-copy' (-not (Test-JournalSliceEligible -Name 'perf-sections.jsonl'))
Check 'bepinex.log stays full-copy'   (-not (Test-JournalSliceEligible -Name 'bepinex.log'))

"=== five sequential runs against one append-only journal ==="
$runs = @('run-a','run-b','run-c','run-d','run-e')
$sliceSizes = @()
foreach ($r in $runs) {
    Add-Rows $r 200
    $dest = Join-Path (Join-Path $evidence $r) 'omen\zdo-journal-cutover.jsonl'
    $meta = Copy-JournalSlice -Source $journal -Destination $dest `
        -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId $r
    $sliceSizes += $meta.bytes

    # Summarizer semantics: rows for THIS run, read from the slice only.
    $sliceRows = @(Get-Content $dest | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.run_id -eq $r })
    $fullRows  = @(Get-Content $journal | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.run_id -eq $r })
    Check "$r slice yields same rows as full journal" `
        ($sliceRows.Count -eq $fullRows.Count -and $sliceRows.Count -eq 200) `
        "(slice=$($sliceRows.Count) full=$($fullRows.Count))"

    $foreign = @(Get-Content $dest | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.run_id -ne $r })
    Check "$r slice carries no foreign history" ($foreign.Count -eq 0) "(foreign=$($foreign.Count))"
}

"=== growth is flat, not quadratic ==="
$firstSlice = $sliceSizes[0]; $lastSlice = $sliceSizes[-1]
Check 'last slice is not larger than the first' ($lastSlice -le $firstSlice * 1.1) `
    "(first=$firstSlice last=$lastSlice)"
$journalSize = (Get-Item $journal).Length
$totalSlices = ($sliceSizes | Measure-Object -Sum).Sum
Check 'all slices together ~= one journal' ($totalSlices -eq $journalSize) `
    "(slices=$totalSlices journal=$journalSize)"

"=== reconstruction is byte-exact ==="
$rebuilt = Join-Path $lab 'rebuilt.jsonl'
$result = Join-JournalSlices -EvidenceRoot $evidence `
    -Journal 'zdo-journal-cutover.jsonl' -Destination $rebuilt -Actor 'omen'
$origHash  = (Get-FileHash $journal -Algorithm SHA256).Hash
$rebHash   = (Get-FileHash $rebuilt -Algorithm SHA256).Hash
Check 'rebuilt journal matches original byte-for-byte' ($origHash -eq $rebHash) `
    "(orig=$($origHash.Substring(0,12)) rebuilt=$($rebHash.Substring(0,12)))"
Check 'reconstruction reports no gaps' ($result.gaps.Count -eq 0) "(gaps=$($result.gaps -join ','))"

"=== partial trailing line is not torn ==="
[System.IO.File]::AppendAllText($journal, '{"run_id":"run-f","seq":0,"state":"par')
$destF = Join-Path (Join-Path $evidence 'run-f') 'omen\zdo-journal-cutover.jsonl'
$metaF = Copy-JournalSlice -Source $journal -Destination $destF `
    -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId 'run-f'
Check 'partial line withheld from slice' ($metaF.bytes -eq 0) "(bytes=$($metaF.bytes))"
[System.IO.File]::AppendAllText($journal, 'tial"}' + "`n")
$destF2 = Join-Path (Join-Path $evidence 'run-f2') 'omen\zdo-journal-cutover.jsonl'
$metaF2 = Copy-JournalSlice -Source $journal -Destination $destF2 `
    -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId 'run-f2'
$rows = @(Get-Content $destF2 | ForEach-Object { $_ | ConvertFrom-Json })
Check 'completed line arrives whole on the next run' `
    ($rows.Count -eq 1 -and $rows[0].state -eq 'partial') "(rows=$($rows.Count))"

"=== rotation / truncation ==="
Remove-Item $journal -Force
Add-Rows 'run-g' 50
$destG = Join-Path (Join-Path $evidence 'run-g') 'omen\zdo-journal-cutover.jsonl'
$metaG = Copy-JournalSlice -Source $journal -Destination $destG `
    -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId 'run-g'
Check 'rotation detected, restarts at offset 0' `
    ($metaG.start_offset -eq 0 -and $metaG.full_copy) "(reason=$($metaG.reason))"
$rowsG = @(Get-Content $destG | ForEach-Object { $_ | ConvertFrom-Json })
Check 'post-rotation slice holds the whole new journal' ($rowsG.Count -eq 50) "(rows=$($rowsG.Count))"

"=== no-new-telemetry run ==="
$destH = Join-Path (Join-Path $evidence 'run-h') 'omen\zdo-journal-cutover.jsonl'
$metaH = Copy-JournalSlice -Source $journal -Destination $destH `
    -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId 'run-h'
Check 'idle run produces an empty slice, not a duplicate' ($metaH.bytes -eq 0) "(bytes=$($metaH.bytes))"
Check 'empty slice file exists for the run' (Test-Path $destH)

"=== forced full copy escape hatch ==="
$destI = Join-Path (Join-Path $evidence 'run-i') 'omen\zdo-journal-cutover.jsonl'
$metaI = Copy-JournalSlice -Source $journal -Destination $destI `
    -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId 'run-i' -FullCopy
Check 'FullCopy returns the entire journal' `
    ($metaI.bytes -eq (Get-Item $journal).Length -and $metaI.reason -eq 'forced_full') `
    "(bytes=$($metaI.bytes))"

"=== corrupt ledger degrades to full copy ==="
'not json at all' | Set-Content $ledger
$destJ = Join-Path (Join-Path $evidence 'run-j') 'omen\zdo-journal-cutover.jsonl'
$metaJ = Copy-JournalSlice -Source $journal -Destination $destJ `
    -LedgerPath $ledger -Key 'omen|zdo-journal-cutover.jsonl' -RunId 'run-j' `
    -WarningAction SilentlyContinue
Check 'corrupt ledger does not strand the run' ($metaJ.bytes -gt 0) "(bytes=$($metaJ.bytes))"

Remove-Item $lab -Recurse -Force -ErrorAction SilentlyContinue
""
"RESULT: $pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
