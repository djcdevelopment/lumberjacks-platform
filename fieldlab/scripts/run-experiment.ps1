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

function New-BridgeFeasibilityReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$BridgeSummary
  )

  $gateLines = @($BridgeSummary.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No bridge feasibility gates were recorded.")
  }

  $matrixLines = @($BridgeSummary.decision_matrix | ForEach-Object {
      "- $($_.layer): $($_.status) - $($_.next_gate)"
    })
  if ($matrixLines.Count -eq 0) {
    $matrixLines = @("- No bridge decision matrix rows were recorded.")
  }

  $nextTitle = if ($BridgeSummary.status -eq "pass_local_visual_projection") { "Next Spike" } else { "Next Command" }
  $nextLines = if ($BridgeSummary.status -eq "pass_local_visual_projection") {
    @(
      "Move to shadow movement authority: compare Lumberjacks authoritative position against Valheim local player movement for drift without applying corrections."
    )
  } elseif ($BridgeSummary.status -eq "pass_sidecar_protocol") {
    @(
      "Restart Valheim after installing ComfyNetworkSense 0.4.5, open the console, then run:",
      "",
      '```text',
      "$(Format-PacketValue $BridgeSummary.projection_command)",
      "network_sense_lumberjacks_projection status",
      "network_sense_lumberjacks_projection stop",
      '```',
      "",
      "Then rerun this scenario to capture the projection row."
    )
  } else {
    @(
      "Restart Valheim after installing ComfyNetworkSense 0.4.5, open the console, then run:",
      "",
      '```text',
      "$(Format-PacketValue $BridgeSummary.console_command)",
      '```',
      "",
      "Then rerun this scenario to capture the probe row."
    )
  }

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Determine how far a live Valheim BepInEx client can attach to the Lumberjacks networking runtime before hitting Valheim authority, ZDO replication, or transport boundaries.",
    "",
    "## Approach",
    "",
    "The command plan checked Lumberjacks service health, installed ComfyNetworkSense version, visible Valheim processes, Valheim network surface types, and any rows written by the in-game ``network_sense_lumberjacks_probe`` and ``network_sense_lumberjacks_projection`` commands.",
    "",
    "## Method",
    "",
    "- Gateway: $(Format-PacketValue $BridgeSummary.gateway_ws)",
    "- Region: $(Format-PacketValue $BridgeSummary.region_id)",
    "- NetworkSense log dir: $(Format-PacketValue $BridgeSummary.network_sense_log_dir)",
    "- Probe log: $(Format-PacketValue $BridgeSummary.probe_log_path)",
    "- Projection log: $(Format-PacketValue $BridgeSummary.projection_log_path)",
    "- Installed plugin version: $(Format-PacketValue $BridgeSummary.plugin.version)",
    "- Console command: ``$(Format-PacketValue $BridgeSummary.console_command)``",
    "- Projection command: ``$(Format-PacketValue $BridgeSummary.projection_command)``",
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $BridgeSummary.status)",
    "- Gateway health: $(Format-PacketValue $BridgeSummary.health.gateway.ok)",
    "- Bridge probe rows: $(Format-PacketValue $BridgeSummary.bridge_probe.observed_rows)",
    "- Latest bridge probe status: $(Format-PacketValue $BridgeSummary.bridge_probe.latest.status)",
    "- Projection rows: $(Format-PacketValue $BridgeSummary.projection.observed_rows)",
    "- Latest projection status: $(Format-PacketValue $BridgeSummary.projection.latest.status)",
    "- Latest projection proxy count: $(Format-PacketValue $BridgeSummary.projection.latest.proxy_count)",
    "- Latest projection updates: $(Format-PacketValue $BridgeSummary.projection.latest.entity_updates_received)",
    "- Valheim processes visible: $(Format-PacketValue $BridgeSummary.valheim_process_count)",
    "",
    "## Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Decision Matrix",
    ""
  ) + $matrixLines + @(
    "",
    "## Interpretation",
    "",
    "A ``pass_sidecar_protocol`` result proves only that the live Valheim plugin process can speak the Lumberjacks Gateway protocol as a side-channel client. A ``pass_local_visual_projection`` result additionally proves local-only Unity proxy markers were populated from Lumberjacks rows. Neither status proves Valheim ZDO transport replacement, Valheim physics authority replacement, Steam/PlayFab socket replacement, or a Valheim dedicated server running on Lumberjacks.",
    "",
    "## $nextTitle",
    ""
  ) + $nextLines
}

