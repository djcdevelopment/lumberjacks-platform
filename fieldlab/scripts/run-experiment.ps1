param(
  [Parameter(Mandatory = $true)]
  [string]$ScenarioPath,

  [string]$RunRoot = "fieldlab\runs",

  [string]$CommandPlanPath,

  [switch]$SkipCommandPlan,

  [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"

function Format-PacketValue {
  param([object]$Value)

  if ($null -eq $Value) {
    return "n/a"
  }

  if ($Value -is [bool]) {
    return $Value.ToString().ToLowerInvariant()
  }

  return [string]$Value
}

function New-GenericReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status
  )

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "See ``experiment.yaml``.",
    "",
    "## Approach",
    "",
    "Initial packet created. Fill in scenario-specific execution after the first manual run.",
    "",
    "## Method",
    "",
    "Environment captured in ``environment.json``.",
    "",
    "## Results",
    "",
    "Status: $Status",
    "",
    "## Next Step",
    "",
    "Add concrete commands to ``commands.ps1``, run them, and update ``results.json``."
  )
}

function New-RuntimeReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$RuntimeSummary
  )

  $services = @($RuntimeSummary.services)
  $probes = @($RuntimeSummary.probes)
  $loadSummary = $RuntimeSummary.load_summary

  $serviceLines = @($services | ForEach-Object {
      $ready = if ($_.readiness -and $_.readiness.ready) { "ready" } else { "not ready" }
      $statusCode = if ($_.readiness) { Format-PacketValue $_.readiness.status_code } else { "n/a" }
      "- $($_.name): $ready (HTTP $statusCode) at $($_.health)"
    })
  if ($serviceLines.Count -eq 0) {
    $serviceLines = @("- No service records were written.")
  }

  $probeLines = @($probes | ForEach-Object {
      $timeout = if ($_.timed_out) { "timed out" } else { "completed" }
      "- $($_.name): exit $($_.exit_code), $timeout; stdout ``$($_.stdout)``"
    })
  if ($probeLines.Count -eq 0) {
    $probeLines = @("- No probe records were written.")
  }

  $postgresMode = Format-PacketValue $RuntimeSummary.infrastructure.postgres_mode
  $postgresHost = Format-PacketValue $RuntimeSummary.infrastructure.postgres_host
  $postgresPort = Format-PacketValue $RuntimeSummary.infrastructure.postgres_port
  $telemetryStatus = Format-PacketValue $RuntimeSummary.status
  $lumberjacksPath = Format-PacketValue $RuntimeSummary.lumberjacks_path
  $dotnetPath = Format-PacketValue $RuntimeSummary.prerequisites.dotnet_path
  $nodePath = Format-PacketValue $RuntimeSummary.prerequisites.node_path
  $npmPath = Format-PacketValue $RuntimeSummary.prerequisites.npm_path
  $dockerAvailable = Format-PacketValue $RuntimeSummary.prerequisites.docker_available

  $loadLines = @("- No dual-channel load summary was recorded.")
  if ($loadSummary) {
    $loadLines = @(
      "- Channel result: $(Format-PacketValue $loadSummary.channel_result)",
      "- UDP inputs sent: $(Format-PacketValue $loadSummary.udp_inputs_sent)",
      "- WebSocket entity updates: $(Format-PacketValue $loadSummary.ws_entity_updates)",
      "- UDP entity updates: $(Format-PacketValue $loadSummary.udp_entity_updates)",
      "- Errors: $(Format-PacketValue $loadSummary.errors)",
      "- Disconnects reported by load script: $(Format-PacketValue $loadSummary.disconnects)"
    )
  }

  $interpretation = if ($RuntimeSummary.status -eq "pass") {
    "The native backend accepted the authoritative structure loop, persisted and projected it through the services, handled five concurrent players, and moved entity updates over the UDP datagram channel during load."
  } else {
    "The packet did not pass. Use the raw command and service logs to find the first failing gate before expanding scope."
  }

  $lines = @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Boot the Lumberjacks native runtime locally, prove the reliable fallback path and enhanced datagram path are observable, then capture a reproducible field packet.",
    "",
    "## Approach",
    "",
    "The runner copied the scenario command plan to ``commands.ps1``. The plan checked prerequisites, attached to Postgres, started EventLog, Progression, OperatorApi, and Gateway, waited for health endpoints, then ran the existing Lumberjacks Node probes.",
    "",
    "## Method",
    "",
    "- Lumberjacks repo: $lumberjacksPath",
    "- .NET: $dotnetPath",
    "- Node: $nodePath",
    "- npm: $npmPath",
    "- Docker available: $dockerAvailable",
    "- Postgres: $postgresMode at $($postgresHost):$postgresPort",
    "- Runtime telemetry: ``telemetry/runtime-summary.json``",
    "- Raw logs: ``raw/``",
    "",
    "## Service Health",
    ""
  )

  $lines += $serviceLines
  $lines += @(
    "",
    "## Existing Probes",
    ""
  )
  $lines += $probeLines
  $lines += @(
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $telemetryStatus"
  )
  $lines += $loadLines
  $lines += @(
    "",
    "## Interpretation",
    "",
    $interpretation,
    "",
    "## Risks",
    "",
    "- This is local workstation evidence, not LAN, cloud, or impaired-network evidence.",
    "- The command plan does not reset the Postgres database, so raw world/event counts can include earlier runs.",
    "- The load script reports bot disconnects during intentional teardown; use exit code, errors, and channel result as the pass gate.",
    "- A passing packet proves the current smoke gate, not sustained 50/100 bot load or real rendered-client performance.",
    "",
    "## Next Experiment",
    "",
    "Rerun against a clean database, then extend to 50/100 bot load or a LAN client comparison."
  )

  return $lines
}

