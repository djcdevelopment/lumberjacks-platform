using System.Threading.RateLimiting;
using Game.Contracts.Protocol;
using Game.Persistence;
using Game.ServiceDefaults;
using Game.Gateway.Valheim;
using Game.Gateway.WebSocket;
using Game.Simulation.Tick;
using Game.Simulation.World;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Gateway services
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MessageRouter>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ValheimPriorityManifestService>();
builder.Services.AddSingleton<ValheimZdoRedirectService>();
builder.Services.AddSingleton<ValheimZdoConsumerTelemetryService>();
builder.Services.AddSingleton<ValheimZdoInjectionService>();
builder.Services.AddSingleton<ValheimWindowActivityService>();
// Explicit factory: the ctor's other parameters are optional test seams DI must not try to bind.
builder.Services.AddSingleton<ValheimHandshakeService>(sp =>
    new ValheimHandshakeService(
        sp.GetRequiredService<ValheimWindowActivityService>(),
        nowUtc: null,
        roster: sp.GetRequiredService<SteamEnrollmentService>().CheckSteamId,
        recipientResolver: sp.GetRequiredService<SteamEnrollmentService>().GetRecipientId));
builder.Services.AddSingleton<ValheimTelemetryHeartbeatService>();
builder.Services.AddSingleton<SteamEnrollmentService>();

// M1 per-surface rate limits. Private/loopback callers (operator tunnel,
// server containers) are exempt; public surfaces get independent budgets so
// invite redemption, consumer poll/ACK, and telemetry cannot starve each
// other. The consumer budget is deliberately far above the frozen 0.5.31
// mod's poll cadence — these limits exist to bound abuse, not to shape
// normal traffic.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "rate_limited", surface = context.HttpContext.Request.Path.Value },
            cancellationToken);
    };

    static string ClientKey(HttpContext context) =>
        context.Request.Headers["X-Lumberjacks-Enrollment-Id"].ToString() is { Length: > 0 } id
            ? id
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    static bool IsPrivate(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null || System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    options.AddPolicy("join", context => IsPrivate(context)
        ? RateLimitPartition.GetNoLimiter("private")
        : RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
        }));

    options.AddPolicy("enrollment-admin", context => IsPrivate(context)
        ? RateLimitPartition.GetNoLimiter("private")
        : RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
        }));

    options.AddPolicy("consumer", context => IsPrivate(context)
        ? RateLimitPartition.GetNoLimiter("private")
        : RateLimitPartition.GetSlidingWindowLimiter(ClientKey(context), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 1000,
            Window = TimeSpan.FromSeconds(10),
            SegmentsPerWindow = 5,
        }));

    options.AddPolicy("telemetry", context => IsPrivate(context)
        ? RateLimitPartition.GetNoLimiter("private")
        : RateLimitPartition.GetSlidingWindowLimiter(ClientKey(context), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromSeconds(10),
            SegmentsPerWindow = 5,
        }));
});

// Simulation services (in-process — eliminates HTTP-per-move hop)
builder.Services.AddSingleton<WorldState>();
builder.Services.AddSingleton<InputQueue>();
// Tick timing: OTel-compatible histograms + windowed p50/p99/max log line and /tick snapshot
builder.Services.AddMetrics();
builder.Services.AddSingleton<TickMetrics>();
builder.Services.AddSingleton<UdpTransport>();
builder.Services.AddSingleton<TickBroadcaster>();
builder.Services.AddSingleton<ITickBroadcaster>(sp => sp.GetRequiredService<TickBroadcaster>());
builder.Services.AddHostedService<TickLoop>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpTransport>());
// Singletons: handlers use IDbContextFactory (not DbContext) so they're stateless.
// Must be singleton because MessageRouter (also singleton) depends on them directly.
builder.Services.AddSingleton<Game.Simulation.Handlers.PlayerHandler>();
builder.Services.AddSingleton<Game.Simulation.Handlers.PlaceStructureHandler>();
builder.Services.AddSingleton<Game.Simulation.Handlers.InventoryHandler>();
builder.Services.AddScoped<Game.Simulation.Startup.StructureLoader>();
builder.Services.AddScoped<Game.Simulation.Startup.RegionLoader>();
builder.Services.AddScoped<Game.Simulation.Startup.RegionProfileLoader>();
builder.Services.AddScoped<Game.Simulation.Startup.NaturalResourceLoader>();
builder.Services.AddDbContextFactory<GameDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("GameDb")
        ?? "Host=localhost;Port=5433;Database=game;Username=game;Password=game"));

var app = builder.Build();

// Load persisted data into WorldState on startup (graceful — works without DB)
try
{
    await using var scope = app.Services.CreateAsyncScope();
    var regionLoader = scope.ServiceProvider.GetRequiredService<Game.Simulation.Startup.RegionLoader>();
    await regionLoader.LoadAsync();

    var profileLoader = scope.ServiceProvider.GetRequiredService<Game.Simulation.Startup.RegionProfileLoader>();
    await profileLoader.LoadAsync();

    var resourceLoader = scope.ServiceProvider.GetRequiredService<Game.Simulation.Startup.NaturalResourceLoader>();
    await resourceLoader.LoadAsync();

    var structureLoader = scope.ServiceProvider.GetRequiredService<Game.Simulation.Startup.StructureLoader>();
    await structureLoader.LoadAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not load persisted data — running with in-memory defaults only");
}

app.MapServiceDefaults();
app.UseRateLimiter();
app.UseMiddleware<ValheimClientAccessMiddleware>();
app.UseWebSockets();
app.UseMiddleware<GameWebSocketMiddleware>();

// Expose simulation HTTP endpoints (for admin/debugging and legacy clients)
Game.Simulation.Endpoints.RegionEndpoints.Map(app);
Game.Simulation.Endpoints.PlayerEndpoints.Map(app);
Game.Simulation.Endpoints.StructureEndpoints.Map(app);
Game.Simulation.Endpoints.InventoryEndpoints.Map(app);
Game.Simulation.Endpoints.TickEndpoints.Map(app);
Game.Gateway.Endpoints.LiveMetricsEndpoints.Map(app);
// Public Telemetry API v0 (community-telemetry-strategy.md Phase 3) + Live Community View (Phase 4)
Game.Simulation.Endpoints.TelemetryV0Endpoints.Map(app);
Game.Gateway.Endpoints.TelemetryV0SessionsEndpoints.Map(app);
Game.Gateway.Endpoints.DeploymentTelemetryEndpoints.Map(app);
Game.Gateway.Endpoints.CommunityViewEndpoints.Map(app);
Game.Gateway.Endpoints.RoadmapViewEndpoints.Map(app);
// G3/G4/G5 UI first pass (community-telemetry-strategy.md, docs/ui/g3-g4-g5-first-pass.md):
// siblings of /community. G3 is live v0 data; G4/G5 are first-pass mockups with sample data /
// simulated actions behind visible banners — see each endpoint's doc comment.
Game.Gateway.Endpoints.NetworkSenseEndpoints.Map(app);
Game.Gateway.Endpoints.GameplayEventsEndpoints.Map(app);
Game.Gateway.Endpoints.LocalTestingEndpoints.Map(app);
ValheimPriorityManifestEndpoints.Map(app);
ValheimZdoRedirectEndpoints.Map(app);
ValheimZdoInjectionEndpoints.Map(app);
ValheimHandshakeEndpoints.Map(app);
ValheimTelemetryHeartbeatEndpoints.Map(app);
SteamEnrollmentEndpoints.Map(app);

app.Run();
