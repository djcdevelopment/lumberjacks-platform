using Game.Persistence;
using Game.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("GameDb")
        ?? "Host=localhost;Port=5433;Database=game;Username=game;Password=game"));

var app = builder.Build();
app.MapServiceDefaults();

Game.EventLog.Endpoints.EventEndpoints.Map(app);
Game.EventLog.Endpoints.AchievementEndpoints.Map(app);

app.Run();