function Format-PacketList {
  param([object]$Value)

  $items = @($Value)
  if ($items.Count -eq 0) {
    return "n/a"
  }

  return ($items | ForEach-Object { Format-PacketValue $_ }) -join ", "
}

function New-MatrixReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$MatrixSummary
  )

  $fixtureLines = @($MatrixSummary.density_fixtures | ForEach-Object {
      "- $($_.band): $($_.build_zdos_500m) build ZDOs, $($_.total_zdos_500m) total ZDOs at ($($_.world_x), $($_.world_z))"
    })
  if ($fixtureLines.Count -eq 0) {
    $fixtureLines = @("- No density fixture summary was recorded.")
  }

  $actorPlayers = Format-PacketList $MatrixSummary.axes.actor_players
  $ranges = Format-PacketList $MatrixSummary.axes.observer_ranges
  $rtt = Format-PacketList $MatrixSummary.axes.rtt_ms
  $processMs = Format-PacketList $MatrixSummary.axes.server_process_ms
  $profiles = Format-PacketList $MatrixSummary.axes.event_profiles

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Convert the parsed Era16 world cache into a deterministic density and pressure matrix for the next Lumberjacks runtime load packets.",
    "",
    "## Approach",
    "",
    "The command plan queried the StewardView DuckDB cache, selected real 500m build-density cells, and crossed those fixtures with player count, observer range, RTT, server process-time budget, and event-profile axes.",
    "",
    "## Method",
    "",
    "- Cache: $(Format-PacketValue $MatrixSummary.cache_path)",
    "- Snapshot: $(Format-PacketValue $MatrixSummary.snapshot_id)",
    "- Fixtures: ``telemetry/era16-density-fixtures.json``",
    "- Matrix CSV: ``telemetry/era16-pressure-matrix.csv``",
    "- Summary: ``telemetry/matrix-summary.json``",
    "",
    "## Matrix Axes",
    "",
    "- Actor players: $actorPlayers",
    "- Observer ranges: $ranges",
    "- RTT ms: $rtt",
    "- Server process ms: $processMs",
    "- Event profiles: $profiles",
    "",
    "## Density Fixtures",
    ""
  ) + $fixtureLines + @(
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $MatrixSummary.status)",
    "- Density fixtures: $(Format-PacketValue $MatrixSummary.density_fixture_count)",
    "- Matrix rows: $(Format-PacketValue $MatrixSummary.matrix_row_count)",
    "",
    "## Interpretation",
    "",
    "This packet is a pressure-contract generator, not a live runtime load result. It turns real save density into reproducible load targets so the next packet can test whether reliable gameplay events remain protected while near, mid, far, and low-priority detail are thinned under pressure.",
    "",
    "## Next Experiment",
    "",
    "Use the generated CSV to seed a runtime executor that drives 25, 50, and 100 actor-player cases through the Lumberjacks gateway and records observed UDP/WebSocket delivery, process time, and drop behavior."
  )
}