function New-BridgeFeasibilityPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$BridgeSummary
  )

  return @(
    "# Valheim To Lumberjacks Bridge Feasibility Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet staged the Valheim-to-Lumberjacks bridge proof. Lumberjacks Gateway health is $(Format-PacketValue $BridgeSummary.health.gateway.ok), installed ComfyNetworkSense is $(Format-PacketValue $BridgeSummary.plugin.version), latest bridge probe status is $(Format-PacketValue $BridgeSummary.bridge_probe.latest.status), and latest projection status is $(Format-PacketValue $BridgeSummary.projection.latest.status).",
    "",
    "## Run In Game",
    "",
    '```text',
    "$(Format-PacketValue $BridgeSummary.console_command)",
    "$(Format-PacketValue $BridgeSummary.projection_command)",
    "network_sense_lumberjacks_projection status",
    '```',
    "",
    "## Output",
    "",
    "- ``telemetry/bridge-feasibility-summary.json``",
    "- ``telemetry/valheim-network-surface.json``",
    "- ``raw/operator-runbook.md``",
    "- ``raw/valheim-console-commands.txt``",
    "",
    "## Scope",
    "",
    "This packet is explicitly scoped to side-channel protocol reachability and local-only projection. ZDO replication, physics authority, Steam/PlayFab sockets, and dedicated-server replacement require separate proofs."
  )
}

function New-ShadowAuthorityReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$ShadowSummary
  )

  $gateLines = @($ShadowSummary.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No shadow authority gates were recorded.")
  }

  $matrixLines = @($ShadowSummary.decision_matrix | ForEach-Object {
      "- $($_.layer): $($_.status) - $($_.next_gate)"
    })
  if ($matrixLines.Count -eq 0) {
    $matrixLines = @("- No shadow authority matrix rows were recorded.")
  }

  $nextLines = if ($ShadowSummary.status -eq "pass_shadow_movement_observed") {
    @("Run a repeatable movement route and compare drift distribution before considering bounded correction experiments.")
  } else {
    @(
      "Restart Valheim after installing ComfyNetworkSense 0.4.6, open the console, then run:",
      "",
      '```text',
      "$(Format-PacketValue $ShadowSummary.shadow_command)",
      "network_sense_lumberjacks_shadow status",
      "network_sense_lumberjacks_shadow stop",
      '```',
      "",
      "Move the local player for 60-120 seconds between start and status so drift samples can accumulate."
    )
  }

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Measure whether Lumberjacks can run authoritative movement in parallel with live Valheim local-player motion without applying corrections.",
    "",
    "## Approach",
    "",
    "The command plan checked Lumberjacks health, installed ComfyNetworkSense version, visible Valheim processes, prior bridge/projection evidence, and rows written by ``network_sense_lumberjacks_shadow``.",
    "",
    "## Method",
    "",
    "- Gateway: $(Format-PacketValue $ShadowSummary.gateway_ws)",
    "- Region: $(Format-PacketValue $ShadowSummary.region_id)",
    "- NetworkSense log dir: $(Format-PacketValue $ShadowSummary.network_sense_log_dir)",
    "- Shadow log: $(Format-PacketValue $ShadowSummary.shadow_log_path)",
    "- Installed plugin version: $(Format-PacketValue $ShadowSummary.plugin.version)",
    "- Shadow command: ``$(Format-PacketValue $ShadowSummary.shadow_command)``",
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $ShadowSummary.status)",
    "- Gateway health: $(Format-PacketValue $ShadowSummary.health.gateway.ok)",
    "- Shadow rows: $(Format-PacketValue $ShadowSummary.shadow.observed_rows)",
    "- Latest shadow status: $(Format-PacketValue $ShadowSummary.shadow.latest.status)",
    "- Inputs sent: $(Format-PacketValue $ShadowSummary.shadow.latest.inputs_sent)",
    "- Self authority updates: $(Format-PacketValue $ShadowSummary.shadow.latest.self_authority_updates)",
    "- Drift samples: $(Format-PacketValue $ShadowSummary.shadow.latest.drift_samples)",
    "- Last drift meters: $(Format-PacketValue $ShadowSummary.shadow.latest.last_drift_meters)",
    "- Max drift meters: $(Format-PacketValue $ShadowSummary.shadow.latest.max_drift_meters)",
    "- Average drift meters: $(Format-PacketValue $ShadowSummary.shadow.latest.average_drift_meters)",
    "- Errors: $(Format-PacketValue $ShadowSummary.shadow.latest.errors)",
    "- Valheim processes visible: $(Format-PacketValue $ShadowSummary.valheim_process_count)",
    "",
    "## Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Decision Matrix",
    ""
  ) + $matrixLines + @(
    "",
    "## Interpretation",
    "",
    "A ``pass_shadow_movement_observed`` result means Valheim-derived inputs reached Lumberjacks, authoritative self updates came back, and drift samples were recorded. It does not prove that transform corrections can be safely applied to Valheim, and it does not replace ZDO replication.",
    "",
    "## Next Step",
    ""
  ) + $nextLines
}

