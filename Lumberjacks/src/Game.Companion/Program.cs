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

app.MapGet("/api/v0/companion/release/check", () => Results.Ok(new
{
    schema_version = 1,
    installed_version = CompanionVersion.Value,
    update_available = false,
    note = "Companion self-update is the next slice; modpack updates are live now.",
}));

// The existing operator dashboard remains at the stable local URLs. Only read-only GET traffic is
// forwarded; enrollment and mutating Gateway routes intentionally stay on their public origin.
foreach (var path in new[] { "/community", "/roadmap", "/networksense", "/events", "/testing", "/ops/boundary", "/ops/boundary/summary" })
{
    app.MapGet(path, (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
        gateway.ProxyGet(context, path, cancellationToken));
}

app.MapGet("/api/v0/telemetry/{**tail}", (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
    gateway.ProxyGet(context, "/api/v0/telemetry/" + (context.Request.RouteValues["tail"] ?? string.Empty), cancellationToken));
app.MapGet("/live/{**tail}", (HttpContext context, GatewayClient gateway, CancellationToken cancellationToken) =>
    gateway.ProxyGet(context, "/live/" + (context.Request.RouteValues["tail"] ?? string.Empty), cancellationToken));

app.MapGet("/", () => Results.Content(CompanionPage.Html, "text/html"));
app.Run();

static class CompanionVersion
{
    public static string Value => typeof(CompanionVersion).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
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

sealed record ModpackManifest(int schema_version, string? release, string? mod_release, ModpackPackage? package);
sealed record ModpackPackage(string? kind, string sha256, long size_bytes);
sealed record GameClosedConfirmation(bool game_closed_confirmed);
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