function New-VolunteerReadinessReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$ReadinessSummary
  )

  $gateLines = @($ReadinessSummary.readiness.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No readiness gates were recorded.")
  }

  $readyInvite = Format-PacketValue $ReadinessSummary.readiness.ready_to_invite_volunteers
  $readyRehearsal = Format-PacketValue $ReadinessSummary.readiness.ready_for_internal_rehearsal
  $headline = $ReadinessSummary.headline_metrics

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Establish baseline client, server, telemetry, and uptime signals before inviting volunteer players.",
    "",
    "## Approach",
    "",
    "The command plan inspected host hardware, WSL state, Valheim process/port visibility, NetworkSense telemetry files, benchmark results, and the latest Era16 density matrix packet. It then evaluated explicit readiness gates.",
    "",
    "## Method",
    "",
    "- NetworkSense log dir: $(Format-PacketValue $ReadinessSummary.log_dir)",
    "- Summary: ``telemetry/volunteer-readiness-summary.json``",
    "- Metrics: ``telemetry/baseline-metrics.json``",
    "- Setup inventory: ``raw/setup-inventory.json``",
    "",
    "## Decision",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $ReadinessSummary.status)",
    "- Ready to invite volunteers: $readyInvite",
    "- Ready for internal rehearsal: $readyRehearsal",
    "- Failed gates: $(Format-PacketValue $ReadinessSummary.readiness.fail_count)",
    "- Warning gates: $(Format-PacketValue $ReadinessSummary.readiness.warn_count)",
    "",
    "## Headline Metrics",
    "",
    "- Client samples: $(Format-PacketValue $headline.client_samples)",
    "- Longest continuous session: $(Format-PacketValue $headline.longest_session_minutes) minutes",
    "- Latest sample age: $(Format-PacketValue $headline.latest_sample_age_minutes) minutes",
    "- FPS average: $(Format-PacketValue $headline.fps_avg)",
    "- Frame p95: $(Format-PacketValue $headline.frame_time_p95_ms) ms",
    "- RTT p95: $(Format-PacketValue $headline.rtt_p95_ms) ms",
    "- Jitter p95: $(Format-PacketValue $headline.jitter_p95_ms) ms",
    "- Max nearby players/entities/build pieces: $(Format-PacketValue $headline.max_nearby_players)/$(Format-PacketValue $headline.max_nearby_entities)/$(Format-PacketValue $headline.max_nearby_build_pieces)",
    "- Benchmarks: $(Format-PacketValue $headline.benchmark_count), latest tier $(Format-PacketValue $headline.latest_benchmark_tier)",
    "- Server pulses: $(Format-PacketValue $headline.server_pulse_count)",
    "- Density matrix rows: $(Format-PacketValue $headline.density_matrix_rows)",
    "- Teleport route steps: $(Format-PacketValue $headline.teleport_route_steps)",
    "- Resource profile: $(Format-PacketValue $headline.resource_profile)",
    "",
    "## Readiness Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Interpretation",
    "",
    "This packet is the pre-volunteer scheduling gate. A pass means invite time can focus on validating multiplayer pressure and immersion; warnings or failures identify the rehearsal work still needed before asking people to help.",
    "",
    "## Next Experiment",
    "",
    "Run a live internal rehearsal with the WSL/server setup active, record route markers and benchmarks at selected density fixtures, then rerun this packet before sending volunteer invites."
  )
}

