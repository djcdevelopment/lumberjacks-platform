using Godot;
using CommunitySurvival.Core;

namespace CommunitySurvival.Replay;

/// <summary>
/// Cursor → entity hit-test. Slice 1 uses ray-to-point distance instead of
/// physics raycasting because orbs don't have collision shapes — the live-game
/// player.tscn is a thin client mesh, and brute-forcing 21 entities per frame
/// is trivial. If entity counts grow into the hundreds, swap for spatial
/// hashing or actual physics — the interface stays the same.
/// </summary>
public static class ReplayRaycast
{
    public static string FindEntityNearCursor(Camera3D camera, Core.World world, Vector2 mousePos, float hitRadius)
    {
        if (camera == null || world == null) return null;
        var rayOrigin = camera.ProjectRayOrigin(mousePos);
        var rayDir = camera.ProjectRayNormal(mousePos);

        string bestId = null;
        float bestDist = float.MaxValue;

        foreach (var (id, node) in world.Entities)
        {
            if (node == null) continue;
            var pos = node.GlobalPosition;
            var toEntity = pos - rayOrigin;
            var t = toEntity.Dot(rayDir);
            if (t < 0) continue; // behind camera
            var perp = toEntity - rayDir * t;
            var dist = perp.Length();
            if (dist < hitRadius && dist < bestDist)
            {
                bestDist = dist;
                bestId = id;
            }
        }
        return bestId;
    }
}
