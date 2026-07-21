using Game.Contracts.Entities;

namespace Game.Contracts.Protocol.Binary;

/// <summary>
/// Compact 48-bit fixed-point 3D vector for wire transmission.
///
/// Layout:
///   X: signed 16-bit integer (±32767 units, 1-unit precision)
///   Z: signed 16-bit integer (±32767 units, 1-unit precision)
///   Y: signed 16-bit integer mapped to ±3276.7 (0.1-unit precision for vertical)
///
/// Total: 48 bits (6 bytes) vs 192 bits (24 bytes) for three doubles.
/// Covers a 65km² world with 1-unit horizontal precision and 0.1-unit vertical precision.
/// </summary>
public readonly record struct CompactVec3(short X, short Y, short Z)
{
    /// <summary>Y-axis scale factor: stored as value * 10, giving 0.1 precision.</summary>
    private const double YScale = 10.0;

    /// <summary>
    /// Convert a full-precision Vec3 to a compact representation.
    /// Values are clamped to the representable range.
    /// </summary>
    public static CompactVec3 FromVec3(Vec3 v)
    {
        return new CompactVec3(
            X: ClampToInt16(v.X),
            Y: ClampToInt16(v.Y * YScale),
            Z: ClampToInt16(v.Z));
    }

    /// <summary>
    /// Convert back to a full-precision Vec3.
    /// </summary>
    public Vec3 ToVec3()
    {
        return new Vec3(X, Y / YScale, Z);
    }

    /// <summary>
    /// Write this vector to a BitWriter (48 bits total).
    /// </summary>
    public void Write(ref BitWriter writer)
    {
        writer.WriteInt16(X);
        writer.WriteInt16(Y);
        writer.WriteInt16(Z);
    }

    /// <summary>
    /// Read a CompactVec3 from a BitReader (48 bits total).
    /// </summary>
    public static CompactVec3 Read(ref BitReader reader)
    {
        return new CompactVec3(
            X: reader.ReadInt16(),
            Y: reader.ReadInt16(),
            Z: reader.ReadInt16());
    }

    private static short ClampToInt16(double value)
    {
        return (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
    }
}
