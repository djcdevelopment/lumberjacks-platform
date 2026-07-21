using Game.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Game.OperatorApi.Endpoints;

public static class ProxyEndpoints
{
    public static void Map(WebApplication app)
    {
        // Simulation endpoints proxy to Gateway (which runs the simulation in-process)
        // rather than the standalone Simulation service.
        app.MapGet("/api/regions", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/regions");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json");
        });

        app.MapGet("/api/players", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/players");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json");
        });

        app.MapGet("/api/events", async (HttpContext context, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:EventLog"] ?? "http://localhost:4002";
            var client = httpFactory.CreateClient();
            var queryString = context.Request.QueryString;
            var response = await client.GetAsync($"{url}/events{queryString}");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json");
        });

        // Community achievements — projection over the authoritative event log.
        app.MapGet("/api/achievements", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:EventLog"] ?? "http://localhost:4002";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/achievements");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        app.MapGet("/api/structures", async (HttpContext context, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var queryString = context.Request.QueryString;
            var response = await client.GetAsync($"{url}/structures{queryString}");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json");
        });

        app.MapGet("/api/players/{id}/progress", async (string id, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Progression"] ?? "http://localhost:4003";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/players/{id}/progress");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        // Tick diagnostics (proxied from Gateway's in-process simulation)
        app.MapGet("/api/tick", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/tick");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        // Live transport distribution (proxied from Gateway's in-process telemetry)
        app.MapGet("/api/transport", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/live/transport");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        // Live session transitions (proxied from Gateway's in-process telemetry)
        app.MapGet("/api/sessions", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/live/sessions");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        // Create region (proxied to Gateway's in-process simulation)
        app.MapPost("/api/regions", async (HttpContext context, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{url}/regions", content);
            var result = await response.Content.ReadAsStringAsync();
            return Results.Content(result, "application/json", statusCode: (int)response.StatusCode);
        });

        // Delete region (proxied to Gateway's in-process simulation)
        app.MapDelete("/api/regions/{id}", async (string id, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Gateway"] ?? "http://localhost:4000";
            var client = httpFactory.CreateClient();
            var response = await client.DeleteAsync($"{url}/regions/{id}");
            var result = await response.Content.ReadAsStringAsync();
            return Results.Content(result, "application/json", statusCode: (int)response.StatusCode);
        });

        app.MapGet("/api/guilds", async (GameDbContext db) =>
        {
            var guilds = await db.GuildProgress
                .OrderByDescending(g => g.Points)
                .Select(g => new
                {
                    guild_id = g.GuildId,
                    points = g.Points,
                    challenges_completed = g.ChallengesCompleted,
                    updated_at = g.UpdatedAt,
                })
                .ToListAsync();

            return Results.Ok(guilds);
        });

        // --- Progression proxies (challenges & guild progress) ---

        app.MapGet("/api/challenges", async (HttpContext context, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Progression"] ?? "http://localhost:4003";
            var client = httpFactory.CreateClient();
            var qs = context.Request.QueryString;
            var response = await client.GetAsync($"{url}/challenges{qs}");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        app.MapPost("/api/challenges", async (HttpContext context, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Progression"] ?? "http://localhost:4003";
            var client = httpFactory.CreateClient();
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var payload = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{url}/challenges", payload);
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        app.MapPatch("/api/challenges/{id}", async (string id, HttpContext context, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Progression"] ?? "http://localhost:4003";
            var client = httpFactory.CreateClient();
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var payload = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PatchAsync($"{url}/challenges/{id}", payload);
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        app.MapGet("/api/challenges/{id}/progress", async (string id, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Progression"] ?? "http://localhost:4003";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/challenges/{id}/progress");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });

        app.MapGet("/api/guilds/{id}/progress", async (string id, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var url = config["ServiceUrls:Progression"] ?? "http://localhost:4003";
            var client = httpFactory.CreateClient();
            var response = await client.GetAsync($"{url}/guilds/{id}/progress");
            var content = await response.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        });
    }
}