function New-TeleportRehearsalReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$RehearsalSummary
  )

  $gateLines = @($RehearsalSummary.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No rehearsal gates were recorded.")
  }

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Stage or verify the internal Valheim teleport rehearsal across the Era16 density route.",
    "",
    "## Approach",
    "",
    "The command plan consumed the latest teleport route contract, generated ``teleport-route.tsv`` for ComfyNetworkSense, copied it into the NetworkSense config folder when available, checked local gateway/game state, and scanned telemetry for route completion markers.",
    "",
    "## Method",
    "",
    "- Resource profile: $(Format-PacketValue $RehearsalSummary.resource_profile)",
    "- NetworkSense log dir: $(Format-PacketValue $RehearsalSummary.network_sense_log_dir)",
    "- Route contract: $(Format-PacketValue $RehearsalSummary.route_contract_path)",
    "- Route file: ``telemetry/teleport-route.tsv``",
    "- Runbook: ``raw/operator-runbook.md``",
    "- Console commands: ``raw/valheim-console-commands.txt``",
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $RehearsalSummary.status)",
    "- Route steps: $(Format-PacketValue $RehearsalSummary.route_step_count)",
    "- Route file copied: $(Format-PacketValue $RehearsalSummary.route_file.copied)",
    "- Plugin DLL exists: $(Format-PacketValue $RehearsalSummary.mod.plugin_dll_exists)",
    "- NetworkSense config profile: $(Format-PacketValue $RehearsalSummary.network_sense_config.profile)",
    "- NetworkSense config changes: $(Format-PacketValue @($RehearsalSummary.network_sense_config.changes).Count)",
    "- Gateway OK: $(Format-PacketValue $RehearsalSummary.gateway.ok)",
    "- Valheim processes: $(Format-PacketValue $RehearsalSummary.valheim.process_count)",
    "- Valheim UDP listeners: $(Format-PacketValue $RehearsalSummary.valheim.udp_listener_count)",
    "- Completed stops: $(Format-PacketValue $RehearsalSummary.observed.completed_stop_count)/$(Format-PacketValue $RehearsalSummary.route_step_count)",
    "- Benchmark results total: $(Format-PacketValue $RehearsalSummary.observed.benchmark_results_total)",
    "",
    "## Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Next Command",
    "",
    "In Valheim, after loading the Era16 test world with the rebuilt mod:",
    "",
    '```text',
    "network_sense_route_run teleport-route.tsv",
    '```',
    "",
    "After the route completes, rerun this scenario and then rerun the volunteer readiness baseline."
  )
}

function New-GenericPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status
  )

  return @(
    "# Publish Packet: $ScenarioName",
    "",
    "Status: draft.",
    "",
    "This packet is not publishable until scenario-specific commands, metrics, and pass/fail gates are",
    "filled in.",
    "",
    "Run status: $Status"
  )
}