function New-ShadowAuthorityPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$ShadowSummary
  )

  return @(
    "# Valheim To Lumberjacks Shadow Authority Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet staged the shadow movement authority proof. Installed ComfyNetworkSense is $(Format-PacketValue $ShadowSummary.plugin.version), latest shadow status is $(Format-PacketValue $ShadowSummary.shadow.latest.status), drift samples are $(Format-PacketValue $ShadowSummary.shadow.latest.drift_samples), and max drift is $(Format-PacketValue $ShadowSummary.shadow.latest.max_drift_meters) meters.",
    "",
    "## Run In Game",
    "",
    '```text',
    "$(Format-PacketValue $ShadowSummary.shadow_command)",
    "network_sense_lumberjacks_shadow status",
    "network_sense_lumberjacks_shadow stop",
    '```',
    "",
    "## Output",
    "",
    "- ``telemetry/shadow-authority-summary.json``",
    "- ``raw/operator-runbook.md``",
    "- ``raw/valheim-console-commands.txt``",
    "",
    "## Scope",
    "",
    "This packet is explicitly scoped to shadow movement measurement. It does not apply Valheim transform corrections, write ZDOs, replace sockets, or prove dedicated-server replacement."
  )
}

function New-ShadowRouteReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$ShadowRouteSummary
  )

  $gateLines = @($ShadowRouteSummary.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No shadow route gates were recorded.")
  }

  $perStopLines = @($ShadowRouteSummary.observed.per_stop | ForEach-Object {
      "- $($_.id): rows=$($_.row_count), inputs=$($_.inputs_sent), self_updates=$($_.self_authority_updates), drift_samples=$($_.drift_samples), max_drift_m=$($_.max_drift_meters), errors=$($_.errors)"
    })
  if ($perStopLines.Count -eq 0) {
    $perStopLines = @("- No per-stop drift rows were recorded.")
  }

  $nextLines = if ($ShadowRouteSummary.status -eq "pass_shadow_route_observed") {
    @("Compare open-control drift against dense/extreme drift and decide whether bounded local-only correction experiments are worth staging.")
  } else {
    @(
      "Restart Valheim after installing ComfyNetworkSense 0.4.9, load Era16, keep the game foregrounded, then run:",
      "",
      '```text',
      "$(Format-PacketValue $ShadowRouteSummary.shadow_route_command)",
      '```',
      "",
      "After the route completes, rerun this scenario to capture the per-stop drift distribution."
    )
  }

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Collect repeatable per-density Lumberjacks shadow movement drift by reusing the existing Era16 teleport route.",
    "",
    "## Approach",
    "",
    "The command plan generated or reused ``teleport-route.tsv``, copied it into the NetworkSense config folder, checked Lumberjacks health and installed mod version, then scanned ``lumberjacks-shadow.jsonl`` for route-tagged rows written by ``network_sense_lumberjacks_shadow_route``.",
    "",
    "## Method",
    "",
    "- Gateway: $(Format-PacketValue $ShadowRouteSummary.gateway_ws)",
    "- Region: $(Format-PacketValue $ShadowRouteSummary.region_id)",
    "- Movement profile: $(Format-PacketValue $ShadowRouteSummary.movement_profile)",
    "- Shadow input Hz: $(Format-PacketValue $ShadowRouteSummary.shadow_input_hz)",
    "- NetworkSense log dir: $(Format-PacketValue $ShadowRouteSummary.network_sense_log_dir)",
    "- Route source: $(Format-PacketValue $ShadowRouteSummary.route_source)",
    "- Route file: ``telemetry/teleport-route.tsv``",
    "- Shadow log: $(Format-PacketValue $ShadowRouteSummary.shadow_log_path)",
    "- Installed plugin version: $(Format-PacketValue $ShadowRouteSummary.plugin.version)",
    "- Shadow route command: ``$(Format-PacketValue $ShadowRouteSummary.shadow_route_command)``",
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $ShadowRouteSummary.status)",
    "- Gateway health: $(Format-PacketValue $ShadowRouteSummary.health.gateway.ok)",
    "- Route steps: $(Format-PacketValue $ShadowRouteSummary.route_step_count)",
    "- Route file copied: $(Format-PacketValue $ShadowRouteSummary.route_file.copied)",
    "- Shadow route rows: $(Format-PacketValue $ShadowRouteSummary.observed.shadow_route_rows)",
    "- Route stop rows: $(Format-PacketValue $ShadowRouteSummary.observed.route_stop_rows)",
    "- Successful stops: $(Format-PacketValue $ShadowRouteSummary.observed.successful_stop_count)/$(Format-PacketValue $ShadowRouteSummary.route_step_count)",
    "- Route end markers: $(Format-PacketValue $ShadowRouteSummary.observed.route_end_markers)",
    "- Valheim processes visible: $(Format-PacketValue $ShadowRouteSummary.valheim_process_count)",
    "",
    "## Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Per-Stop Drift",
    ""
  ) + $perStopLines + @(
    "",
    "## Interpretation",
    "",
    "A ``pass_shadow_route_observed`` result means every route stop produced route-tagged Lumberjacks shadow rows with sent inputs, authoritative self updates, drift samples, zero errors, and a route completion marker. It still does not prove that transform corrections can be safely applied to Valheim or that ZDO replication has been replaced.",
    "",
    "## Next Step",
    ""
  ) + $nextLines
}

