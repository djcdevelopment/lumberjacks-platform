using Game.Simulation.Handlers;
using Game.Simulation.World;

namespace Game.Simulation.Endpoints;

public static class StructureEndpoints
{
    public static void Map(WebApplication app)
    {
        // Called by Gateway when a player sends place_structure via WebSocket
        app.MapPost("/structures/place", async (PlaceStructureRequest request, PlaceStructureHandler handler) =>
        {
            var result = await handler.HandleAsync(request);

            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Created($"/structures/{result.Structure!.Id}", new
            {
                structure_id = result.Structure.Id,
                type = result.Structure.Type,
                position = new { x = result.Structure.Position.X, y = result.Structure.Position.Y, z = result.Structure.Position.Z },
                rotation = result.Structure.Rotation,
                owner_id = result.Structure.OwnerId,
                region_id = result.Structure.RegionId,
                placed_at = result.Structure.PlacedAt,
                tags = result.Structure.Tags,
            });
        });

        // Query structures in a region
        app.MapGet("/structures", (string? region_id, WorldState world) =>
        {
            var structures = world.Structures.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(region_id))
                structures = structures.Where(s => s.RegionId == region_id);

            return Results.Ok(structures.Select(s => new
            {
                id = s.Id,
                type = s.Type,
                position = new { x = s.Position.X, y = s.Position.Y, z = s.Position.Z },
                rotation = s.Rotation,
                owner_id = s.OwnerId,
                region_id = s.RegionId,
                placed_at = s.PlacedAt,
                tags = s.Tags,
            }));
        });
    }
}