function New-RuntimePublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$RuntimeSummary
  )

  $loadSummary = $RuntimeSummary.load_summary
  $channelResult = if ($loadSummary) { Format-PacketValue $loadSummary.channel_result } else { "n/a" }
  $udpInputs = if ($loadSummary) { Format-PacketValue $loadSummary.udp_inputs_sent } else { "n/a" }
  $udpUpdates = if ($loadSummary) { Format-PacketValue $loadSummary.udp_entity_updates } else { "n/a" }
  $wsUpdates = if ($loadSummary) { Format-PacketValue $loadSummary.ws_entity_updates } else { "n/a" }
  $errors = if ($loadSummary) { Format-PacketValue $loadSummary.errors } else { "n/a" }
  $postgresMode = Format-PacketValue $RuntimeSummary.infrastructure.postgres_mode
  $postgresHost = Format-PacketValue $RuntimeSummary.infrastructure.postgres_host
  $postgresPort = Format-PacketValue $RuntimeSummary.infrastructure.postgres_port

  $summary = if ($RuntimeSummary.status -eq "pass") {
    "The local Lumberjacks native runtime smoke gate passed: all four services became healthy, the existing vertical-slice and multiplayer probes exited 0, and the dual-channel load probe observed UDP datagram entity updates."
  } else {
    "The local Lumberjacks native runtime smoke gate did not pass. Treat this packet as an internal failure artifact until the first failing gate is resolved."
  }

  return @(
    "# Lumberjacks Native Runtime Smoke Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    $summary,
    "",
    "## What Ran",
    "",
    "- EventLog, Progression, OperatorApi, and Gateway were started from ``C:\work\Lumberjacks``.",
    "- Postgres mode: $postgresMode at $($postgresHost):$postgresPort.",
    "- Existing probes: ``scripts/test-vertical-slice.js``, ``scripts/test-multiplayer.js``, and ``scripts/load-test-dual-channel.js``.",
    "",
    "## Key Metrics",
    "",
    "- Channel result: $channelResult",
    "- UDP inputs sent: $udpInputs",
    "- UDP entity updates: $udpUpdates",
    "- WebSocket entity updates: $wsUpdates",
    "- Errors: $errors",
    "",
    "## Reproduce",
    "",
    '```powershell',
    ".\fieldlab\scripts\run-experiment.ps1 .\fieldlab\scenarios\$ScenarioName.yaml",
    '```',
    "",
    "## Caveats",
    "",
    "- Local workstation result only; LAN and impaired-network behavior still need separate packets.",
    "- The database is not reset by the smoke gate, so raw event/world counts may include previous local runs.",
    "- Bot disconnect counts in the load log include intentional teardown."
  )
}

function New-MatrixPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$MatrixSummary
  )

  return @(
    "# Era16 Density Pressure Matrix Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet generated a deterministic load-target matrix from the Era16 StewardView cache: $(Format-PacketValue $MatrixSummary.density_fixture_count) density fixtures crossed with player count, observer range, RTT, server process-time, and event-profile axes for $(Format-PacketValue $MatrixSummary.matrix_row_count) rows.",
    "",
    "## Outputs",
    "",
    "- ``telemetry/era16-density-fixtures.json``",
    "- ``telemetry/era16-pressure-matrix.csv``",
    "- ``telemetry/matrix-summary.json``",
    "",
    "## Reproduce",
    "",
    '```powershell',
    ".\fieldlab\scripts\run-experiment.ps1 .\fieldlab\scenarios\$ScenarioName.yaml",
    '```',
    "",
    "## Caveats",
    "",
    "- This packet models event pressure targets; it does not yet start Lumberjacks services or measure live packet delivery.",
    "- Density comes from the local StewardView DuckDB cache at ``$(Format-PacketValue $MatrixSummary.cache_path)``.",
    "- The next packet should replay selected rows through the gateway and compare observed channel behavior against these expectations."
  )
}

function New-VolunteerReadinessPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$ReadinessSummary
  )

  $headline = $ReadinessSummary.headline_metrics

  return @(
    "# Valheim Era16 Volunteer Readiness Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Decision",
    "",
    "- Ready to invite volunteers: $(Format-PacketValue $ReadinessSummary.readiness.ready_to_invite_volunteers)",
    "- Ready for internal rehearsal: $(Format-PacketValue $ReadinessSummary.readiness.ready_for_internal_rehearsal)",
    "- Failed gates: $(Format-PacketValue $ReadinessSummary.readiness.fail_count)",
    "- Warning gates: $(Format-PacketValue $ReadinessSummary.readiness.warn_count)",
    "",
    "## Baseline Signals",
    "",
    "- Client samples: $(Format-PacketValue $headline.client_samples)",
    "- Longest continuous session: $(Format-PacketValue $headline.longest_session_minutes) minutes",
    "- Frame p95: $(Format-PacketValue $headline.frame_time_p95_ms) ms",
    "- RTT p95: $(Format-PacketValue $headline.rtt_p95_ms) ms",
    "- Jitter p95: $(Format-PacketValue $headline.jitter_p95_ms) ms",
    "- Benchmarks: $(Format-PacketValue $headline.benchmark_count), latest tier $(Format-PacketValue $headline.latest_benchmark_tier)",
    "- Density matrix rows: $(Format-PacketValue $headline.density_matrix_rows)",
    "- Teleport route steps: $(Format-PacketValue $headline.teleport_route_steps)",
    "- Resource profile: $(Format-PacketValue $headline.resource_profile)",
    "",
    "## Outputs",
    "",
    "- ``telemetry/volunteer-readiness-summary.json``",
    "- ``telemetry/baseline-metrics.json``",
    "- ``telemetry/teleport-route-contract.json``",
    "- ``raw/setup-inventory.json``",
    "",
    "## Reproduce",
    "",
    '```powershell',
    ".\fieldlab\scripts\run-experiment.ps1 .\fieldlab\scenarios\$ScenarioName.yaml",
    '```',
    "",
    "## Caveats",
    "",
    "- This is a pre-volunteer gate, not a substitute for a live multiplayer test.",
    "- Server process visibility is only meaningful during an internal rehearsal run.",
    "- Invite only after failed gates are resolved and any warnings are accepted intentionally."
  )
}

