using Game.Persistence;
using Game.ServiceDefaults;
using Game.Simulation.Handlers;
using Game.Simulation.Tick;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton<WorldState>();
builder.Services.AddSingleton<InputQueue>();
builder.Services.AddSingleton<Game.Contracts.Protocol.ITickBroadcaster, Game.Contracts.Protocol.NullTickBroadcaster>();
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

Game.Simulation.Endpoints.RegionEndpoints.Map(app);
Game.Simulation.Endpoints.PlayerEndpoints.Map(app);
Game.Simulation.Endpoints.StructureEndpoints.Map(app);
Game.Simulation.Endpoints.InventoryEndpoints.Map(app);
Game.Simulation.Endpoints.TickEndpoints.Map(app);

app.Run();
