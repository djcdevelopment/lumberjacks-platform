using Game.Simulation.Handlers;
using Game.Simulation.World;

namespace Game.Simulation.Endpoints;

public static class PlayerEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/players", (WorldState world) =>
            Results.Ok(world.Players.Values.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                position = new { x = p.Position.X, y = p.Position.Y, z = p.Position.Z },
                region_id = p.RegionId,
                connected = p.Connected,
            })));

        app.MapPost("/players/join", (JoinRequest request, PlayerHandler handler) =>
        {
            var result = handler.Join(request);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(new
            {
                region_id = result.RegionId,
                player_id = result.PlayerId,
                entities = result.Entities,
                tick = 0,
            });
        });

        app.MapPost("/players/move", (MoveRequest request, PlayerHandler handler) =>
        {
            var result = handler.Move(request);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(new
            {
                player_id = result.PlayerId,
                position = new { x = result.Position.X, y = result.Position.Y, z = result.Position.Z },
                velocity = new { x = result.Velocity.X, y = result.Velocity.Y, z = result.Velocity.Z },
                region_id = result.RegionId,
                corrected = result.Corrected,
            });
        });

        app.MapPost("/players/leave", (LeaveRequest request, PlayerHandler handler) =>
        {
            var result = handler.Leave(request);
            return Results.Ok(new
            {
                removed = result.Removed,
                player_id = result.PlayerId,
                region_id = result.RegionId,
            });
        });
    }
}
