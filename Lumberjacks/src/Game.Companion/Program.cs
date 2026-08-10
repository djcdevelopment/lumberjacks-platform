using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumberjacks.Companion;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<GatewayClient>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<CompanionStateStore>();
builder.Services.AddSingleton<ValheimLocator>();
builder.Services.AddSingleton<QuestPackPublisher>();
builder.Services.AddSingleton<ModpackInstaller>();
builder.Services.AddHttpClient<TransportTruthCaptureService>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<WorkbenchStore>();
builder.Services.AddSingleton<QuestStudioService>();
builder.Services.AddSingleton<WorkbenchJobStore>();
builder.Services.AddSingleton<WorkbenchRegistry>();
builder.Services.AddSingleton<WorkbenchService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store, max-age=0";
    await next();
});

WorkbenchEndpoints.Map(app);

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "lumberjacks-companion" }));
app.MapGet("/api/v0/companion/status", (CompanionStateStore state, ValheimLocator locator) =>
{
    var install = locator.Find();
    var saved = state.Read();
    CompanionConfig.TryReadCredentials(install is null ? null : ValheimLocator.ConfigPath(install), out var discovered);
    var profile = saved.profile ?? (discovered is null ? null : new CompanionProfile(discovered.enrollment_id, null));
    return Results.Ok(new
    {
        schema_version = 1,
        companion_version = CompanionVersion.Value,
        gateway_url = GatewayClient.GatewayUrl,
        valheim = new
        {
            found = install is not null,
            path = install,
            running = ValheimLocator.IsRunning(),
            config_found = install is not null && File.Exists(ValheimLocator.ConfigPath(install)),
        },
        profile = new
        {
            linked = profile is not null,
            enrollment_id_hash = HashShort(profile?.enrollment_id),
            linked_utc = profile?.linked_utc,
        },
        installed = saved.installed,
        last_error = saved.last_error,
    });
});

app.MapGet("/api/v0/companion/workbench", async (CompanionStateStore state, ValheimLocator locator, GatewayClient gateway, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var install = locator.Find();
    var saved = state.Read();
    var deployment = await gateway.GetJson("/api/v0/telemetry/deployment", cancellationToken);
    var catalog = WorkbenchCatalog.Read();
    var latestCapture = capture.ListCaptures(1).FirstOrDefault();
    return Results.Json(new
    {
        schema_version = 1,
        event_type = "companion.workbench_status",
        generated_utc = DateTimeOffset.UtcNow,
        source = new
        {
            companion_version = CompanionVersion.Value,
            bootstrap_release = CompanionVersion.BootstrapRelease,
            source_revision = CompanionVersion.SourceRevision,
            source_branch = CompanionVersion.SourceBranch,
            source_dirty = CompanionVersion.SourceDirty,
            image = CompanionVersion.Image,
            gateway_url = GatewayClient.GatewayUrl,
        },
        gateway_reachable = deployment is not null,
        local_status = new
        {
            valheim = new
            {
                found = install is not null,
                config_found = install is not null && File.Exists(ValheimLocator.ConfigPath(install)),
                running = ValheimLocator.IsRunning(),
            },
            profile = new { linked = saved.profile is not null },
            installed = saved.installed is null ? null : new
            {
                saved.installed.release,
                saved.installed.mod_release,
                installed_package_sha256_short = HashShort(saved.installed.package_sha256),
                saved.installed.installed_utc,
            },
            last_error = saved.last_error,
        },
        recent_capture_exists = latestCapture is not null,
        latest_capture = latestCapture,
        catalog,
    }, Json.Options);
});

app.MapPost("/api/v0/companion/workbench/snapshot", (CompanionStateStore state, ValheimLocator locator) =>
{
    var snapshot = WorkbenchCatalog.Snapshot(WorkbenchCatalog.Read(), state.Read(), locator.Find());
    var path = WorkbenchCatalog.WriteSnapshot(state, snapshot);
    return Results.Ok(new
    {
        ok = true,
        event_type = "companion.workbench_snapshot",
        generated_utc = DateTimeOffset.UtcNow,
        snapshot_name = Path.GetFileName(path),
    });
});

app.MapGet("/api/v0/companion/workbench/snapshot/latest", (CompanionStateStore state) =>
{
    var path = WorkbenchCatalog.LatestSnapshot(state);
    return path is null
        ? Results.NotFound(new { error = "workbench_snapshot_not_found" })
        : Results.File(path, "application/json", Path.GetFileName(path));
});

app.MapPost("/api/v0/companion/profile/claim-installed", (CompanionStateStore state, ValheimLocator locator) =>
{
    var install = locator.Find();
    if (!CompanionConfig.TryReadCredentials(install is null ? null : ValheimLocator.ConfigPath(install), out var credentials))
        return Results.BadRequest(new { error = "lumberjacks_config_or_credential_missing" });
    var current = state.Read();
    current.profile = new CompanionProfile(credentials!.enrollment_id, DateTime.UtcNow);
    current.last_error = null;
    state.Write(current);
    return Results.Ok(new { ok = true, enrollment_id = current.profile.enrollment_id, linked_utc = current.profile.linked_utc });
});

app.MapGet("/api/v0/companion/update/check", async (GatewayClient gateway, CancellationToken cancellationToken) =>
{
    var manifest = await gateway.GetManifest(cancellationToken);
    return manifest is null
        ? Results.Problem("Gateway did not provide a valid modpack manifest.", statusCode: 502)
        : Results.Ok(manifest);
});