function New-TeleportRehearsalPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$RehearsalSummary
  )

  return @(
    "# Valheim Era16 Teleport Rehearsal Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet staged the NetworkSense one-command rehearsal for $(Format-PacketValue $RehearsalSummary.route_step_count) Era16 density stops under resource profile ``$(Format-PacketValue $RehearsalSummary.resource_profile)``.",
    "",
    "## Current State",
    "",
    "- Route file copied: $(Format-PacketValue $RehearsalSummary.route_file.copied)",
    "- NetworkSense config profile: $(Format-PacketValue $RehearsalSummary.network_sense_config.profile)",
    "- Gateway OK: $(Format-PacketValue $RehearsalSummary.gateway.ok)",
    "- Valheim processes: $(Format-PacketValue $RehearsalSummary.valheim.process_count)",
    "- Completed stops: $(Format-PacketValue $RehearsalSummary.observed.completed_stop_count)/$(Format-PacketValue $RehearsalSummary.route_step_count)",
    "",
    "## Run In Game",
    "",
    '```text',
    "network_sense_rehearsal teleport-route.tsv $(Format-PacketValue $RehearsalSummary.resource_profile)",
    '```',
    "",
    "For a zero-touch Docker lab, run:",
    "",
    '```powershell',
    ".\fieldlab\scripts\run-autonomous-valheim-lab.ps1 -Clients 1 -Profile $(Format-PacketValue $RehearsalSummary.resource_profile) -Start",
    '```',
    "",
    "## Outputs",
    "",
    "- ``telemetry/teleport-rehearsal-summary.json``",
    "- ``telemetry/teleport-route.tsv``",
    "- ``raw/operator-runbook.md``",
    "- ``raw/valheim-console-commands.txt``"
  )
}

if (-not (Test-Path $ScenarioPath)) {
  throw "Scenario not found: $ScenarioPath"
}

$repoRoot = (Resolve-Path ".").Path
$scenarioName = [System.IO.Path]::GetFileNameWithoutExtension($ScenarioPath)
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runId = "$timestamp-$scenarioName"
$runDir = Join-Path $RunRoot $runId

New-Item -ItemType Directory -Force $runDir | Out-Null
New-Item -ItemType Directory -Force (Join-Path $runDir "raw") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $runDir "telemetry") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $runDir "captures") | Out-Null

Copy-Item $ScenarioPath (Join-Path $runDir "experiment.yaml") -Force

$commandsPath = Join-Path $runDir "commands.ps1"
$fieldlabRoot = Split-Path -Parent $PSScriptRoot
$defaultCommandPlanPath = Join-Path (Join-Path $fieldlabRoot "command-plans") "$scenarioName.ps1"
$selectedCommandPlanPath = $null

if ($CommandPlanPath) {
  $selectedCommandPlanPath = $CommandPlanPath
} elseif (Test-Path $defaultCommandPlanPath) {
  $selectedCommandPlanPath = $defaultCommandPlanPath
}

if ($selectedCommandPlanPath -and -not (Test-Path $selectedCommandPlanPath)) {
  throw "Command plan not found: $selectedCommandPlanPath"
}