function New-ShadowRoutePublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$ShadowRouteSummary
  )

  return @(
    "# Valheim Lumberjacks Shadow Route Drift Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet stages or verifies the route-backed Lumberjacks shadow movement proof. Installed ComfyNetworkSense is $(Format-PacketValue $ShadowRouteSummary.plugin.version), route steps are $(Format-PacketValue $ShadowRouteSummary.route_step_count), route-tagged shadow rows are $(Format-PacketValue $ShadowRouteSummary.observed.shadow_route_rows), and successful stops are $(Format-PacketValue $ShadowRouteSummary.observed.successful_stop_count)/$(Format-PacketValue $ShadowRouteSummary.route_step_count).",
    "",
    "## Run In Game",
    "",
    '```text',
    "$(Format-PacketValue $ShadowRouteSummary.shadow_route_command)",
    '```',
    "",
    "## Output",
    "",
    "- ``telemetry/shadow-route-summary.json``",
    "- ``telemetry/teleport-route.tsv``",
    "- ``raw/operator-runbook.md``",
    "- ``raw/valheim-console-commands.txt``",
    "",
    "## Scope",
    "",
    "This packet is explicitly scoped to route-backed shadow movement measurement. It does not apply Valheim transform corrections, write ZDOs, replace sockets, or prove dedicated-server replacement."
  )
}

