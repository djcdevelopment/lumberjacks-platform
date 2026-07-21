using Game.Persistence;
using Game.Progression;
using Game.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("GameDb")
        ?? "Host=localhost;Port=5433;Database=game;Username=game;Password=game"));
builder.Services.AddScoped<ChallengeEngine>();

var app = builder.Build();
app.MapServiceDefaults();

Game.Progression.Endpoints.ProgressEndpoints.Map(app);
Game.Progression.Endpoints.ChallengeEndpoints.Map(app);

app.Run();