app.MapPost("/api/v0/companion/update/install", async (GameClosedConfirmation? confirmation, GatewayClient gateway, ModpackInstaller installer, CancellationToken cancellationToken) =>
{
    if (confirmation?.game_closed_confirmed != true)
        return Results.BadRequest(InstallResult.Fail("game_closed_confirmation_required"));
    var result = await installer.InstallAsync(gateway, cancellationToken);
    return result.ok ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/v0/companion/update/rollback", (GameClosedConfirmation? confirmation, ModpackInstaller installer) =>
{
    if (confirmation?.game_closed_confirmed != true)
        return Results.BadRequest(InstallResult.Fail("game_closed_confirmation_required"));
    var result = installer.RollbackLatest();
    return result.ok ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/v0/companion/release/check", async (GatewayClient gateway, CancellationToken cancellationToken) =>
{
    CompanionBootstrapManifest? latest = null;
    string? error = null;
    try { latest = await gateway.GetCompanionBootstrapManifest(cancellationToken); }
    catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }

    var local = CompanionVersion.BootstrapRelease;
    var updateAvailable = latest is not null &&
        (string.Equals(local, "unknown", StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(local, latest.release, StringComparison.Ordinal));
    return Results.Ok(new
    {
        schema_version = 1,
        companion_version = CompanionVersion.Value,
        bootstrap_release = local,
        latest_bootstrap = latest,
        update_available = updateAvailable,
        error,
        note = updateAvailable
            ? "A newer public Companion bootstrap is available. Download it, extract over or beside the old bundle, then run Start-LumberjacksCompanion.cmd."
            : "This Companion bootstrap matches the public P7 bootstrap lane.",
    });
});

app.MapGet("/api/v0/companion/diagnostics", async (CompanionStateStore state, ValheimLocator locator, GatewayClient gateway, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var install = locator.Find();
    var saved = state.Read();
    CompanionConfig.TryReadCredentials(install is null ? null : ValheimLocator.ConfigPath(install), out var discovered);
    var profile = saved.profile ?? (discovered is null ? null : new CompanionProfile(discovered.enrollment_id, null));
    var latestModpack = await gateway.GetJson("/api/v0/valheim/modpack/manifest", cancellationToken);
    var latestBootstrap = await gateway.GetJson("/api/v0/companion/bootstrap/manifest", cancellationToken);
    var deployment = await gateway.GetJson("/api/v0/telemetry/deployment", cancellationToken);
    var valheim = await gateway.GetJson("/api/v0/telemetry/valheim", cancellationToken);
    var cutover = await gateway.GetJson("/api/v0/telemetry/cutover", cancellationToken);
    var motion = await gateway.GetJson("/live/valheim-motion", cancellationToken);

    return Results.Json(new
    {
        schema_version = 1,
        generated_utc = DateTimeOffset.UtcNow,
        companion = new
        {
            version = CompanionVersion.Value,
            bootstrap_release = CompanionVersion.BootstrapRelease,
            gateway_url = GatewayClient.GatewayUrl,
        },
        local = new
        {
            valheim_found = install is not null,
            valheim_running = ValheimLocator.IsRunning(),
            config_found = install is not null && File.Exists(ValheimLocator.ConfigPath(install)),
            installed_release = saved.installed is null ? null : new
            {
                saved.installed.release,
                saved.installed.mod_release,
                saved.installed.package_sha256,
                saved.installed.installed_utc,
                changed_file_count = saved.installed.changed_files.Count,
            },
            saved.last_error,
        },
        profile = new
        {
            linked = profile is not null,
            enrollment_id_hash = HashShort(profile?.enrollment_id),
            linked_utc = profile?.linked_utc,
        },
        public_gateway = new
        {
            modpack_manifest = latestModpack,
            companion_bootstrap = latestBootstrap,
            deployment,
            valheim,
            cutover,
            motion,
        },
        recent_captures = capture.ListCaptures(5).Select(c => new
        {
            c.run_id,
            c.label,
            c.started_utc,
            c.finished_utc,
            c.verdict,
            c.sample_count,
            c.bad_sample_count,
            c.max_peers,
            c.motion_received_delta,
            c.observed_players,
            c.counter_ranges,
            c.capture_identity,
            c.interpretation,
            c.first_motion_state,
            c.last_motion_state,
            c.observed_motion_states,
            c.final_motion_websocket_connected,
            c.final_motion_udp_ready,
            c.final_motion_last_error,
        }).ToList(),
    }, Json.Options);
});

app.MapGet("/api/v0/companion/wave0/status", async (CompanionStateStore state, ValheimLocator locator, GatewayClient gateway, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var install = locator.Find();
    var saved = state.Read();
    CompanionConfig.TryReadCredentials(install is null ? null : ValheimLocator.ConfigPath(install), out var discovered);
    var profile = saved.profile ?? (discovered is null ? null : new CompanionProfile(discovered.enrollment_id, null));
    var deployment = await gateway.GetJson("/api/v0/telemetry/deployment", cancellationToken);
    var valheim = await gateway.GetJson("/api/v0/telemetry/valheim", cancellationToken);
    var motion = await gateway.GetJson("/live/valheim-motion", cancellationToken);
    var captures = capture.ListCaptures(3);

    var status = Wave0Status.Build(install, saved, profile, deployment, valheim, motion, captures);
    return Results.Json(status, Json.Options);
});

app.MapGet("/api/v0/companion/wave0/packet", async (CompanionStateStore state, ValheimLocator locator, GatewayClient gateway, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var install = locator.Find();
    var saved = state.Read();
    CompanionConfig.TryReadCredentials(install is null ? null : ValheimLocator.ConfigPath(install), out var discovered);
    var profile = saved.profile ?? (discovered is null ? null : new CompanionProfile(discovered.enrollment_id, null));
    var deployment = await gateway.GetJson("/api/v0/telemetry/deployment", cancellationToken);
    var valheim = await gateway.GetJson("/api/v0/telemetry/valheim", cancellationToken);
    var motion = await gateway.GetJson("/live/valheim-motion", cancellationToken);
    var captures = capture.ListCaptures(3);

    var status = Wave0Status.Build(install, saved, profile, deployment, valheim, motion, captures);
    return Results.Json(Wave0Packet.Build(status), Json.Options);
});

app.MapGet("/api/v0/companion/wave0/packet.md", async (CompanionStateStore state, ValheimLocator locator, GatewayClient gateway, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var install = locator.Find();
    var saved = state.Read();
    CompanionConfig.TryReadCredentials(install is null ? null : ValheimLocator.ConfigPath(install), out var discovered);
    var profile = saved.profile ?? (discovered is null ? null : new CompanionProfile(discovered.enrollment_id, null));
    var deployment = await gateway.GetJson("/api/v0/telemetry/deployment", cancellationToken);
    var valheim = await gateway.GetJson("/api/v0/telemetry/valheim", cancellationToken);
    var motion = await gateway.GetJson("/live/valheim-motion", cancellationToken);
    var captures = capture.ListCaptures(3);

    var status = Wave0Status.Build(install, saved, profile, deployment, valheim, motion, captures);
    return Results.Text(Wave0Packet.BuildMarkdown(status), "text/markdown");
});

app.MapPost("/api/v0/companion/transport-capture", async (TransportCaptureRequest? request, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var duration = Math.Clamp(request?.duration_seconds ?? 60, 5, 300);
    var interval = Math.Clamp(request?.interval_seconds ?? 5, 1, 60);
    var label = string.IsNullOrWhiteSpace(request?.label) ? "companion" : request!.label!;
    var summary = await capture.CaptureAsync(duration, interval, label, cancellationToken);
    return Results.Ok(summary);
});

// Dev-only local control for the installed mod. The command surface is intentionally allow-listed
// and bounded; it is not a general console or shell bridge. The mod consumes the command on its
// Unity main thread and appends a receipt beside its existing NetworkSense JSONL.
app.MapPost("/api/v0/companion/motion-test", (MotionTestRequest? request, ValheimLocator locator) =>
{
    var install = locator.Find();
    if (install is null) return Results.BadRequest(new { error = "valheim_not_found" });
    if (!File.Exists(ValheimLocator.ConfigPath(install))) return Results.BadRequest(new { error = "mod_config_not_found" });

    var action = (request?.action ?? "").Trim().ToLowerInvariant();
    if (action is not ("start" or "stop" or "set_apply")) return Results.BadRequest(new { error = "action_not_allowed" });
    var id = string.IsNullOrWhiteSpace(request?.id) ? "companion-motion" : request!.id!.Trim();
    if (!MotionTestValidation.IsSafeToken(id)) return Results.BadRequest(new { error = "id_not_allowed" });
    var pattern = (request?.pattern ?? "straight_north").Trim().ToLowerInvariant();
    if (action == "start" && pattern is not ("straight_north" or "straight_east" or "stutter_north" or "circle"))
        return Results.BadRequest(new { error = "pattern_not_allowed" });
    if (action == "set_apply" && request?.motion_apply_enabled is null)
        return Results.BadRequest(new { error = "motion_apply_enabled_required" });
    var duration = Math.Clamp(request?.duration_seconds ?? 10, 1, 60);

    var directory = MotionTestFiles.Directory(install);
    Directory.CreateDirectory(directory);
    var commandPath = MotionTestFiles.CommandPath(install);
    var temporary = commandPath + ".tmp";
    var line = action == "set_apply"
        ? string.Join("|", id, action, request!.motion_apply_enabled!.Value ? "true" : "false")
        : string.Join("|", id, action, pattern, duration.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.WriteAllText(temporary, line + Environment.NewLine);
    File.Move(temporary, commandPath, true);
    return Results.Ok(new
    {
        ok = true,
        id,
        action,
        pattern = action == "set_apply" ? null : pattern,
        duration_seconds = action == "set_apply" ? (int?)null : duration,
        motion_apply_enabled = action == "set_apply" ? request!.motion_apply_enabled : null,
        command_path = commandPath
    });
});

app.MapGet("/api/v0/companion/motion-test/status", (ValheimLocator locator) =>
{
    var install = locator.Find();
    if (install is null) return Results.Ok(new { found = false, pending = false, last_receipt = (string?)null });
    var command = MotionTestFiles.CommandPath(install);
    var receipts = MotionTestFiles.ReceiptPath(install);
    var last = File.Exists(receipts) ? File.ReadLines(receipts).LastOrDefault() : null;
    return Results.Ok(new { found = true, pending = File.Exists(command), last_receipt = last });
});

app.MapGet("/api/v0/companion/transport-capture", (TransportTruthCaptureService capture) =>
    Results.Ok(new
    {
        schema_version = 1,
        captures = capture.ListCaptures(10),
    }));

app.MapGet("/api/v0/companion/transport-capture/{runId}/{file}", (string runId, string file, TransportTruthCaptureService capture) =>
{
    if (file.Equals("bundle.zip", StringComparison.OrdinalIgnoreCase))
    {
        var bundle = capture.CreateCaptureBundle(runId);
        return bundle is null
            ? Results.NotFound(new { error = "capture_bundle_not_found" })
            : Results.File(bundle, "application/zip", $"{runId}.zip");
    }

    var path = capture.ResolveCaptureFile(runId, file);
    if (path is null) return Results.NotFound(new { error = "capture_file_not_found" });
    var contentType = file.Equals("samples.jsonl", StringComparison.OrdinalIgnoreCase)
        ? "application/x-ndjson"
        : "application/json";
    return Results.File(path, contentType, fileDownloadName: file);
});

// The existing operator dashboard remains at the stable local URLs. Only read-only GET traffic is
// forwarded; enrollment and mutating Gateway routes intentionally stay on their public origin.
foreach (var path in new[] { "/community", "/roadmap", "/networksense", "/events", "/testing", "/ops/boundary", "/ops/boundary/summary" })
{
    app.MapGet(path, (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
        gateway.ProxyGet(context, path, cancellationToken));
}
app.MapGet("/trace", (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
    gateway.ProxyGetWithFallback(context, "/ops/boundary", "/community", cancellationToken));

app.MapGet("/api/v0/telemetry/{**tail}", (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
    gateway.ProxyGet(context, "/api/v0/telemetry/" + (context.Request.RouteValues["tail"] ?? string.Empty), cancellationToken));
app.MapGet("/live/{**tail}", (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
    gateway.ProxyGet(context, "/live/" + (context.Request.RouteValues["tail"] ?? string.Empty), cancellationToken));

app.MapGet("/workbench", () => Results.Text(WorkbenchV1Page.Html, "text/html", Encoding.UTF8));

app.MapGet("/companion", () => Results.Text(CompanionPage.Html, "text/html", Encoding.UTF8));

app.MapGet("/", () => Results.Text(WorkbenchV1Page.Html, "text/html", Encoding.UTF8));
app.Run();

static string? HashShort(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(hash).ToLowerInvariant()[..12];
}

static class Wave0Status
{
    public static object Build(
        string? install,
        CompanionState saved,
        CompanionProfile? profile,
        JsonElement? deployment,
        JsonElement? valheim,
        JsonElement? motion,
        IReadOnlyList<TransportCaptureSummary> captures)
    {
        var valheimFound = install is not null;
        var configFound = install is not null && File.Exists(ValheimLocator.ConfigPath(install));
        var profileLinked = profile is not null;
        var valheimRunning = ValheimLocator.IsRunning();
        var gatewayReady = deployment is not null;
        var valheimReady = valheim is not null && !Bool(valheim, "stale");
        var motionReady = motion is not null;
        var peers = PeerCount(valheim);
        var players = PlayerNames(valheim);
        var motionReceived = Int(motion, "received");
        var motionRelayed = Int(motion, "relayed_udp") + Int(motion, "relayed_websocket");
        var latestCapture = captures.FirstOrDefault();

        var localReady = valheimFound && configFound && profileLinked;
        string verdict;
        string nextAction;
        string level;
        if (!localReady)
        {
            verdict = "blocked_by_local_setup";
            level = "bad";
            nextAction = "Fix the amber local setup checks first: Valheim folder, ComfyNetworkSense config, and installed profile association.";
        }
        else if (!gatewayReady || !valheimReady || !motionReady)
        {
            verdict = "blocked_by_unreadable_live_telemetry";
            level = "bad";
            nextAction = "Open the Community and Live trace links, then fix the missing P7 telemetry surface before running a live movement gate.";
        }
        else if (peers < 2)
        {
            verdict = "wait_for_two_real_clients";
            level = "wait";
            nextAction = "Join OMEN and i5 to P7, wait for two peers, then run the live gate from OMEN.";
        }
        else if (motionReceived > 0)
        {
            verdict = "motion_evidence_present";
            level = "ok";
            nextAction = "Run or review the Wave 0 live gate receipt and add the visual observation sidecar.";
        }
        else
        {
            verdict = "ready_for_live_gate";
            level = "ok";
            nextAction = "Run the Wave 0 live gate. First pass: OMEN applies and i5 observes; second pass reverses roles.";
        }

        return new
        {
            schema_version = 1,
            generated_utc = DateTimeOffset.UtcNow,
            verdict,
            level,
            next_action = nextAction,
            local = new
            {
                valheim_found = valheimFound,
                config_found = configFound,
                profile_linked = profileLinked,
                valheim_running = valheimRunning,
                installed_release = saved.installed?.release,
                installed_mod_release = saved.installed?.mod_release,
                installed_package_sha256 = saved.installed?.package_sha256,
                enrollment_id_hash = ShortHash(profile?.enrollment_id),
            },
            p7 = new
            {
                gateway_ready = gatewayReady,
                gateway_version = Text(deployment, "lumberjacks_version"),
                valheim_ready = valheimReady,
                valheim_status = Text(valheim, "status") ?? Text(Object(valheim, "heartbeat"), "server_state"),
                peer_count = peers,
                players,
                motion_ready = motionReady,
                motion_received = motionReceived,
                motion_relayed = motionRelayed,
            },
            latest_capture = latestCapture is null ? null : new
            {
                latestCapture.run_id,
                latestCapture.label,
                latestCapture.started_utc,
                latestCapture.verdict,
                latestCapture.max_peers,
                latestCapture.motion_received_delta,
                latestCapture.observed_players,
                latestCapture.interpretation,
            },
            commands = new
            {
                prelive = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Test-Wave0Prelive.ps1 -OutputDirectory captures\wave0-prelive-current",
                wait_live_omen_applies = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient omen -OutputJson captures\wave0-live-gate\result.json",
                wait_live_i5_applies = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Wait-Wave0LiveGate.ps1 -DesiredApplyClient i5 -OutputJson captures\wave0-live-gate-reversal\result.json",
                live_omen_applies = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Start-Wave0LiveGate.ps1 -DesiredApplyClient omen -OutputJson captures\wave0-live-gate\result.json",
                live_i5_applies = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Start-Wave0LiveGate.ps1 -DesiredApplyClient i5 -OutputJson captures\wave0-live-gate-reversal\result.json",
                annotate_omen_applies = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson captures\wave0-live-gate\result.json -ApplyClient omen -ObserveClient i5 -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun no",
                annotate_i5_applies = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Add-Wave0VisualObservation.ps1 -ReceiptJson captures\wave0-live-gate-reversal\result.json -ApplyClient i5 -ObserveClient omen -VisualResult followed_role -StraightMovement smooth -StutterMovement mixed -RoleReversalRun yes",
                seal_visual_evidence = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\Seal-Wave0VisualEvidence.ps1 -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -OutputJson captures\wave0-live-seal\visual-seal.json",
                retain_named_defect = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\wave0\New-Wave0DefectPacket.ps1 -DefectId wave0-visual-proof-not-sealed -DefectKind visual_inconclusive -Summary ""Visual proof could not be sealed; inspect annotations and seal failure."" -FirstReceiptJson captures\wave0-live-gate\result.json -ReversalReceiptJson captures\wave0-live-gate-reversal\result.json -FirstAnnotatedJson captures\wave0-live-gate\result.annotated.json -ReversalAnnotatedJson captures\wave0-live-gate-reversal\result.annotated.json -SealJson captures\wave0-live-seal\visual-seal.json",
            },
        };
    }

    static JsonElement? Object(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    static string? Text(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    static bool Bool(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    static int Int(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property)) return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)) return value;
        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value) ? value : 0;
    }

    static int PeerCount(JsonElement? valheim) =>
        Int(valheim, "peers") != 0 ? Int(valheim, "peers") :
        Int(valheim, "peer_count") != 0 ? Int(valheim, "peer_count") :
        Int(Object(valheim, "heartbeat"), "peer_count");

    static IReadOnlyList<string> PlayerNames(JsonElement? element)
    {
        var array = Array(Object(element, "heartbeat"), "players") ?? Array(element, "players");
        if (array is null) return [];
        var names = new List<string>();
        foreach (var player in array.Value.EnumerateArray())
        {
            var name = player.ValueKind == JsonValueKind.String ? player.GetString() : null;
            name ??= Text(player, "name") ?? Text(player, "player_name") ?? Text(player, "character_name") ?? Text(player, "steam_name") ?? Text(player, "id");
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }

    static JsonElement? Array(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property
            : null;

    static string? ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }
}

static class Wave0Packet
{
    public static object Build(object status)
    {
        var root = JsonSerializer.SerializeToElement(status, Json.Options);
        var commands = Object(root, "commands");
        var local = Object(root, "local");
        var p7 = Object(root, "p7");
        var latestCapture = Object(root, "latest_capture");

        return new
        {
            schema_version = 1,
            generated_utc = DateTimeOffset.UtcNow,
            objective = "Wave 0 two-client apply/observe proof handoff.",
            verdict = Text(root, "verdict"),
            level = Text(root, "level"),
            next_action = Text(root, "next_action"),
            current_state = new
            {
                installed_release = Text(local, "installed_release"),
                installed_mod_release = Text(local, "installed_mod_release"),
                gateway_version = Text(p7, "gateway_version"),
                p7_peer_count = Int(p7, "peer_count"),
                p7_players = Strings(Array(p7, "players")),
                motion_received = Int(p7, "motion_received"),
                motion_relayed = Int(p7, "motion_relayed"),
                latest_capture = latestCapture is null ? null : new
                {
                    run_id = Text(latestCapture, "run_id"),
                    verdict = Text(latestCapture, "verdict"),
                    max_peers = Int(latestCapture, "max_peers"),
                    motion_received_delta = Int(latestCapture, "motion_received_delta"),
                },
            },
            ready_checks = new
            {
                local_profile_and_config_ready = Bool(local, "valheim_found") && Bool(local, "config_found") && Bool(local, "profile_linked"),
                p7_telemetry_readable = Bool(p7, "gateway_ready") && Bool(p7, "valheim_ready") && Bool(p7, "motion_ready"),
                two_real_clients_joined = Int(p7, "peer_count") >= 2,
                recent_evidence_capture_exists = latestCapture is not null,
            },
            required_human_observations = new[]
            {
                "Join OMEN and i5 to P7 with the two player accounts.",
                "Watch both screens during the bounded movement course.",
                "Record whether motion follows the selected apply/observe client rather than the machine/account.",
                "Record straight-run and stutter-step quality as smooth, glidey, teleporting, mixed, or not_tested.",
                "Repeat with roles reversed before sealing the evidence.",
            },
            stop_conditions = new[]
            {
                "Any non-human gate fails.",
                "P7 peer_count stays below 2.",
                "Role preflight does not show exactly one apply-enabled client.",
                "Motion command fails on either Companion.",
                "Visual result does not follow the selected apply/observe role.",
                "Role reversal contradicts the first run.",
            },
            commands = new
            {
                prelive = Text(commands, "prelive"),
                wait_live_omen_applies = Text(commands, "wait_live_omen_applies"),
                wait_live_i5_applies = Text(commands, "wait_live_i5_applies"),
                live_omen_applies = Text(commands, "live_omen_applies"),
                annotate_omen_applies = Text(commands, "annotate_omen_applies"),
                live_i5_applies = Text(commands, "live_i5_applies"),
                annotate_i5_applies = Text(commands, "annotate_i5_applies"),
                seal_visual_evidence = Text(commands, "seal_visual_evidence"),
                retain_named_defect = Text(commands, "retain_named_defect"),
            },
        };
    }

    public static string BuildMarkdown(object status)
    {
        var packet = JsonSerializer.SerializeToElement(Build(status), Json.Options);
        var lines = new List<string>
        {
            "# Wave 0 handoff packet",
            "",
            $"- Generated UTC: {Text(packet, "generated_utc")}",
            $"- Verdict: {Text(packet, "verdict")}",
            $"- Next action: {Text(packet, "next_action")}",
            "",
            "## Current state",
            "",
        };

        var current = Object(packet, "current_state");
        lines.Add($"- Installed release: {Text(current, "installed_release") ?? "unknown"}");
        lines.Add($"- Gateway release: {Text(current, "gateway_version") ?? "unknown"}");
        lines.Add($"- P7 peer count: {Int(current, "p7_peer_count")}");
        var players = string.Join(", ", Strings(Array(current, "p7_players")));
        lines.Add($"- P7 players: {(string.IsNullOrWhiteSpace(players) ? "none" : players)}");
        lines.Add($"- Motion: {Int(current, "motion_received")} received / {Int(current, "motion_relayed")} relayed");
        lines.Add("");
        lines.Add("## Ready checks");
        lines.Add("");
        var checks = Object(packet, "ready_checks");
        foreach (var name in new[] { "local_profile_and_config_ready", "p7_telemetry_readable", "two_real_clients_joined", "recent_evidence_capture_exists" })
            lines.Add($"- [{(Bool(checks, name) ? "x" : " ")}] {name}");
        lines.Add("");
        lines.Add("## Human observations still required");
        lines.Add("");
        foreach (var item in Strings(Array(packet, "required_human_observations")))
            lines.Add($"- {item}");
        lines.Add("");
        lines.Add("## Stop conditions");
        lines.Add("");
        foreach (var item in Strings(Array(packet, "stop_conditions")))
            lines.Add($"- {item}");
        lines.Add("");
        lines.Add("## Commands");
        lines.Add("");
        lines.Add("```powershell");
        var commands = Object(packet, "commands");
        foreach (var name in new[] { "prelive", "wait_live_omen_applies", "live_omen_applies", "annotate_omen_applies", "wait_live_i5_applies", "live_i5_applies", "annotate_i5_applies", "seal_visual_evidence", "retain_named_defect" })
        {
            var command = Text(commands, name);
            if (string.IsNullOrWhiteSpace(command)) continue;
            lines.Add("# " + name);
            lines.Add(command);
            lines.Add("");
        }
        lines.Add("```");
        lines.Add("");
        lines.Add("Original machine receipts remain immutable. Visual observations are sidecars; the seal is a derived index over both directions.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    static JsonElement? Object(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    static JsonElement? Array(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property
            : null;

    static string? Text(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.Null ? null : property.ToString();
    }

    static bool Bool(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    static int Int(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property)) return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)) return value;
        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value) ? value : 0;
    }

    static IReadOnlyList<string> Strings(JsonElement? array)
    {
        if (array is null) return [];
        var values = new List<string>();
        foreach (var item in array.Value.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values;
    }
}

static class CompanionVersion
{
    public static string Value => typeof(CompanionVersion).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public static string BootstrapRelease => Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_BOOTSTRAP_RELEASE") ?? "unknown";
    public static string SourceRevision => Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_SOURCE_REVISION") ?? "unknown";
    public static string SourceBranch => Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_SOURCE_BRANCH") ?? "unknown";
    public static string SourceDirty => Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_SOURCE_DIRTY") ?? "unknown";
    public static string Image => Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_IMAGE") ?? "unknown";
}

sealed class GatewayClient(HttpClient client)
{
    public static string GatewayUrl => (Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_GATEWAY_URL") ?? "https://comfy-p7.duckdns.org").TrimEnd('/');

    public async Task<ModpackManifest?> GetManifest(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(GatewayUrl + "/api/v0/valheim/modpack/manifest", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ModpackManifest>(stream, Json.Options, cancellationToken);
    }

    public async Task<CompanionBootstrapManifest?> GetCompanionBootstrapManifest(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(GatewayUrl + "/api/v0/companion/bootstrap/manifest", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<CompanionBootstrapManifest>(stream, Json.Options, cancellationToken);
    }

    public async Task<JsonElement?> GetJson(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(GatewayUrl + path, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parsed = await JsonSerializer.DeserializeAsync<JsonElement>(stream, Json.Options, cancellationToken);
            return parsed.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetPackage(ModpackCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GatewayUrl + "/api/v0/valheim/modpack/package");
        request.Headers.Add("X-Lumberjacks-Enrollment-Id", credentials.enrollment_id);
        request.Headers.Add("X-Lumberjacks-Client-Key", credentials.client_access_key);
        using var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : null;
    }

    public async Task<IResult> ProxyGet(HttpContext context, string path, CancellationToken cancellationToken)
    {
        var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
        using var response = await client.GetAsync(GatewayUrl + path + query, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        return Results.File(body, contentType, enableRangeProcessing: false, lastModified: null, entityTag: null);
    }

    public async Task<IResult> ProxyGetWithFallback(HttpContext context, string path, string fallbackPath, CancellationToken cancellationToken)
    {
        var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
        using var response = await client.GetAsync(GatewayUrl + path + query, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            using var fallback = await client.GetAsync(GatewayUrl + fallbackPath, cancellationToken);
            var fallbackBody = await fallback.Content.ReadAsByteArrayAsync(cancellationToken);
            var fallbackContentType = fallback.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            return Results.File(fallbackBody, fallbackContentType, enableRangeProcessing: false, lastModified: null, entityTag: null);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        return Results.File(body, contentType, enableRangeProcessing: false, lastModified: null, entityTag: null);
    }
}

sealed class CompanionStateStore
{
    readonly string _path;
    readonly object _lock = new();

    public CompanionStateStore()
    {
        var root = Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumberjacks", "Companion");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "companion-state.json");
    }

    public CompanionState Read()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return new CompanionState();
            try { return JsonSerializer.Deserialize<CompanionState>(File.ReadAllText(_path), Json.Options) ?? new CompanionState(); }
            catch { return new CompanionState { last_error = "local_state_unreadable" }; }
        }
    }

    public void Write(CompanionState state)
    {
        lock (_lock)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json.Options));
            File.Move(temporary, _path, true);
        }
    }

    public string DataDirectory => Path.GetDirectoryName(_path)!;
}

sealed class ValheimLocator
{
    public string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("LUMBERJACKS_VALHEIM_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "valheim.exe"))) return configured;
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Valheim");
        return File.Exists(Path.Combine(defaultPath, "valheim.exe")) ? defaultPath : null;
    }

    public static string ConfigPath(string valheimPath) => Path.Combine(valheimPath, "BepInEx", "config", "djcdevelopment.valheim.comfynetworksense.cfg");
    public static bool IsRunning() => Process.GetProcessesByName("valheim").Length > 0 || Process.GetProcessesByName("valheim_server").Length > 0;
}

static class MotionTestFiles
{
    public static string Directory(string valheimPath) => Path.Combine(valheimPath, "BepInEx", "config", "comfy-network-sense");
    public static string CommandPath(string valheimPath) => Path.Combine(Directory(valheimPath), "companion-motion.command");
    public static string ReceiptPath(string valheimPath) => Path.Combine(Directory(valheimPath), "companion-motion-receipts.jsonl");
}

static class MotionTestValidation
{
    public static bool IsSafeToken(string value) => value.Length <= 80 &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');
}

sealed class ModpackInstaller(CompanionStateStore stateStore, ValheimLocator locator)
{
    readonly SemaphoreSlim _operationGate = new(1, 1);

    public async Task<InstallResult> InstallAsync(GatewayClient gateway, CancellationToken cancellationToken)
    {
        if (!_operationGate.Wait(0)) return InstallResult.Fail("modpack_operation_in_progress");
        try { return await InstallCoreAsync(gateway, cancellationToken); }
        finally { _operationGate.Release(); }
    }

    async Task<InstallResult> InstallCoreAsync(GatewayClient gateway, CancellationToken cancellationToken)
    {
        var valheimPath = locator.Find();
        if (valheimPath is null) return InstallResult.Fail("valheim_not_found");
        if (ValheimLocator.IsRunning()) return InstallResult.Fail("valheim_is_running");
        var configPath = ValheimLocator.ConfigPath(valheimPath);
        if (!CompanionConfig.TryReadCredentials(configPath, out var credentials)) return InstallResult.Fail("lumberjacks_config_or_credential_missing");

        ModpackManifest? manifest;
        try { manifest = await gateway.GetManifest(cancellationToken); }
        catch (Exception ex) { return InstallResult.Fail("manifest_fetch_failed", ex.Message); }
        if (manifest?.package is null || string.IsNullOrWhiteSpace(manifest.package.sha256)) return InstallResult.Fail("manifest_invalid");

        byte[]? package;
        try { package = await gateway.GetPackage(credentials!, cancellationToken); }
        catch (Exception ex) { return InstallResult.Fail("package_download_failed", ex.Message); }
        if (package is null) return InstallResult.Fail("package_download_denied");
        var actualHash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        if (!string.Equals(actualHash, manifest.package.sha256, StringComparison.OrdinalIgnoreCase))
            return InstallResult.Fail("package_hash_mismatch", $"expected {manifest.package.sha256}, got {actualHash}");

        var releaseId = SafeToken(manifest.release ?? manifest.mod_release ?? "unknown");
        var backupRoot = Path.Combine(stateStore.DataDirectory, "backups",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + releaseId + "-" + Guid.NewGuid().ToString("N")[..8]);
        var beforeInstall = stateStore.Read();
        if (string.Equals(beforeInstall.last_error, "local_state_unreadable", StringComparison.Ordinal))
            return InstallResult.Fail("local_state_unreadable");
        var changed = new List<string>();
        var created = new List<string>();
        var applied = new List<AppliedModpackFile>();
        try
        {
            using var stream = new MemoryStream(package, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plans = new List<PlannedModpackFile>();
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                // The modpack carries a root README for humans. It is package metadata, not a
                // Valheim payload; every other file must live under Valheim/ or the complete
                // package fails before the first target byte is changed.
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.Equals(normalized, "README.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = ArchiveRelativePath(normalized);
                if (relative is null) return InstallResult.Fail("package_entry_outside_valheim", entry.FullName);
                var receiptPath = relative.Replace('\\', '/');
                if (!seen.Add(receiptPath)) return InstallResult.Fail("package_duplicate_entry", receiptPath);
                if (relative.EndsWith("djcdevelopment.valheim.comfynetworksense.cfg", StringComparison.OrdinalIgnoreCase))
                    return InstallResult.Fail("package_personalized_config_forbidden", receiptPath);
                var target = Path.GetFullPath(Path.Combine(valheimPath, relative));
                if (!target.StartsWith(Path.GetFullPath(valheimPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return InstallResult.Fail("package_path_escape", entry.FullName);
                if (Directory.Exists(target)) return InstallResult.Fail("package_target_is_directory", receiptPath);
                var existed = File.Exists(target);
                var backup = Path.Combine(backupRoot, relative);
                plans.Add(new PlannedModpackFile(entry, receiptPath, target, backup, existed));
            }

            if (plans.Count == 0) return InstallResult.Fail("package_payload_empty");
            Directory.CreateDirectory(backupRoot);
            foreach (var plan in plans)
            {
                if (plan.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.Backup)!);
                    File.Copy(plan.Target, plan.Backup, true);
                }
                else
                {
                    created.Add(plan.Relative);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(plan.Target)!);
                var temporary = plan.Target + ".lumberjacks-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using var source = plan.Entry.Open();
                    await using var destination = File.Create(temporary);
                    await source.CopyToAsync(destination, cancellationToken);
                    File.Move(temporary, plan.Target, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                applied.Add(new AppliedModpackFile(plan.Target, plan.Backup, plan.Existed));
                changed.Add(plan.Relative);
            }
        }
        catch (Exception ex)
        {
            var rollbackError = RestoreApplied(applied);
            var detail = rollbackError is null ? ex.Message : $"{ex.Message}; automatic_restore_failed={rollbackError}";
            return InstallResult.Fail("package_install_failed", detail);
        }

        var installed = new InstalledRelease(manifest.release, manifest.mod_release, actualHash,
            DateTime.UtcNow, backupRoot, changed, created, beforeInstall.installed, 1);
        var nextState = CopyState(beforeInstall, installed);
        try { stateStore.Write(nextState); }
        catch (Exception ex)
        {
            var rollbackError = RestoreApplied(applied);
            var stateError = TryWriteState(beforeInstall);
            var detail = JoinErrors(ex.Message,
                rollbackError is null ? null : $"automatic_restore_failed={rollbackError}",
                stateError is null ? null : $"state_restore_failed={stateError}");
            return InstallResult.Fail("install_state_write_failed", detail);
        }
        return new InstallResult(true, "installed", null, installed);
    }

    public InstallResult RollbackLatest()
    {
        if (!_operationGate.Wait(0)) return InstallResult.Fail("modpack_operation_in_progress");
        try { return RollbackLatestCore(); }
        finally { _operationGate.Release(); }
    }

    InstallResult RollbackLatestCore()
    {
        var current = stateStore.Read();
        if (string.Equals(current.last_error, "local_state_unreadable", StringComparison.Ordinal))
            return InstallResult.Fail("local_state_unreadable");
        if (current.installed is null) return InstallResult.Fail("rollback_backup_missing");
        if (current.installed.transaction_schema_version != 1)
            return InstallResult.Fail("rollback_not_reversible_legacy_state");
        if (!Directory.Exists(current.installed.backup_path)) return InstallResult.Fail("rollback_backup_missing");
        var valheimPath = locator.Find();
        if (valheimPath is null) return InstallResult.Fail("valheim_not_found");
        if (ValheimLocator.IsRunning()) return InstallResult.Fail("valheim_is_running");
        var rollingBack = current.installed;
        var restores = new List<(string Source, string Target)>();
        var deletes = new List<string>();
        try
        {
            var backupRoot = Path.GetFullPath(rollingBack.backup_path);
            var expectedBackupRoot = Path.GetFullPath(Path.Combine(stateStore.DataDirectory, "backups")) + Path.DirectorySeparatorChar;
            if (!backupRoot.StartsWith(expectedBackupRoot, StringComparison.OrdinalIgnoreCase))
                return InstallResult.Fail("rollback_backup_outside_state");
            foreach (var backup in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(backupRoot, backup);
                var target = SafeRollbackTarget(valheimPath, relative);
                if (target is null) return InstallResult.Fail("rollback_path_escape", relative);
                restores.Add((backup, target));
            }
            if (rollingBack.transaction_schema_version == 1)
            {
                foreach (var relative in rollingBack.created_files ?? [])
                {
                    var target = SafeRollbackTarget(valheimPath, relative);
                    if (target is null) return InstallResult.Fail("rollback_path_escape", relative);
                    if (restores.Any(item => string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase)))
                        return InstallResult.Fail("rollback_state_conflict", relative);
                    deletes.Add(target);
                }
            }
        }
        catch (Exception ex) { return InstallResult.Fail("rollback_preflight_failed", ex.Message); }
        try
        {
            foreach (var restore in restores)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(restore.Target)!);
                File.Copy(restore.Source, restore.Target, true);
            }
            foreach (var target in deletes)
            {
                if (File.Exists(target)) File.Delete(target);
            }
        }
        catch (Exception ex) { return InstallResult.Fail("rollback_restore_failed", ex.Message); }
        var installed = rollingBack.previous;
        var nextState = CopyState(current, installed);
        try { stateStore.Write(nextState); }
        catch (Exception ex) { return InstallResult.Fail("rollback_state_write_failed", ex.Message); }
        return new InstallResult(true, "rolled_back", null, installed);
    }

    static string? ArchiveRelativePath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        const string prefix = "Valheim/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = normalized[prefix.Length..];
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.IsNullOrWhiteSpace(relative) || segments.Length == 0 ||
            segments.Any(segment => segment is "." or "..")
            ? null
            : string.Join(Path.DirectorySeparatorChar, segments);
    }

    static string? SafeRollbackTarget(string valheimPath, string relative)
    {
        var root = Path.GetFullPath(valheimPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    static string? RestoreApplied(IEnumerable<AppliedModpackFile> applied)
    {
        try
        {
            foreach (var file in applied.Reverse())
            {
                if (file.Existed) File.Copy(file.Backup, file.Target, true);
                else if (File.Exists(file.Target)) File.Delete(file.Target);
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    static CompanionState CopyState(CompanionState source, InstalledRelease? installed) => new()
    {
        schema_version = source.schema_version,
        profile = source.profile,
        installed = installed,
        last_error = null,
    };

    string? TryWriteState(CompanionState state)
    {
        try { stateStore.Write(state); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    static string JoinErrors(params string?[] errors) => string.Join("; ", errors.Where(error => !string.IsNullOrWhiteSpace(error)));

    static string SafeToken(string value) => string.Concat(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));

    sealed record PlannedModpackFile(ZipArchiveEntry Entry, string Relative, string Target, string Backup, bool Existed);
    sealed record AppliedModpackFile(string Target, string Backup, bool Existed);
}

sealed class TransportTruthCaptureService(HttpClient client, CompanionStateStore stateStore, ValheimLocator locator)
{
    static readonly JsonSerializerOptions JsonLineOptions = new(Json.Options) { WriteIndented = false };

    public async Task<TransportCaptureSummary> CaptureAsync(int durationSeconds, int intervalSeconds, string label, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var runId = $"{startedUtc:yyyyMMdd-HHmmss}-{SafeToken(label)}";
        var runDirectory = Path.Combine(stateStore.DataDirectory, "captures", "transport-truth", runId);
        Directory.CreateDirectory(runDirectory);
        var samplesPath = Path.Combine(runDirectory, "samples.jsonl");
        var summaryPath = Path.Combine(runDirectory, "summary.json");
        var endAt = startedUtc.AddSeconds(durationSeconds);
        var sampleIndex = 0;
        int? firstMotionReceived = null;
        int? lastMotionReceived = null;
        var maxPeers = 0;
        int? firstPeers = null, lastPeers = null;
        int? firstPending = null, lastPending = null;
        int? firstActiveConsumers = null, lastActiveConsumers = null;
        int? firstAcknowledged = null, lastAcknowledged = null;
        int? firstApplied = null, lastApplied = null;
        int? firstMotionRelayed = null, lastMotionRelayed = null;
        string? firstMotionState = null, lastMotionState = null;
        bool? finalMotionWebSocketConnected = null, finalMotionUdpReady = null;
        string? finalMotionLastError = null;
        var localMotionReady = false;
        var localMotionApplied = false;
        TransportLocalMotionSnapshot? finalLocalMotion = null;
        var badSamples = 0;
        var observedPlayers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedMotionStates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        TransportCaptureIdentity? captureIdentity = null;
        TransportCurrentRead? finalCurrentRead = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = DateTimeOffset.UtcNow;
            var deployment = await ReadEndpointAsync("/api/v0/telemetry/deployment", cancellationToken);
            var valheim = await ReadEndpointAsync("/api/v0/telemetry/valheim", cancellationToken);
            var cutover = await ReadEndpointAsync("/api/v0/telemetry/cutover", cancellationToken);
            var motion = await ReadEndpointAsync("/live/valheim-motion", cancellationToken);
            var localMotion = ReadLatestLocalMotion();
            finalLocalMotion = LocalMotionSnapshot(localMotion);
            if (localMotion.HasValue)
            {
                var state = StringProperty(localMotion.Value, "motion_state");
                localMotionReady |= string.Equals(state, "observing", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "websocket", StringComparison.OrdinalIgnoreCase)
                    || BoolValue(localMotion.Value, "motion_websocket_connected") == true
                    || BoolValue(localMotion.Value, "motion_udp_ready") == true;
                localMotionApplied |= IntValue(localMotion.Value, "motion_applied") > 0;
            }
            var currentRead = CurrentRead(deployment, valheim, motion);
            if (localMotion.HasValue)
                currentRead = CurrentRead(deployment, valheim, motion, localMotion.Value);
            finalCurrentRead = currentRead;

            if (!deployment.ok || !valheim.ok || !cutover.ok || !motion.ok || !HasReadableHeartbeat(valheim)) badSamples++;
            captureIdentity = MergeIdentity(captureIdentity, deployment, valheim, cutover);

            if (motion.ok)
            {
                var received = IntValue(motion.body, "received");
                firstMotionReceived ??= received;
                lastMotionReceived = received;
                var relayed = IntValue(motion.body, "relayed_udp") + IntValue(motion.body, "relayed_websocket");
                firstMotionRelayed ??= relayed;
                lastMotionRelayed = relayed;
            }

            if (valheim.ok)
            {
                var peers = PeerCount(valheim.body);
                var heartbeat = ObjectProperty(valheim.body, "heartbeat");
                if (peers > maxPeers) maxPeers = peers;
                firstPeers ??= peers;
                lastPeers = peers;
                foreach (var player in PlayerNames(valheim.body)) observedPlayers.Add(player);
                var motionState = localMotion.HasValue
                    ? StringProperty(localMotion.Value, "motion_state")
                    : StringProperty(heartbeat, "motion_state");
                if (!string.IsNullOrWhiteSpace(motionState))
                {
                    firstMotionState ??= motionState;
                    lastMotionState = motionState;
                    observedMotionStates.Add(motionState);
                }
                finalMotionWebSocketConnected = localMotion.HasValue
                    ? BoolValue(localMotion.Value, "motion_websocket_connected")
                    : BoolValue(heartbeat, "motion_websocket_connected");
                finalMotionUdpReady = localMotion.HasValue
                    ? BoolValue(localMotion.Value, "motion_udp_ready")
                    : BoolValue(heartbeat, "motion_udp_ready");
                finalMotionLastError = localMotion.HasValue
                    ? StringProperty(localMotion.Value, "motion_last_error")
                    : StringProperty(heartbeat, "motion_last_error");
            }
            else
            {
                finalMotionWebSocketConnected = null;
                finalMotionUdpReady = null;
                finalMotionLastError = null;
            }

            if (cutover.ok)
            {
                var window = ObjectProperty(cutover.body, "authoritative_window");
                var pending = IntValue(window, "pending", IntValue(window, "consumer_pending"));
                var activeConsumers = IntValue(window, "active_consumers");
                var acknowledged = IntValue(window, "consumer_acknowledged", IntValue(window, "acknowledged"));
                var applied = IntValue(window, "applied");
                firstPending ??= pending;
                lastPending = pending;
                firstActiveConsumers ??= activeConsumers;
                lastActiveConsumers = activeConsumers;
                firstAcknowledged ??= acknowledged;
                lastAcknowledged = acknowledged;
                firstApplied ??= applied;
                lastApplied = applied;
            }

            var row = new TransportCaptureSample(
                1,
                "transport_truth.sample",
                timestamp.UtcDateTime,
                runId,
                sampleIndex,
                GatewayClient.GatewayUrl,
                currentRead,
                new TransportCaptureEndpoints(deployment, valheim, cutover, motion),
                localMotion);
            await File.AppendAllTextAsync(samplesPath, JsonSerializer.Serialize(row, JsonLineOptions) + Environment.NewLine, cancellationToken);
            sampleIndex++;

            var remaining = endAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(intervalSeconds, Math.Ceiling(remaining.TotalSeconds))), cancellationToken);
        }

        var finishedUtc = DateTimeOffset.UtcNow;
        var verdict = Verdict(badSamples, maxPeers, firstMotionReceived, lastMotionReceived, observedMotionStates, localMotionReady, localMotionApplied);
        var counterRanges = new TransportCaptureCounterRanges(
            CounterRange(firstPeers, lastPeers),
            CounterRange(firstMotionReceived, lastMotionReceived),
            CounterRange(firstMotionRelayed, lastMotionRelayed),
            CounterRange(firstPending, lastPending),
            CounterRange(firstActiveConsumers, lastActiveConsumers),
            CounterRange(firstAcknowledged, lastAcknowledged),
            CounterRange(firstApplied, lastApplied));
        var summary = new TransportCaptureSummary(
            1,
            runId,
            label,
            GatewayClient.GatewayUrl,
            startedUtc.UtcDateTime,
            finishedUtc.UtcDateTime,
            Math.Round((finishedUtc - startedUtc).TotalSeconds, 3),
            intervalSeconds,
            sampleIndex,
            badSamples,
            maxPeers,
            firstMotionReceived,
            lastMotionReceived,
            firstMotionReceived.HasValue && lastMotionReceived.HasValue ? lastMotionReceived.Value - firstMotionReceived.Value : null,
            verdict,
            finalCurrentRead,
            samplesPath,
            summaryPath,
            observedPlayers.ToList(),
            counterRanges,
            captureIdentity,
            Interpret(verdict, badSamples, maxPeers, observedPlayers.Count, counterRanges, observedMotionStates),
            firstMotionState,
            lastMotionState,
            observedMotionStates.ToList(),
            finalMotionWebSocketConnected,
            finalMotionUdpReady,
            finalMotionLastError,
            finalLocalMotion);
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, Json.Options), cancellationToken);
        return summary;
    }

    public string? ResolveCaptureFile(string runId, string file)
    {
        if (!IsSafeToken(runId)) return null;
        if (!file.Equals("summary.json", StringComparison.OrdinalIgnoreCase) && !file.Equals("samples.jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.Combine(stateStore.DataDirectory, "captures", "transport-truth");
        var path = Path.GetFullPath(Path.Combine(root, runId, file));
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public byte[]? CreateCaptureBundle(string runId)
    {
        var summaryPath = ResolveCaptureFile(runId, "summary.json");
        var samplesPath = ResolveCaptureFile(runId, "samples.jsonl");
        if (summaryPath is null && samplesPath is null) return null;

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (summaryPath is not null) archive.CreateEntryFromFile(summaryPath, "summary.json", CompressionLevel.Fastest);
            if (samplesPath is not null) archive.CreateEntryFromFile(samplesPath, "samples.jsonl", CompressionLevel.Fastest);
        }
        return stream.ToArray();
    }

    public IReadOnlyList<TransportCaptureSummary> ListCaptures(int limit)
    {
        var root = Path.Combine(stateStore.DataDirectory, "captures", "transport-truth");
        if (!Directory.Exists(root)) return [];
        var captures = new List<TransportCaptureSummary>();
        foreach (var summaryPath in Directory.EnumerateFiles(root, "summary.json", SearchOption.AllDirectories))
        {
            try
            {
                var summary = JsonSerializer.Deserialize<TransportCaptureSummary>(File.ReadAllText(summaryPath), Json.Options);
                if (summary is not null) captures.Add(NormalizeSummary(summary));
            }
            catch
            {
                // Ignore malformed local capture summaries; they should not break the dashboard.
            }
        }

        return captures
            .OrderByDescending(capture => capture.started_utc)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
    }

    async Task<TransportCaptureEndpoint> ReadEndpointAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(GatewayClient.GatewayUrl + path, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var body = default(JsonElement?);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(text);
                var normalized = JsonSerializer.Serialize(parsed, JsonLineOptions);
                using var document = JsonDocument.Parse(normalized);
                body = document.RootElement.Clone();
            }
            return new TransportCaptureEndpoint(response.IsSuccessStatusCode, path, (int)response.StatusCode, body, response.IsSuccessStatusCode ? null : response.ReasonPhrase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TransportCaptureEndpoint(false, path, null, null, ex.Message);
        }
    }

    JsonElement? ReadLatestLocalMotion()
    {
        try
        {
            var install = locator.Find();
            if (install is null) return null;
            var path = Path.Combine(install, "BepInEx", "config", "comfy-network-sense", "telemetry-client.jsonl");
            if (!File.Exists(path)) return null;
            var line = File.ReadLines(path).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(line)) return null;
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    static TransportCurrentRead CurrentRead(TransportCaptureEndpoint deployment, TransportCaptureEndpoint valheim, TransportCaptureEndpoint motion)
    {
        if (!deployment.ok) return new("bad", "Gateway telemetry unavailable; live network evidence is not trustworthy.");
        if (!valheim.ok) return new("bad", "Valheim heartbeat telemetry unavailable; this sample cannot establish client readiness.");
        if (!motion.ok) return new("bad", "Motion telemetry unavailable; use the in-game strip and trace before interpreting movement.");
        if (BoolValue(valheim.body, "stale") == true) return new("bad", "Valheim heartbeat is stale; this sample cannot establish client readiness.");
        var heartbeat = ObjectProperty(valheim.body, "heartbeat");
        if (heartbeat is null) return new("bad", "Valheim heartbeat payload is missing; this sample cannot establish client readiness.");
        var state = StringProperty(heartbeat, "motion_state") ?? "unknown";
        var ws = ReadinessText(BoolValue(heartbeat, "motion_websocket_connected"));
        var udp = ReadinessText(BoolValue(heartbeat, "motion_udp_ready"));
        var error = StringProperty(heartbeat, "motion_last_error");
        var received = IntValue(motion.body, "received");
        var stateText = $"client motion {state} (WS {ws}, UDP {udp})";
        if (!string.IsNullOrWhiteSpace(error)) stateText += $"; error {error}";
        var motionReady = string.Equals(state, "observing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "websocket", StringComparison.OrdinalIgnoreCase) ||
            BoolValue(heartbeat, "motion_websocket_connected") == true ||
            BoolValue(heartbeat, "motion_udp_ready") == true;
        if (received > 0 && motionReady) return new("ok", $"Lumberjacks motion frames are arriving; {stateText}.");
        if (received > 0) return new("wait", $"Lumberjacks counters advanced without an active motion lane; {stateText}. Treat this as ZDO/relay activity, not motion proof.");
        var peers = PeerCount(valheim.body);
        if (peers > 0) return new("wait", $"Valheim has {peers} peer(s), but Lumberjacks motion counters are zero; {stateText}. Visible player movement is native Valheim for this run.");
        return new("wait", $"P7 is up with no active peers; {stateText}. Join two clients, then watch Valheim peers and Motion counters change together.");
    }

    static TransportCurrentRead CurrentRead(TransportCaptureEndpoint deployment, TransportCaptureEndpoint valheim, TransportCaptureEndpoint motion, JsonElement localMotion)
    {
        if (!deployment.ok) return new("bad", "Gateway telemetry unavailable; live network evidence is not trustworthy.");
        if (!valheim.ok) return new("bad", "Valheim heartbeat telemetry unavailable; this sample cannot establish client readiness.");
        if (!motion.ok) return new("bad", "Motion telemetry unavailable; use the in-game strip and trace before interpreting movement.");

        var state = StringProperty(localMotion, "motion_state") ?? "unknown";
        var ws = ReadinessText(BoolValue(localMotion, "motion_websocket_connected"));
        var udp = ReadinessText(BoolValue(localMotion, "motion_udp_ready"));
        var error = StringProperty(localMotion, "motion_last_error");
        var received = IntValue(motion.body, "received");
        var stateText = $"client-local motion {state} (WS {ws}, UDP {udp})";
        if (!string.IsNullOrWhiteSpace(error)) stateText += $"; error {error}";
        var motionReady = string.Equals(state, "observing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "websocket", StringComparison.OrdinalIgnoreCase) ||
            BoolValue(localMotion, "motion_websocket_connected") == true ||
            BoolValue(localMotion, "motion_udp_ready") == true;
        if (received > 0 && motionReady) return new("ok", $"Lumberjacks motion frames are arriving; {stateText}. Gateway counters are relay evidence.");
        if (received > 0) return new("wait", $"Gateway counters advanced, but client-local motion was not ready; {stateText}.");
        var peers = PeerCount(valheim.body);
        if (peers > 0) return new("wait", $"Valheim has {peers} peer(s), but Gateway motion counters are zero; {stateText}.");
        return new("wait", $"P7 is up with no active peers; {stateText}.");
    }

    static string Verdict(int badSamples, int maxPeers, int? firstMotionReceived, int? lastMotionReceived, IReadOnlyCollection<string>? motionStates = null, bool localMotionReady = false, bool localMotionApplied = false)
    {
        if (badSamples > 0) return "incomplete_telemetry";
        var activeState = motionStates?.Any(state => string.Equals(state, "observing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "websocket", StringComparison.OrdinalIgnoreCase)) == true;
        if (firstMotionReceived.HasValue && lastMotionReceived.HasValue && lastMotionReceived.Value > firstMotionReceived.Value)
            return activeState ? "lumberjacks_motion_observed" : "motion_counter_only";
        if (maxPeers > 0 && localMotionApplied) return "motion_applied_no_gateway_delta";
        if (maxPeers > 0 && (localMotionReady || activeState)) return "motion_ready_no_gateway_delta";
        if (maxPeers > 0) return "native_motion_only";
        return "no_peer_window";
    }

    static TransportCaptureSummary NormalizeSummary(TransportCaptureSummary summary)
    {
        var verdict = string.IsNullOrWhiteSpace(summary.verdict)
            ? Verdict(summary.bad_sample_count, summary.max_peers, summary.first_motion_received, summary.last_motion_received, summary.observed_motion_states)
            : summary.verdict;
        var finalRead = summary.final_current_read ?? ReadFromVerdict(verdict, summary.max_peers);
        return summary with
        {
            verdict = verdict,
            final_current_read = finalRead,
            observed_players = summary.observed_players ?? [],
            capture_identity = summary.capture_identity ?? new TransportCaptureIdentity(
                CompanionVersion.Value,
                CompanionVersion.BootstrapRelease,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            observed_motion_states = summary.observed_motion_states ?? [],
            interpretation = summary.interpretation ?? Interpret(verdict, summary.bad_sample_count, summary.max_peers, summary.observed_players?.Count ?? 0, summary.counter_ranges, summary.observed_motion_states ?? []),
        };
    }

    static TransportCurrentRead ReadFromVerdict(string verdict, int maxPeers) => verdict switch
    {
        "incomplete_telemetry" => new("bad", "Capture had incomplete telemetry; use samples.jsonl before interpreting movement."),
        "lumberjacks_motion_observed" => new("ok", "Lumberjacks motion frames arrived during this capture."),
        "motion_counter_only" => new("wait", "Counters advanced, but the motion lane never reported active readiness; this is not motion proof."),
        "motion_ready_no_gateway_delta" => new("wait", $"Valheim had up to {maxPeers} peer(s), and the client-local motion lane was ready, but the Gateway motion counter did not advance. This is a transport/relay boundary result, not native-motion proof."),
        "motion_applied_no_gateway_delta" => new("ok", "The client-local motion lane applied snapshots while the Gateway motion counter stayed flat. This proves local presentation activity; use local counters and relay samples to explain the aggregate delta."),
        "native_motion_only" => new("wait", $"Valheim had up to {maxPeers} peer(s), but Lumberjacks motion counters did not advance. Visible player movement was native Valheim for this capture."),
        _ => new("wait", "No active peer window was captured."),
    };

    static TransportLocalMotionSnapshot? LocalMotionSnapshot(JsonElement? element) => element is null
        ? null
        : new(
            BoolValue(element, "motion_apply_enabled"),
            IntValue(element, "motion_received_udp") + IntValue(element, "motion_received_websocket"),
            IntValue(element, "motion_applied"),
            IntValue(element, "motion_unknown_zdos"),
            IntValue(element, "motion_direct_lookup_hits"),
            IntValue(element, "motion_zdo_object_lookup_hits"),
            IntValue(element, "motion_player_index_lookup_hits"),
            IntValue(element, "motion_player_index_rebuilds"),
            IntValue(element, "motion_player_index_size"),
            DoubleValue(element, "server_ping_age_ms") ?? DoubleValue(element, "rtt_ms"),
            DoubleValue(element, "server_ping_age_jitter_ms") ?? DoubleValue(element, "jitter_ms"));

    static TransportCaptureInterpretation Interpret(
        string verdict,
        int badSamples,
        int maxPeers,
        int observedPlayerCount,
        TransportCaptureCounterRanges? counters,
        IReadOnlyCollection<string>? motionStates = null)
    {
        var acknowledgedDelta = counters?.acknowledged?.delta ?? 0;
        var appliedDelta = counters?.applied?.delta ?? 0;
        var pendingDelta = counters?.pending?.delta ?? 0;
        var motionDelta = counters?.motion_received?.delta ?? 0;
        var stateText = motionStates is { Count: > 0 } ? string.Join(",", motionStates) : "unknown";

        return verdict switch
        {
            "incomplete_telemetry" => new(
                "bad",
                "Telemetry was incomplete during this capture.",
                "Do not use this run as a transport verdict. Re-run capture after Gateway, Valheim, cutover, and motion tiles are all readable.",
                $"Bad samples: {badSamples}."),
            "lumberjacks_motion_observed" => new(
                "ok",
                "Lumberjacks motion frames were observed during this capture.",
                "Compare in-game movement feel against the motion counter deltas and samples.jsonl; this run can support motion-lane debugging.",
                $"Motion received delta: {motionDelta}; max peers: {maxPeers}; observed players: {observedPlayerCount}; motion states: {stateText}."),
            "motion_counter_only" => new(
                "wait",
                "Lumberjacks counters advanced without an active motion lane.",
                "Do not attribute visible movement to Lumberjacks motion. Inspect the motion connection/readiness path; the advancing counter is likely ZDO or relay activity.",
                $"Counter delta: {motionDelta}; max peers: {maxPeers}; motion states: {stateText}; acknowledged delta: {acknowledgedDelta}; applied delta: {appliedDelta}."),
            "motion_ready_no_gateway_delta" => new(
                "wait",
                "The client-local motion lane was ready, but no Gateway motion delta was observed.",
                "Do not call this native-only. Inspect publish, recipient binding, and Gateway relay evidence before changing interpolation.",
                $"Max peers: {maxPeers}; motion states: {stateText}; motion delta: {motionDelta}; acknowledged delta: {acknowledgedDelta}; applied delta: {appliedDelta}."),
            "motion_applied_no_gateway_delta" => new(
                "ok",
                "Client-local motion snapshots were applied while the Gateway motion delta stayed flat.",
                "Treat local apply counters as presentation proof and Gateway counters as aggregate relay evidence; do not infer failure from a flat aggregate window.",
                $"Max peers: {maxPeers}; motion states: {stateText}; motion delta: {motionDelta}; acknowledged delta: {acknowledgedDelta}; applied delta: {appliedDelta}."),
            "native_motion_only" => new(
                "wait",
                "Valheim peers were present, but Lumberjacks motion counters did not advance.",
                "Treat visible player movement as native Valheim for this window. Use this as evidence that the remaining movement behavior is outside the Lumberjacks motion lane.",
                $"Max peers: {maxPeers}; motion states: {stateText}; acknowledged delta: {acknowledgedDelta}; applied delta: {appliedDelta}; pending delta: {pendingDelta}."),
            _ => new(
                "wait",
                "No active peer window was captured.",
                "Start capture before joining or moving two clients. The useful run begins when peer count rises above zero.",
                $"All peer and motion counters stayed at zero; motion states: {stateText}."),
        };
    }

    static int IntValue(JsonElement? element, string name, int fallback = 0)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.Value.TryGetProperty(name, out var property)) return fallback;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.Number when property.TryGetInt64(out var value) => value > int.MaxValue ? int.MaxValue : (int)value,
            _ => fallback,
        };
    }

    static double? DoubleValue(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return null;
        if (!element.Value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number) return null;
        return property.TryGetDouble(out var value) ? value : null;
    }

    static bool? BoolValue(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return null;
        if (!element.Value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    static string ReadinessText(bool? value) => value switch
    {
        true => "up",
        false => "down",
        _ => "unknown",
    };

    static bool HasReadableHeartbeat(TransportCaptureEndpoint endpoint) =>
        endpoint.ok && BoolValue(endpoint.body, "stale") != true && ObjectProperty(endpoint.body, "heartbeat") is not null;

    static int PeerCount(JsonElement? valheim) =>
        IntValue(valheim, "peers",
            IntValue(valheim, "peer_count",
                IntValue(ObjectProperty(valheim, "heartbeat"), "peer_count")));

    static JsonElement? ObjectProperty(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return null;
        return element.Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object ? property : null;
    }

    static TransportCaptureIdentity MergeIdentity(
        TransportCaptureIdentity? prior,
        TransportCaptureEndpoint deployment,
        TransportCaptureEndpoint valheim,
        TransportCaptureEndpoint cutover)
    {
        var heartbeat = ObjectProperty(valheim.body, "heartbeat");
        return new TransportCaptureIdentity(
            CompanionVersion.Value,
            CompanionVersion.BootstrapRelease,
            StringProperty(deployment.body, "lumberjacks_version") ?? prior?.gateway_version,
            StringProperty(deployment.body, "environment") ?? prior?.gateway_environment,
            StringProperty(heartbeat, "mod_version") ?? StringProperty(valheim.body, "mod_version") ?? prior?.valheim_mod_version,
            StringProperty(heartbeat, "instance_id") ?? StringProperty(valheim.body, "instance_id") ?? prior?.valheim_instance_id,
            StringProperty(heartbeat, "server_state") ?? StringProperty(valheim.body, "server_state") ?? prior?.valheim_server_state,
            StringProperty(cutover.body, "mode") ?? StringProperty(cutover.body, "state") ?? prior?.cutover_mode,
            StringProperty(cutover.body, "enrollment_manifest_id") ?? StringProperty(heartbeat, "enrollment_manifest_id") ?? prior?.enrollment_manifest_id);
    }

    static IReadOnlyList<string> PlayerNames(JsonElement? element)
    {
        var players = ArrayProperty(ObjectProperty(element, "heartbeat"), "players") ?? ArrayProperty(element, "players");
        if (players is null) return [];
        var names = new List<string>();
        foreach (var player in players.Value.EnumerateArray())
        {
            var name = player.ValueKind == JsonValueKind.String ? player.GetString() : null;
            name ??= StringProperty(player, "name") ??
                StringProperty(player, "player_name") ??
                StringProperty(player, "character_name") ??
                StringProperty(player, "steam_name") ??
                StringProperty(player, "id");
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }

    static JsonElement? ArrayProperty(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return null;
        return element.Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property : null;
    }

    static string? StringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    static string? StringProperty(JsonElement? element, string name) =>
        element is null ? null : StringProperty(element.Value, name);

    static TransportCaptureCounterRange? CounterRange(int? first, int? last) =>
        first.HasValue && last.HasValue ? new(first.Value, last.Value, last.Value - first.Value) : null;

    static string SafeToken(string value)
    {
        var token = string.Concat(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')).Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "companion" : token;
    }

    static bool IsSafeToken(string value) => !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}

sealed record ModpackManifest(int schema_version, string? release, string? mod_release, ModpackPackage? package);
sealed record ModpackPackage(string? kind, string sha256, long size_bytes);
sealed record CompanionBootstrapManifest(int schema_version, string release, DateTime? created_utc, CompanionBootstrapPackage? package, CompanionBootstrapDownloads? downloads);
sealed record CompanionBootstrapPackage(string? kind, string sha256, long size_bytes, string? entrypoint);
sealed record CompanionBootstrapDownloads(string? package, string? manifest, string? latest_update);
sealed record GameClosedConfirmation(bool game_closed_confirmed);
sealed record TransportCaptureRequest(int? duration_seconds, int? interval_seconds, string? label);
sealed record MotionTestRequest(string? action, string? pattern, int? duration_seconds, string? id, bool? motion_apply_enabled = null);
sealed record TransportCurrentRead(string level, string text);
sealed record TransportCaptureEndpoint(bool ok, string path, int? status, JsonElement? body, string? error);
sealed record TransportCaptureEndpoints(TransportCaptureEndpoint deployment, TransportCaptureEndpoint valheim, TransportCaptureEndpoint cutover, TransportCaptureEndpoint motion);
sealed record TransportCaptureSample(int schema_version, string event_type, DateTime timestamp_utc, string run_id, int sample_index, string base_url, TransportCurrentRead current_read, TransportCaptureEndpoints endpoints, JsonElement? local_motion = null);
sealed record TransportCaptureCounterRange(int first, int last, int delta);
sealed record TransportCaptureCounterRanges(
    TransportCaptureCounterRange? peers,
    TransportCaptureCounterRange? motion_received,
    TransportCaptureCounterRange? motion_relayed,
    TransportCaptureCounterRange? pending,
    TransportCaptureCounterRange? active_consumers,
    TransportCaptureCounterRange? acknowledged,
    TransportCaptureCounterRange? applied);
sealed record TransportCaptureIdentity(
    string companion_version,
    string bootstrap_release,
    string? gateway_version,
    string? gateway_environment,
    string? valheim_mod_version,
    string? valheim_instance_id,
    string? valheim_server_state,
    string? cutover_mode,
    string? enrollment_manifest_id);
sealed record TransportCaptureInterpretation(string level, string headline, string next_action, string evidence);
sealed record TransportLocalMotionSnapshot(bool? apply_enabled, int received, int applied, int unknown_zdos, int direct_lookup_hits, int zdo_object_lookup_hits, int player_index_lookup_hits, int player_index_rebuilds, int player_index_size, double? server_ping_age_ms, double? server_ping_age_jitter_ms);
sealed record TransportCaptureSummary(int schema_version, string run_id, string label, string base_url, DateTime started_utc, DateTime finished_utc, double duration_seconds, int interval_seconds, int sample_count, int bad_sample_count, int max_peers, int? first_motion_received, int? last_motion_received, int? motion_received_delta, string verdict, TransportCurrentRead? final_current_read, string samples_path, string summary_path, List<string>? observed_players = null, TransportCaptureCounterRanges? counter_ranges = null, TransportCaptureIdentity? capture_identity = null, TransportCaptureInterpretation? interpretation = null, string? first_motion_state = null, string? last_motion_state = null, List<string>? observed_motion_states = null, bool? final_motion_websocket_connected = null, bool? final_motion_udp_ready = null, string? final_motion_last_error = null, TransportLocalMotionSnapshot? final_local_motion = null);
sealed record CompanionProfile(string enrollment_id, DateTime? linked_utc);
sealed record InstalledRelease(
    string? release,
    string? mod_release,
    string package_sha256,
    DateTime installed_utc,
    string backup_path,
    List<string> changed_files,
    List<string>? created_files = null,
    InstalledRelease? previous = null,
    int transaction_schema_version = 0);

sealed class CompanionState
{
    public int schema_version { get; set; } = 1;
    public CompanionProfile? profile { get; set; }
    public InstalledRelease? installed { get; set; }
    public string? last_error { get; set; }
}
sealed record InstallResult(bool ok, string result, string? detail, InstalledRelease? installed)
{
    public static InstallResult Fail(string result, string? detail = null) => new(false, result, detail, null);
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    public static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };
}

static class CompanionLegacyPage
{
    public const string Html = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Lumberjacks Companion</title><style>body{max-width:920px;margin:40px auto;padding:0 18px;background:#101319;color:#e8edf4;font:16px system-ui}h1{color:#43a6ff}section{background:#191e27;border:1px solid #303846;border-radius:12px;padding:18px;margin:14px 0}button,a{background:#2476c6;color:white;border:0;border-radius:7px;padding:10px 14px;text-decoration:none;font:inherit;cursor:pointer}pre{white-space:pre-wrap;background:#0c0f14;padding:12px;border-radius:8px;overflow:auto}.ok{color:#77dc9b}.bad{color:#ffb05a}</style></head><body><h1>Lumberjacks Companion</h1><p>Local alpha control plane. Dashboard: <a href="/community">community</a> · <a href="/ops/boundary">trace</a> · <a href="/roadmap">roadmap</a></p><section><h2>Local status</h2><pre id="status">Loading…</pre></section><section><h2>Mod update</h2><p><button onclick="check()">Check for updates</button> <button onclick="install()">Install latest</button> <button onclick="rollback()">Rollback latest</button></p><pre id="update">No check yet.</pre></section><section><h2>Companion update</h2><pre id="self">Checking…</pre></section><script>async function get(u){let r=await fetch(u,{cache:'no-store'});return await r.json()}async function status(){document.querySelector('#status').textContent=JSON.stringify(await get('/api/v0/companion/status'),null,2)}async function check(){document.querySelector('#update').textContent=JSON.stringify(await get('/api/v0/companion/update/check'),null,2)}async function install(){let r=await fetch('/api/v0/companion/update/install',{method:'POST'});document.querySelector('#update').textContent=JSON.stringify(await r.json(),null,2);status()}async function rollback(){let r=await fetch('/api/v0/companion/update/rollback',{method:'POST'});document.querySelector('#update').textContent=JSON.stringify(await r.json(),null,2);status()}get('/api/v0/companion/release/check').then(x=>document.querySelector('#self').textContent=JSON.stringify(x,null,2));status()</script></body></html>
""";
}