function New-PriorityLoadReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$PrioritySummary
  )

  $gateLines = @($PrioritySummary.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No priority load gates were recorded.")
  }

  $perStopLines = @($PrioritySummary.observed.per_stop | ForEach-Object {
      "- $($_.id): samples=$($_.sample_count), candidates=$($_.candidate_count), emitted=$($_.emitted_object_count), portal=$($_.portal_count), structural=$($_.structural_anchor_count), interactive=$($_.near_interactive_count), storage=$($_.storage_crafting_count), max_scan_ms=$($_.max_scan_duration_ms)"
    })
  if ($perStopLines.Count -eq 0) {
    $perStopLines = @("- No per-stop priority rows were recorded.")
  }

  $nextLines = if ($PrioritySummary.status -eq "pass_priority_route_observed" -or $PrioritySummary.status -eq "pass_priority_route_observed_with_sparse_gap") {
    @(
      "Build the Lumberjacks mirror phase: send this priority manifest as ordered side-channel metadata and compare delivery/order under load.",
      "",
      "$(if ($PrioritySummary.status -eq 'pass_priority_route_observed_with_sparse_gap') { 'Carry forward the sparse fixture note: one route stop had no non-player priority objects even at 256m.' } else { '' })",
      "",
      "MCP marker:",
      "",
      '```text',
      "$(Format-PacketValue $PrioritySummary.mcp_phase.next_marker_command)",
      '```'
    )
  } elseif ($PrioritySummary.status -eq "priority_route_observed_visibility_gaps") {
    @(
      "The 96m route captured all stops, but some fixture centers only saw the local player. Run the wider-radius follow-up to decide whether those are true empty local views or route-center/radius artifacts:",
      "",
      '```text',
      "$(Format-PacketValue $PrioritySummary.wide_priority_route_command)",
      "$(Format-PacketValue $PrioritySummary.mcp_phase.next_marker_command)",
      '```',
      "",
      "After that route completes, rerun this scenario to compare the wider manifest."
    )
  } else {
    @(
      "Restart Valheim after installing ComfyNetworkSense 0.5.0, load Era16, keep the game foregrounded, then run:",
      "",
      '```text',
      "$(Format-PacketValue $PrioritySummary.priority_route_command)",
      '```',
      "",
      "After the route completes, rerun this scenario to collect the priority manifest."
    )
  }

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Collect a per-density priority/load-order manifest from loaded Valheim objects, using Lumberjacks as the future ordered side-channel rather than replacing vanilla replication.",
    "",
    "## Approach",
    "",
    "The command plan generated or reused ``teleport-route.tsv``, copied it into the NetworkSense config folder, checked the installed mod version, then scanned ``priority-load.jsonl`` for route-tagged rows written by ``network_sense_lumberjacks_priority_route``.",
    "",
    "## Method",
    "",
    "- Priority radius: $(Format-PacketValue $PrioritySummary.priority_radius_meters)m",
    "- Scan interval: $(Format-PacketValue $PrioritySummary.priority_scan_interval_seconds)s",
    "- Max object rows per sample: $(Format-PacketValue $PrioritySummary.priority_max_objects_per_sample)",
    "- Observed route radius: $(Format-PacketValue $PrioritySummary.observed_route_run.radius_meters)m",
    "- Observed route completed: $(Format-PacketValue $PrioritySummary.observed_route_run.completed)",
    "- NetworkSense log dir: $(Format-PacketValue $PrioritySummary.network_sense_log_dir)",
    "- Route source: $(Format-PacketValue $PrioritySummary.route_source)",
    "- Route file: ``telemetry/teleport-route.tsv``",
    "- Priority log: $(Format-PacketValue $PrioritySummary.priority_log_path)",
    "- Installed plugin version: $(Format-PacketValue $PrioritySummary.plugin.version)",
    "- Priority route command: ``$(Format-PacketValue $PrioritySummary.priority_route_command)``",
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $PrioritySummary.status)",
    "- Phase: $(Format-PacketValue $PrioritySummary.phase)",
    "- Route steps: $(Format-PacketValue $PrioritySummary.route_step_count)",
    "- Route file copied: $(Format-PacketValue $PrioritySummary.route_file.copied)",
    "- Priority sample rows: $(Format-PacketValue $PrioritySummary.observed.sample_rows)",
    "- Priority object rows: $(Format-PacketValue $PrioritySummary.observed.object_rows)",
    "- Observed stops: $(Format-PacketValue $PrioritySummary.observed.observed_stop_count)/$(Format-PacketValue $PrioritySummary.route_step_count)",
    "- Route end markers: $(Format-PacketValue $PrioritySummary.observed.route_end_markers)",
    "- Max scan duration: $(Format-PacketValue $PrioritySummary.observed.max_scan_duration_ms)ms",
    "- Collider buffer saturated: $(Format-PacketValue $PrioritySummary.observed.collider_buffer_full_any)",
    "",
    "## Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Per-Stop Priority",
    ""
  ) + $perStopLines + @(
    "",
    "## Interpretation",
    "",
    "A ``pass_priority_route_observed`` result means every route stop produced a local priority manifest with tier counts and a completion marker. It does not prove true vanilla network arrival order or Lumberjacks side-channel delivery yet.",
    "",
    "## Next Step",
    ""
  ) + $nextLines
}

