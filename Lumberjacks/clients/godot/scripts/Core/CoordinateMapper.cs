using Godot;
using Game.Contracts.Entities;

namespace CommunitySurvival.Core;

/// <summary>
/// Handles the mapping between Server space (+Z = North) and Godot space (-Z = Forward).
/// </summary>
public static class CoordinateMapper
{
    /// <summary>
    /// Converts a server-side Vec3 to a Godot Vector3.
    /// Server: X, Y (up), Z (North)
    /// Godot: X, Y (up), -Z (Forward)
    /// </summary>
    public static Vector3 ServerToGodot(Vec3 serverPos)
    {
        // ADR 0018: Server +Z maps to Godot -Z
        return new Vector3((float)serverPos.X, (float)serverPos.Y, (float)-serverPos.Z);
    }

    /// <summary>
    /// Converts a Godot Vector3 to a server-side Vec3.
    /// </summary>
    public static Vec3 GodotToServer(Vector3 godotPos)
    {
        return new Vec3(godotPos.X, godotPos.Y, -godotPos.Z);
    }

    /// <summary>
    /// Converts a server-side heading (0-360, 0 = North/+Z) to radians for Godot.
    /// In Godot, rotation.y = 0 points towards -Z (Forward).
    /// </summary>
    public static float ServerHeadingToGodot(float serverHeading)
    {
        // Server 0 (North) -> Godot 0 (-Z)
        // Server 90 (East) -> Godot -90 or 270 degrees
        return Mathf.DegToRad(-serverHeading);
    }

    /// <summary>
    /// Converts a Godot rotation (radians) to a server-side direction byte (0-255).
    /// </summary>
    public static byte GodotRotationToServerByte(float godotRotationY)
    {
        // godot_rotation 0 -> server_heading 0
        // godot_rotation -pi/2 -> server_heading 90
        float deg = -Mathf.RadToDeg(godotRotationY);
        while (deg < 0) deg += 360;
        while (deg >= 360) deg -= 360;

        return (byte)Mathf.FloorToInt((deg / 360.0f) * 256.0f % 256.0f);
    }
}