if ($selectedCommandPlanPath) {
  Copy-Item $selectedCommandPlanPath $commandsPath -Force
} else {
  @(
    "# Commands for $runId",
    "# Add scenario-specific commands here as automation matures."
  ) | Set-Content -Encoding UTF8 $commandsPath
}

& "$PSScriptRoot\collect-environment.ps1" -OutPath (Join-Path $runDir "environment.json")

$commandPlan = [ordered]@{
  path = $selectedCommandPlanPath
  copied_to = "commands.ps1"
  skipped = [bool]$SkipCommandPlan
  exit_code = $null
  stdout = $null
  stderr = $null
}

if ($selectedCommandPlanPath -and -not $SkipCommandPlan) {
  $stdoutPath = Join-Path $runDir "raw\command-plan.out.log"
  $stderrPath = Join-Path $runDir "raw\command-plan.err.log"
  $runDirFull = (Resolve-Path $runDir).Path
  $commandPlanExitCode = $null

  $previousKeepRunning = $env:FIELDLAB_KEEP_RUNNING
  $previousMsbuildDisableNodeReuse = $env:MSBUILDDISABLENODEREUSE
  $env:FIELDLAB_KEEP_RUNNING = if ($KeepRunning) { "1" } else { "0" }
  $env:MSBUILDDISABLENODEREUSE = "1"

  try {
    $commandPlanArguments = @(
      "-NoProfile",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      $commandsPath,
      "-RunDir",
      $runDirFull,
      "-RepoRoot",
      $repoRoot
    )

    & "powershell.exe" @commandPlanArguments > $stdoutPath 2> $stderrPath
    $commandPlanExitCode = $LASTEXITCODE
  } finally {
    if ($null -eq $previousKeepRunning) {
      Remove-Item Env:FIELDLAB_KEEP_RUNNING -ErrorAction SilentlyContinue
    } else {
      $env:FIELDLAB_KEEP_RUNNING = $previousKeepRunning
    }

    if ($null -eq $previousMsbuildDisableNodeReuse) {
      Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    } else {
      $env:MSBUILDDISABLENODEREUSE = $previousMsbuildDisableNodeReuse
    }
  }

  $commandPlan.exit_code = $commandPlanExitCode
  $commandPlan.stdout = "raw/command-plan.out.log"
  $commandPlan.stderr = "raw/command-plan.err.log"
}

$status = "packet_created"
$notes = @("Initial runner created the run packet skeleton.")

if ($selectedCommandPlanPath -and -not $SkipCommandPlan) {
  if ($commandPlan.exit_code -eq 0) {
    $status = "command_plan_passed"
    $notes += "Command plan completed successfully."
  } else {
    $status = "command_plan_failed"
    $notes += "Command plan failed. Inspect raw/command-plan.err.log."
  }
} elseif ($selectedCommandPlanPath -and $SkipCommandPlan) {
  $status = "command_plan_skipped"
  $notes += "Command plan was copied but skipped by request."
} else {
  $notes += "No scenario command plan was found."
}

$runtimeSummary = $null
$matrixSummary = $null
$volunteerReadinessSummary = $null
$teleportRehearsalSummary = $null
$scenarioTelemetryStatus = $null
$runtimeSummaryPath = Join-Path $runDir "telemetry\runtime-summary.json"
if (Test-Path $runtimeSummaryPath) {
  try {
    $runtimeSummary = Get-Content -Raw $runtimeSummaryPath | ConvertFrom-Json
    $scenarioTelemetryStatus = $runtimeSummary.status
  } catch {
    $notes += "Could not parse telemetry/runtime-summary.json."
  }
}

$matrixSummaryPath = Join-Path $runDir "telemetry\matrix-summary.json"
if (Test-Path $matrixSummaryPath) {
  try {
    $matrixSummary = Get-Content -Raw $matrixSummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $matrixSummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/matrix-summary.json."
  }
}

$volunteerReadinessSummaryPath = Join-Path $runDir "telemetry\volunteer-readiness-summary.json"
if (Test-Path $volunteerReadinessSummaryPath) {
  try {
    $volunteerReadinessSummary = Get-Content -Raw $volunteerReadinessSummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $volunteerReadinessSummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/volunteer-readiness-summary.json."
  }
}