function New-PriorityLoadPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$PrioritySummary
  )

  return @(
    "# Valheim Lumberjacks Priority Load Order Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet stages or verifies the Lumberjacks priority/load-order manifest proof. Installed ComfyNetworkSense is $(Format-PacketValue $PrioritySummary.plugin.version), route steps are $(Format-PacketValue $PrioritySummary.route_step_count), priority sample rows are $(Format-PacketValue $PrioritySummary.observed.sample_rows), and observed stops are $(Format-PacketValue $PrioritySummary.observed.observed_stop_count)/$(Format-PacketValue $PrioritySummary.route_step_count).",
    "",
    "## Run In Game",
    "",
    '```text',
    "$(Format-PacketValue $PrioritySummary.priority_route_command)",
    '```',
    "",
    "## Output",
    "",
    "- ``telemetry/priority-load-summary.json``",
    "- ``telemetry/priority-load-density-comparison.csv``",
    "- ``telemetry/teleport-route.tsv``",
    "- ``raw/operator-runbook.md``",
    "- ``raw/valheim-console-commands.txt``",
    "",
    "## Scope",
    "",
    "This packet is scoped to local manifest observation. It does not write ZDOs, alter ZNetView ownership, correct transforms, replace vanilla replication, or prove Lumberjacks delivery ordering yet."
  )
}

