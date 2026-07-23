using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumberjacks.Companion;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<GatewayClient>();
builder.Services.AddSingleton<CompanionStateStore>();
builder.Services.AddSingleton<ValheimLocator>();
builder.Services.AddSingleton<ModpackInstaller>();
builder.Services.AddHttpClient<TransportTruthCaptureService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store, max-age=0";
    await next();
});

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
            enrollment_id = profile?.enrollment_id,
            linked_utc = profile?.linked_utc,
        },
        installed = saved.installed,
        last_error = saved.last_error,
    });
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

app.MapPost("/api/v0/companion/transport-capture", async (TransportCaptureRequest? request, TransportTruthCaptureService capture, CancellationToken cancellationToken) =>
{
    var duration = Math.Clamp(request?.duration_seconds ?? 60, 5, 300);
    var interval = Math.Clamp(request?.interval_seconds ?? 5, 1, 60);
    var label = string.IsNullOrWhiteSpace(request?.label) ? "companion" : request!.label!;
    var summary = await capture.CaptureAsync(duration, interval, label, cancellationToken);
    return Results.Ok(summary);
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

app.MapGet("/", () => Results.Content(CompanionPage.Html, "text/html"));
app.Run();

static class CompanionVersion
{
    public static string Value => typeof(CompanionVersion).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public static string BootstrapRelease => Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_BOOTSTRAP_RELEASE") ?? "unknown";
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

sealed class ModpackInstaller(CompanionStateStore stateStore, ValheimLocator locator)
{
    public async Task<InstallResult> InstallAsync(GatewayClient gateway, CancellationToken cancellationToken)
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
        var backupRoot = Path.Combine(stateStore.DataDirectory, "backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + releaseId);
        var changed = new List<string>();
        try
        {
            using var stream = new MemoryStream(package, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var relative = ArchiveRelativePath(entry.FullName);
                // The modpack carries a root README for humans. It is package metadata, not a
                // Valheim payload; skip it rather than treating a valid package as malformed.
                // Only Valheim/ entries can ever reach the local game directory.
                if (relative is null) continue;
                if (relative.EndsWith("djcdevelopment.valheim.comfynetworksense.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(valheimPath, relative));
                if (!target.StartsWith(Path.GetFullPath(valheimPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return InstallResult.Fail("package_path_escape", entry.FullName);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var destination = File.Create(target);
                await source.CopyToAsync(destination, cancellationToken);
                changed.Add(relative.Replace('\\', '/'));
            }
        }
        catch (Exception ex) { return InstallResult.Fail("package_install_failed", ex.Message); }

        var current = stateStore.Read();
        current.installed = new InstalledRelease(manifest.release, manifest.mod_release, actualHash, DateTime.UtcNow, backupRoot, changed);
        current.last_error = null;
        stateStore.Write(current);
        return new InstallResult(true, "installed", null, current.installed);
    }

    public InstallResult RollbackLatest()
    {
        var current = stateStore.Read();
        if (current.installed is null || !Directory.Exists(current.installed.backup_path)) return InstallResult.Fail("rollback_backup_missing");
        var valheimPath = locator.Find();
        if (valheimPath is null) return InstallResult.Fail("valheim_not_found");
        if (ValheimLocator.IsRunning()) return InstallResult.Fail("valheim_is_running");
        foreach (var backup in Directory.EnumerateFiles(current.installed.backup_path, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(current.installed.backup_path, backup);
            var target = Path.Combine(valheimPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backup, target, true);
        }
        current.last_error = null;
        stateStore.Write(current);
        return new InstallResult(true, "rolled_back", null, current.installed);
    }

    static string? ArchiveRelativePath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        const string prefix = "Valheim/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = normalized[prefix.Length..];
        return string.IsNullOrWhiteSpace(relative) || relative.Contains("../", StringComparison.Ordinal) ? null : relative.Replace('/', Path.DirectorySeparatorChar);
    }

    static string SafeToken(string value) => string.Concat(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
}

sealed class TransportTruthCaptureService(HttpClient client, CompanionStateStore stateStore)
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
        var badSamples = 0;
        var observedPlayers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var currentRead = CurrentRead(deployment, valheim, motion);
            finalCurrentRead = currentRead;

            if (!deployment.ok || !valheim.ok || !cutover.ok || !motion.ok) badSamples++;
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
                if (peers > maxPeers) maxPeers = peers;
                firstPeers ??= peers;
                lastPeers = peers;
                foreach (var player in PlayerNames(valheim.body)) observedPlayers.Add(player);
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
                new TransportCaptureEndpoints(deployment, valheim, cutover, motion));
            await File.AppendAllTextAsync(samplesPath, JsonSerializer.Serialize(row, JsonLineOptions) + Environment.NewLine, cancellationToken);
            sampleIndex++;

            var remaining = endAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(intervalSeconds, Math.Ceiling(remaining.TotalSeconds))), cancellationToken);
        }

        var finishedUtc = DateTimeOffset.UtcNow;
        var verdict = Verdict(badSamples, maxPeers, firstMotionReceived, lastMotionReceived);
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
            Interpret(verdict, badSamples, maxPeers, observedPlayers.Count, counterRanges));
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

    static TransportCurrentRead CurrentRead(TransportCaptureEndpoint deployment, TransportCaptureEndpoint valheim, TransportCaptureEndpoint motion)
    {
        if (!deployment.ok) return new("bad", "Gateway telemetry unavailable; live network evidence is not trustworthy.");
        if (!motion.ok) return new("bad", "Motion telemetry unavailable; use the in-game strip and trace before interpreting movement.");
        var received = IntValue(motion.body, "received");
        if (received > 0) return new("ok", "Lumberjacks motion frames are arriving.");
        var peers = PeerCount(valheim.body);
        if (peers > 0) return new("wait", $"Valheim has {peers} peer(s), but Lumberjacks motion counters are zero. Visible player movement is native Valheim for this run.");
        return new("wait", "P7 is up with no active peers. Join two clients, then watch Valheim peers and Motion counters change together.");
    }

    static string Verdict(int badSamples, int maxPeers, int? firstMotionReceived, int? lastMotionReceived)
    {
        if (badSamples > 0) return "incomplete_telemetry";
        if (firstMotionReceived.HasValue && lastMotionReceived.HasValue && lastMotionReceived.Value > firstMotionReceived.Value) return "lumberjacks_motion_observed";
        if (maxPeers > 0) return "native_motion_only";
        return "no_peer_window";
    }

    static TransportCaptureSummary NormalizeSummary(TransportCaptureSummary summary)
    {
        var verdict = string.IsNullOrWhiteSpace(summary.verdict)
            ? Verdict(summary.bad_sample_count, summary.max_peers, summary.first_motion_received, summary.last_motion_received)
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
            interpretation = summary.interpretation ?? Interpret(verdict, summary.bad_sample_count, summary.max_peers, summary.observed_players?.Count ?? 0, summary.counter_ranges),
        };
    }

    static TransportCurrentRead ReadFromVerdict(string verdict, int maxPeers) => verdict switch
    {
        "incomplete_telemetry" => new("bad", "Capture had incomplete telemetry; use samples.jsonl before interpreting movement."),
        "lumberjacks_motion_observed" => new("ok", "Lumberjacks motion frames arrived during this capture."),
        "native_motion_only" => new("wait", $"Valheim had up to {maxPeers} peer(s), but Lumberjacks motion counters did not advance. Visible player movement was native Valheim for this capture."),
        _ => new("wait", "No active peer window was captured."),
    };

    static TransportCaptureInterpretation Interpret(
        string verdict,
        int badSamples,
        int maxPeers,
        int observedPlayerCount,
        TransportCaptureCounterRanges? counters)
    {
        var acknowledgedDelta = counters?.acknowledged?.delta ?? 0;
        var appliedDelta = counters?.applied?.delta ?? 0;
        var pendingDelta = counters?.pending?.delta ?? 0;
        var motionDelta = counters?.motion_received?.delta ?? 0;

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
                $"Motion received delta: {motionDelta}; max peers: {maxPeers}; observed players: {observedPlayerCount}."),
            "native_motion_only" => new(
                "wait",
                "Valheim peers were present, but Lumberjacks motion counters did not advance.",
                "Treat visible player movement as native Valheim for this window. Use this as evidence that the remaining movement behavior is outside the Lumberjacks motion lane.",
                $"Max peers: {maxPeers}; acknowledged delta: {acknowledgedDelta}; applied delta: {appliedDelta}; pending delta: {pendingDelta}."),
            _ => new(
                "wait",
                "No active peer window was captured.",
                "Start capture before joining or moving two clients. The useful run begins when peer count rises above zero.",
                "All peer and motion counters stayed at zero."),
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
            var name = StringProperty(player, "name") ??
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
sealed record TransportCurrentRead(string level, string text);
sealed record TransportCaptureEndpoint(bool ok, string path, int? status, JsonElement? body, string? error);
sealed record TransportCaptureEndpoints(TransportCaptureEndpoint deployment, TransportCaptureEndpoint valheim, TransportCaptureEndpoint cutover, TransportCaptureEndpoint motion);
sealed record TransportCaptureSample(int schema_version, string event_type, DateTime timestamp_utc, string run_id, int sample_index, string base_url, TransportCurrentRead current_read, TransportCaptureEndpoints endpoints);
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
sealed record TransportCaptureSummary(int schema_version, string run_id, string label, string base_url, DateTime started_utc, DateTime finished_utc, double duration_seconds, int interval_seconds, int sample_count, int bad_sample_count, int max_peers, int? first_motion_received, int? last_motion_received, int? motion_received_delta, string verdict, TransportCurrentRead? final_current_read, string samples_path, string summary_path, List<string>? observed_players = null, TransportCaptureCounterRanges? counter_ranges = null, TransportCaptureIdentity? capture_identity = null, TransportCaptureInterpretation? interpretation = null);
sealed record CompanionProfile(string enrollment_id, DateTime? linked_utc);
sealed record InstalledRelease(string? release, string? mod_release, string package_sha256, DateTime installed_utc, string backup_path, List<string> changed_files);
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
}

static class CompanionLegacyPage
{
    public const string Html = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Lumberjacks Companion</title><style>body{max-width:920px;margin:40px auto;padding:0 18px;background:#101319;color:#e8edf4;font:16px system-ui}h1{color:#43a6ff}section{background:#191e27;border:1px solid #303846;border-radius:12px;padding:18px;margin:14px 0}button,a{background:#2476c6;color:white;border:0;border-radius:7px;padding:10px 14px;text-decoration:none;font:inherit;cursor:pointer}pre{white-space:pre-wrap;background:#0c0f14;padding:12px;border-radius:8px;overflow:auto}.ok{color:#77dc9b}.bad{color:#ffb05a}</style></head><body><h1>Lumberjacks Companion</h1><p>Local alpha control plane. Dashboard: <a href="/community">community</a> · <a href="/ops/boundary">trace</a> · <a href="/roadmap">roadmap</a></p><section><h2>Local status</h2><pre id="status">Loading…</pre></section><section><h2>Mod update</h2><p><button onclick="check()">Check for updates</button> <button onclick="install()">Install latest</button> <button onclick="rollback()">Rollback latest</button></p><pre id="update">No check yet.</pre></section><section><h2>Companion update</h2><pre id="self">Checking…</pre></section><script>async function get(u){let r=await fetch(u,{cache:'no-store'});return await r.json()}async function status(){document.querySelector('#status').textContent=JSON.stringify(await get('/api/v0/companion/status'),null,2)}async function check(){document.querySelector('#update').textContent=JSON.stringify(await get('/api/v0/companion/update/check'),null,2)}async function install(){let r=await fetch('/api/v0/companion/update/install',{method:'POST'});document.querySelector('#update').textContent=JSON.stringify(await r.json(),null,2);status()}async function rollback(){let r=await fetch('/api/v0/companion/update/rollback',{method:'POST'});document.querySelector('#update').textContent=JSON.stringify(await r.json(),null,2);status()}get('/api/v0/companion/release/check').then(x=>document.querySelector('#self').textContent=JSON.stringify(x,null,2));status()</script></body></html>
""";
}