$teleportRehearsalSummaryPath = Join-Path $runDir "telemetry\teleport-rehearsal-summary.json"
if (Test-Path $teleportRehearsalSummaryPath) {
  try {
    $teleportRehearsalSummary = Get-Content -Raw $teleportRehearsalSummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $teleportRehearsalSummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/teleport-rehearsal-summary.json."
  }
}

if ($scenarioTelemetryStatus) {
  $notes += "Scenario telemetry status: $scenarioTelemetryStatus."
  if ($scenarioTelemetryStatus -ne "pass") {
    $status = $scenarioTelemetryStatus
  }
}

$results = [ordered]@{
  schema_version = 1
  run_id = $runId
  scenario = $scenarioName
  status = $status
  created_at_utc = (Get-Date).ToUniversalTime().ToString("o")
  command_plan = $commandPlan
  notes = $notes
  artifacts = @{
    experiment = "experiment.yaml"
    environment = "environment.json"
    commands = "commands.ps1"
    raw = "raw/"
    telemetry = "telemetry/"
    captures = "captures/"
  }
}

$results | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $runDir "results.json")

if ($runtimeSummary) {
  New-RuntimeReportLines -ScenarioName $scenarioName -Status $status -RuntimeSummary $runtimeSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-RuntimePublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -RuntimeSummary $runtimeSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($matrixSummary) {
  New-MatrixReportLines -ScenarioName $scenarioName -Status $status -MatrixSummary $matrixSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-MatrixPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -MatrixSummary $matrixSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($volunteerReadinessSummary) {
  New-VolunteerReadinessReportLines -ScenarioName $scenarioName -Status $status -ReadinessSummary $volunteerReadinessSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-VolunteerReadinessPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -ReadinessSummary $volunteerReadinessSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($teleportRehearsalSummary) {
  New-TeleportRehearsalReportLines -ScenarioName $scenarioName -Status $status -RehearsalSummary $teleportRehearsalSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-TeleportRehearsalPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -RehearsalSummary $teleportRehearsalSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} else {
  New-GenericReportLines -ScenarioName $scenarioName -Status $status |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-GenericPublishLines -ScenarioName $scenarioName -Status $status |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
}

@(
  "# Quick Start: $scenarioName",
  "",
  '```powershell',
  ".\fieldlab\scripts\run-experiment.ps1 .\fieldlab\scenarios\$scenarioName.yaml",
  '```'
) | Set-Content -Encoding UTF8 (Join-Path $runDir "quickstart.md")

$validationPath = Join-Path $runDir "validation.json"
& "$PSScriptRoot\validate-run-packet.ps1" -RunDir $runDir -AllowMissingSignature -OutPath $validationPath | Out-Null
$validation = Get-Content -Raw $validationPath | ConvertFrom-Json
$results.validation = @{
  status = $validation.status
  path = "validation.json"
}
$results | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $runDir "results.json")

$experimentHash = (Get-FileHash (Join-Path $runDir "experiment.yaml") -Algorithm SHA256).Hash
$environmentHash = (Get-FileHash (Join-Path $runDir "environment.json") -Algorithm SHA256).Hash
$resultsHash = (Get-FileHash (Join-Path $runDir "results.json") -Algorithm SHA256).Hash
$validationHash = (Get-FileHash (Join-Path $runDir "validation.json") -Algorithm SHA256).Hash

$signature = [ordered]@{
  schema_version = 1
  run_id = $runId
  timestamp_utc = (Get-Date).ToUniversalTime().ToString("o")
  machine = $env:COMPUTERNAME
  operator = $env:USERNAME
  scenario_path = $ScenarioPath
  hashes = @{
    experiment_yaml = $experimentHash
    environment_json = $environmentHash
    results_json = $resultsHash
    validation_json = $validationHash
  }
  keep_running = [bool]$KeepRunning
}

$signature | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $runDir "signature.json")

Write-Host "Created fieldlab run packet: $runDir"