function New-PriorityMirrorReportLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$MirrorSummary
  )

  $gateLines = @($MirrorSummary.gates | ForEach-Object {
      "- $($_.id): $($_.status) - $($_.observed)"
    })
  if ($gateLines.Count -eq 0) {
    $gateLines = @("- No priority mirror gates were recorded.")
  }

  $stopLines = @($MirrorSummary.route_stops | ForEach-Object {
      $tierText = @($_.priority_tiers | ForEach-Object { "$($_.tier)=$($_.count)" }) -join ", "
      "- $($_.route_stop_id): objects=$($_.object_rows), tiers=$tierText"
    })
  if ($stopLines.Count -eq 0) {
    $stopLines = @("- No mirrored route-stop rows were recorded.")
  }

  return @(
    "# Field Notes: $ScenarioName",
    "",
    "## Goal",
    "",
    "Mirror the collected Valheim priority/load-order manifest into Lumberjacks EventLog as ordered side-channel metadata.",
    "",
    "## Approach",
    "",
    "The command plan read the latest priority-load packet, bounded object rows to the latest sample per route stop, posted typed events to Lumberjacks EventLog, and queried them back by event type.",
    "",
    "## Method",
    "",
    "- EventLog: $(Format-PacketValue $MirrorSummary.eventlog_url)",
    "- Postgres: $(Format-PacketValue $MirrorSummary.postgres.host):$(Format-PacketValue $MirrorSummary.postgres.port), ok=$(Format-PacketValue $MirrorSummary.postgres.ok)",
    "- Manifest id: $(Format-PacketValue $MirrorSummary.manifest_id)",
    "- Priority source: $(Format-PacketValue $MirrorSummary.priority_summary_path)",
    "- Priority status: $(Format-PacketValue $MirrorSummary.priority_status)",
    "- Priority route completed: $(Format-PacketValue $MirrorSummary.priority_route_completed)",
    "- Manifest: ``telemetry/priority-mirror-manifest.json``",
    "- Events CSV: ``telemetry/priority-mirror-events.csv``",
    "",
    "## Results",
    "",
    "- Packet status: $Status",
    "- Scenario telemetry status: $(Format-PacketValue $MirrorSummary.status)",
    "- Phase: $(Format-PacketValue $MirrorSummary.phase)",
    "- Expected events: $(Format-PacketValue $MirrorSummary.manifest_counts.expected_event_count)",
    "- Mirrored sample rows: $(Format-PacketValue $MirrorSummary.manifest_counts.mirrored_sample_rows)",
    "- Mirrored object rows: $(Format-PacketValue $MirrorSummary.manifest_counts.mirrored_object_rows)",
    "- Posted ok/failed: $(Format-PacketValue $MirrorSummary.posts.posted_ok)/$(Format-PacketValue $MirrorSummary.posts.posted_failed)",
    "- Queried samples: $(Format-PacketValue $MirrorSummary.query.sample_events)",
    "- Queried object batches: $(Format-PacketValue $MirrorSummary.query.object_batch_events)",
    "- Queried object records: $(Format-PacketValue $MirrorSummary.query.object_events)",
    "- Queried completions: $(Format-PacketValue $MirrorSummary.query.complete_events)",
    "- Object sequence preserved: $(Format-PacketValue $MirrorSummary.query.object_sequence_set_preserved)",
    "",
    "## Gates",
    ""
  ) + $gateLines + @(
    "",
    "## Mirrored Stops",
    ""
  ) + $stopLines + @(
    "",
    "## Interpretation",
    "",
    "A passing mirror packet proves Lumberjacks EventLog can carry the ordered priority manifest in per-stop batches and return it with sequence integrity. It does not yet prove live in-game streaming or Gateway dual-channel broadcast of Valheim object metadata.",
    "",
    "## Next Step",
    "",
    "Build the live mirror or Gateway dual-channel load phase, using the marker below as the phase handoff:",
    "",
    '```text',
    "$(Format-PacketValue $MirrorSummary.mcp_phase.next_marker_command)",
    '```'
  )
}

