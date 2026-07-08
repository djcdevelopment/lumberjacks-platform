param(
  [string] $TelemetryRoot = "C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\config\comfy-network-sense",
  [string] $SessionId = "",
  [string] $OutputRoot = "",
  [int] $TimelineLimit = 200
)

$ErrorActionPreference = "Stop"

function Read-Jsonl {
  param([string] $Path)

  $rows = [System.Collections.Generic.List[object]]::new()
  if (-not (Test-Path -LiteralPath $Path)) {
    return $rows
  }

  $lineNumber = 0
  foreach ($line in [System.IO.File]::ReadLines($Path)) {
    $lineNumber++
    if ([string]::IsNullOrWhiteSpace($line)) {
      continue
    }

    try {
      $rows.Add(($line | ConvertFrom-Json))
    } catch {
      Write-Warning "Skipping malformed JSONL row ${Path}:$lineNumber ($($_.Exception.Message))"
    }
  }

  return $rows
}

function To-Double {
  param($Value)

  if ($null -eq $Value) {
    return 0.0
  }

  try {
    return [double] $Value
  } catch {
    return 0.0
  }
}

function Select-SessionRows {
  param(
    [object[]] $Rows,
    [string] $SelectedSessionId
  )

  if ([string]::IsNullOrWhiteSpace($SelectedSessionId)) {
    return @($Rows)
  }

  return @($Rows | Where-Object { $_.session_id -eq $SelectedSessionId })
}

if (-not (Test-Path -LiteralPath $TelemetryRoot)) {
  throw "Telemetry root not found: $TelemetryRoot"
}

$hitches = @(Read-Jsonl (Join-Path $TelemetryRoot "perf-hitches.jsonl"))
$sections = @(Read-Jsonl (Join-Path $TelemetryRoot "perf-sections.jsonl"))
$engine = @(Read-Jsonl (Join-Path $TelemetryRoot "perf-engine-log.jsonl"))
$markers = @(Read-Jsonl (Join-Path $TelemetryRoot "perf-markers.jsonl"))
$events = @(Read-Jsonl (Join-Path $TelemetryRoot "event-timeline.jsonl"))

if ([string]::IsNullOrWhiteSpace($SessionId)) {
  $sessionSource =
      @($hitches + $sections + $engine + $markers + $events) |
      Where-Object { -not [string]::IsNullOrWhiteSpace($_.session_id) -and -not [string]::IsNullOrWhiteSpace($_.timestamp_utc) } |
      Sort-Object timestamp_utc -Descending |
      Select-Object -First 1

  if ($null -ne $sessionSource) {
    $SessionId = $sessionSource.session_id
  }
}

$hitches = Select-SessionRows $hitches $SessionId
$sections = Select-SessionRows $sections $SessionId
$engine = Select-SessionRows $engine $SessionId
$markers = Select-SessionRows $markers $SessionId
$events = Select-SessionRows $events $SessionId

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
  $fieldlabRoot = Split-Path -Parent $PSScriptRoot
  $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
  $OutputRoot = Join-Path (Join-Path $fieldlabRoot "runs") "$stamp-networksense-perf-analysis"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$telemetryOut = Join-Path $OutputRoot "telemetry"
$rawOut = Join-Path $OutputRoot "raw"
New-Item -ItemType Directory -Force -Path $telemetryOut | Out-Null
New-Item -ItemType Directory -Force -Path $rawOut | Out-Null

$hitchFrameMs = @($hitches | ForEach-Object { To-Double $_.frame_ms })
$maxFrameMs = 0.0
if ($hitchFrameMs.Count -gt 0) {
  $maxFrameMs = ($hitchFrameMs | Measure-Object -Maximum).Maximum
}

$routePhaseHitches =
    @($hitches |
      Group-Object {
        $state = if ($_.route_state) { $_.route_state } else { "" }
        $stop = if ($_.route_stop_id) { $_.route_stop_id } else { "" }
        $phase = if ($_.route_phase) { $_.route_phase } else { "" }
        "$state|$stop|$phase"
      } |
      ForEach-Object {
        $parts = $_.Name.Split("|", 3)
        $frames = @($_.Group | ForEach-Object { To-Double $_.frame_ms })
        [pscustomobject] [ordered] @{
          route_state = $parts[0]
          route_stop_id = $parts[1]
          route_phase = $parts[2]
          hitch_count = $_.Count
          max_frame_ms = if ($frames.Count -gt 0) { ($frames | Measure-Object -Maximum).Maximum } else { 0.0 }
        }
      } |
      Sort-Object max_frame_ms -Descending)

$sectionSummary =
    @($sections |
      Group-Object section |
      ForEach-Object {
        $elapsed = @($_.Group | ForEach-Object { To-Double $_.elapsed_ms })
        $stats = $elapsed | Measure-Object -Sum -Maximum -Average
        [pscustomobject] [ordered] @{
          section = $_.Name
          count = $_.Count
          total_ms = [math]::Round($stats.Sum, 3)
          max_ms = [math]::Round($stats.Maximum, 3)
          avg_ms = [math]::Round($stats.Average, 3)
        }
      })

$engineWarningBursts =
    @($engine |
      Where-Object { (To-Double $_.warnings_since_last_sample) -gt 0 } |
      Sort-Object { To-Double $_.warnings_since_last_sample } -Descending |
      Select-Object -First 20 |
      ForEach-Object {
        [pscustomobject] [ordered] @{
          timestamp_utc = $_.timestamp_utc
          warnings_since_last_sample = [int] (To-Double $_.warnings_since_last_sample)
          logs_since_last_sample = [int] (To-Double $_.logs_since_last_sample)
          latest_engine_log_type = $_.latest_engine_log_type
          latest_engine_log_message = $_.latest_engine_log_message
          route_state = $_.route_state
          route_stop_id = $_.route_stop_id
          route_phase = $_.route_phase
        }
      })

