namespace Game.OperatorApi.Endpoints;

public static class StatusEndpoints
{
    private record ServiceHealth(string Service, string Url);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/status", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var services = new[]
            {
                new ServiceHealth("gateway", config["ServiceUrls:Gateway"] ?? "http://localhost:4000"),
                new ServiceHealth("event-log", config["ServiceUrls:EventLog"] ?? "http://localhost:4002"),
                new ServiceHealth("progression", config["ServiceUrls:Progression"] ?? "http://localhost:4003"),
            };

            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);

            var results = await Task.WhenAll(services.Select(async svc =>
            {
                try
                {
                    var response = await client.GetAsync($"{svc.Url}/health");
                    var data = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                    return new { service = svc.Service, status = "up", data };
                }
                catch
                {
                    return new { service = svc.Service, status = "down", data = (Dictionary<string, object>?)null };
                }
            }));

            return Results.Ok(new { services = results, timestamp = DateTimeOffset.UtcNow });
        });
    }
}