function New-PriorityMirrorPublishLines {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioName,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$Status,

    [Parameter(Mandatory = $true)]
    [object]$MirrorSummary
  )

  return @(
    "# Valheim Lumberjacks Priority Mirror Packet",
    "",
    "Run: $RunId",
    "Scenario: $ScenarioName",
    "Status: $Status",
    "",
    "## Summary",
    "",
    "The packet mirrored $(Format-PacketValue $MirrorSummary.manifest_counts.mirrored_sample_rows) priority samples and $(Format-PacketValue $MirrorSummary.manifest_counts.mirrored_object_rows) priority objects into Lumberjacks EventLog. Posted events were $(Format-PacketValue $MirrorSummary.posts.posted_ok)/$(Format-PacketValue $MirrorSummary.manifest_counts.expected_event_count), queried object batches were $(Format-PacketValue $MirrorSummary.query.object_batch_events), queried object records were $(Format-PacketValue $MirrorSummary.query.object_events), and object sequence preservation was $(Format-PacketValue $MirrorSummary.query.object_sequence_set_preserved).",
    "",
    "## Output",
    "",
    "- ``telemetry/priority-mirror-summary.json``",
    "- ``telemetry/priority-mirror-manifest.json``",
    "- ``telemetry/priority-mirror-events.csv``",
    "- ``raw/operator-runbook.md``",
    "",
    "## Scope",
    "",
    "This packet proves EventLog side-channel carriage of the ordered manifest in per-stop batches. It does not stream live from BepInEx, change vanilla replication, or exercise the Gateway datagram channel yet."
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
$bridgeFeasibilitySummary = $null
$shadowAuthoritySummary = $null
$shadowRouteSummary = $null
$priorityLoadSummary = $null
$priorityMirrorSummary = $null
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

$bridgeFeasibilitySummaryPath = Join-Path $runDir "telemetry\bridge-feasibility-summary.json"
if (Test-Path $bridgeFeasibilitySummaryPath) {
  try {
    $bridgeFeasibilitySummary = Get-Content -Raw $bridgeFeasibilitySummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $bridgeFeasibilitySummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/bridge-feasibility-summary.json."
  }
}

$shadowAuthoritySummaryPath = Join-Path $runDir "telemetry\shadow-authority-summary.json"
if (Test-Path $shadowAuthoritySummaryPath) {
  try {
    $shadowAuthoritySummary = Get-Content -Raw $shadowAuthoritySummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $shadowAuthoritySummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/shadow-authority-summary.json."
  }
}

$shadowRouteSummaryPath = Join-Path $runDir "telemetry\shadow-route-summary.json"
if (Test-Path $shadowRouteSummaryPath) {
  try {
    $shadowRouteSummary = Get-Content -Raw $shadowRouteSummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $shadowRouteSummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/shadow-route-summary.json."
  }
}

$priorityLoadSummaryPath = Join-Path $runDir "telemetry\priority-load-summary.json"
if (Test-Path $priorityLoadSummaryPath) {
  try {
    $priorityLoadSummary = Get-Content -Raw $priorityLoadSummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $priorityLoadSummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/priority-load-summary.json."
  }
}

$priorityMirrorSummaryPath = Join-Path $runDir "telemetry\priority-mirror-summary.json"
if (Test-Path $priorityMirrorSummaryPath) {
  try {
    $priorityMirrorSummary = Get-Content -Raw $priorityMirrorSummaryPath | ConvertFrom-Json
    if (-not $scenarioTelemetryStatus) {
      $scenarioTelemetryStatus = $priorityMirrorSummary.status
    }
  } catch {
    $notes += "Could not parse telemetry/priority-mirror-summary.json."
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
} elseif ($bridgeFeasibilitySummary) {
  New-BridgeFeasibilityReportLines -ScenarioName $scenarioName -Status $status -BridgeSummary $bridgeFeasibilitySummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-BridgeFeasibilityPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -BridgeSummary $bridgeFeasibilitySummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($shadowRouteSummary) {
  New-ShadowRouteReportLines -ScenarioName $scenarioName -Status $status -ShadowRouteSummary $shadowRouteSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-ShadowRoutePublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -ShadowRouteSummary $shadowRouteSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($priorityLoadSummary) {
  New-PriorityLoadReportLines -ScenarioName $scenarioName -Status $status -PrioritySummary $priorityLoadSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-PriorityLoadPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -PrioritySummary $priorityLoadSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($priorityMirrorSummary) {
  New-PriorityMirrorReportLines -ScenarioName $scenarioName -Status $status -MirrorSummary $priorityMirrorSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-PriorityMirrorPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -MirrorSummary $priorityMirrorSummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "publish.md")
} elseif ($shadowAuthoritySummary) {
  New-ShadowAuthorityReportLines -ScenarioName $scenarioName -Status $status -ShadowSummary $shadowAuthoritySummary |
    Set-Content -Encoding UTF8 (Join-Path $runDir "report.md")

  New-ShadowAuthorityPublishLines -ScenarioName $scenarioName -RunId $runId -Status $status -ShadowSummary $shadowAuthoritySummary |
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