$writerRows = @($hitches + $sections + $markers)
$maxWriterQueue = 0
$maxDroppedRows = 0
if ($writerRows.Count -gt 0) {
  $maxWriterQueue = ($writerRows | ForEach-Object { To-Double $_.writer_queue_depth } | Measure-Object -Maximum).Maximum
  $maxDroppedRows = ($writerRows | ForEach-Object { To-Double $_.writer_dropped_rows } | Measure-Object -Maximum).Maximum
}

$summary = [ordered] @{
  generated_utc = [DateTime]::UtcNow.ToString("o")
  telemetry_root = $TelemetryRoot
  session_id = $SessionId
  files = [ordered] @{
    perf_hitches = (Join-Path $TelemetryRoot "perf-hitches.jsonl")
    perf_sections = (Join-Path $TelemetryRoot "perf-sections.jsonl")
    perf_engine_log = (Join-Path $TelemetryRoot "perf-engine-log.jsonl")
    perf_markers = (Join-Path $TelemetryRoot "perf-markers.jsonl")
    event_timeline = (Join-Path $TelemetryRoot "event-timeline.jsonl")
  }
  hitches = [ordered] @{
    count_250ms = @($hitches | Where-Object { (To-Double $_.frame_ms) -ge 250 }).Count
    count_1000ms = @($hitches | Where-Object { (To-Double $_.frame_ms) -ge 1000 }).Count
    count_2000ms = @($hitches | Where-Object { (To-Double $_.frame_ms) -ge 2000 }).Count
    max_frame_ms = [math]::Round($maxFrameMs, 3)
    by_route_phase = @($routePhaseHitches | Select-Object -First 30)
  }
  sections = [ordered] @{
    count = $sections.Count
    top_by_total_ms = @($sectionSummary | Sort-Object total_ms -Descending | Select-Object -First 20)
    top_by_max_ms = @($sectionSummary | Sort-Object max_ms -Descending | Select-Object -First 20)
  }
  engine = [ordered] @{
    sample_count = $engine.Count
    warning_bursts = $engineWarningBursts
  }
  writer = [ordered] @{
    max_queue_depth = [int] $maxWriterQueue
    max_dropped_rows = [int] $maxDroppedRows
  }
  marker_count = $markers.Count
  event_count = $events.Count
}

$summaryPath = Join-Path $telemetryOut "perf-summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

$timelineRows = [System.Collections.Generic.List[object]]::new()
foreach ($row in $hitches) {
  $timelineRows.Add([pscustomobject] [ordered] @{
      timestamp_utc = $row.timestamp_utc
      type = "hitch"
      detail = "frame_ms=$(To-Double $row.frame_ms) level=$($row.hitch_level) state=$($row.route_state) stop=$($row.route_stop_id) phase=$($row.route_phase)"
    })
}
foreach ($row in $sections) {
  $timelineRows.Add([pscustomobject] [ordered] @{
      timestamp_utc = $row.timestamp_utc
      type = "section"
      detail = "elapsed_ms=$(To-Double $row.elapsed_ms) section=$($row.section) state=$($row.route_state) stop=$($row.route_stop_id) phase=$($row.route_phase)"
    })
}
foreach ($row in $engine) {
  $warnings = To-Double $row.warnings_since_last_sample
  if ($warnings -gt 0) {
    $timelineRows.Add([pscustomobject] [ordered] @{
        timestamp_utc = $row.timestamp_utc
        type = "engine-warning-burst"
        detail = "warnings=$warnings latest=$($row.latest_engine_log_type): $($row.latest_engine_log_message)"
      })
  }
}
foreach ($row in $markers) {
  $timelineRows.Add([pscustomobject] [ordered] @{
      timestamp_utc = $row.timestamp_utc
      type = "marker"
      detail = "$($row.label) state=$($row.route_state) stop=$($row.route_stop_id) phase=$($row.route_phase)"
    })
}

$timelinePath = Join-Path $rawOut "perf-timeline.md"
$timeline = [System.Text.StringBuilder]::new()
[void] $timeline.AppendLine("# NetworkSense Perf Timeline")
[void] $timeline.AppendLine("")
[void] $timeline.AppendLine("- generated_utc: $([DateTime]::UtcNow.ToString("o"))")
[void] $timeline.AppendLine("- telemetry_root: $TelemetryRoot")
[void] $timeline.AppendLine("- session_id: $SessionId")
[void] $timeline.AppendLine("")
foreach ($row in ($timelineRows | Sort-Object timestamp_utc | Select-Object -First $TimelineLimit)) {
  [void] $timeline.AppendLine("- $($row.timestamp_utc) [$($row.type)] $($row.detail)")
}
$timeline.ToString() | Set-Content -LiteralPath $timelinePath -Encoding UTF8

Write-Host "NetworkSense perf analysis written:"
Write-Host "  $summaryPath"
Write-Host "  $timelinePath"
Write-Host ""
Write-Host ("Session: {0}" -f ($(if ([string]::IsNullOrWhiteSpace($SessionId)) { "<none>" } else { $SessionId })))
Write-Host ("Hitches >=250ms: {0}; >=2000ms: {1}; max frame ms: {2}" -f $summary.hitches.count_250ms, $summary.hitches.count_2000ms, $summary.hitches.max_frame_ms)
