using Godot;
using Game.Contracts.Entities;

namespace CommunitySurvival.Core;

/// <summary>
/// Server space (+Z = North) ↔ Godot space (-Z = Forward). ADR 0018.
/// </summary>
public static class CoordinateMapper
{
    public static Vector3 ServerToGodot(Vec3 s) => new((float)s.X, (float)s.Y, (float)-s.Z);
    public static Vec3 GodotToServer(Vector3 g) => new(g.X, g.Y, -g.Z);
    public static float ServerHeadingToGodot(float deg) => Mathf.DegToRad(-deg);

    public static byte GodotRotationToServerByte(float radY)
    {
        float deg = -Mathf.RadToDeg(radY);
        while (deg < 0) deg += 360;
        while (deg >= 360) deg -= 360;
        return (byte)(deg / 360f * 256f % 256f);
    }
}
