using Game.Contracts.Entities;
using Game.Contracts.Protocol.Binary;
using Xunit;

namespace Game.Contracts.Tests;

public class CompactVec3Tests
{
    [Fact]
    public void FromVec3_and_ToVec3_roundtrip_integer_coordinates()
    {
        var original = new Vec3(100, 50, -200);
        var compact = CompactVec3.FromVec3(original);
        var restored = compact.ToVec3();

        Assert.Equal(100.0, restored.X);
        Assert.Equal(50.0, restored.Y);
        Assert.Equal(-200.0, restored.Z);
    }

    [Fact]
    public void Y_axis_preserves_decimal_precision()
    {
        var original = new Vec3(0, 12.3, 0);
        var compact = CompactVec3.FromVec3(original);
        var restored = compact.ToVec3();

        Assert.Equal(12.3, restored.Y, 1); // 0.1 precision
    }

    [Fact]
    public void Clamps_to_int16_range()
    {
        var huge = new Vec3(100_000, 100_000, -100_000);
        var compact = CompactVec3.FromVec3(huge);

        Assert.Equal(short.MaxValue, compact.X);
        Assert.Equal(short.MaxValue, compact.Y); // 100_000 * 10 clamped
        Assert.Equal(short.MinValue, compact.Z);
    }

    [Fact]
    public void Zero_vector_roundtrips()
    {
        var compact = CompactVec3.FromVec3(new Vec3(0, 0, 0));
        var restored = compact.ToVec3();

        Assert.Equal(0, restored.X);
        Assert.Equal(0, restored.Y);
        Assert.Equal(0, restored.Z);
    }

    [Fact]
    public void Negative_coordinates_roundtrip()
    {
        var original = new Vec3(-100, -5.5, -300);
        var compact = CompactVec3.FromVec3(original);
        var restored = compact.ToVec3();

        Assert.Equal(-100.0, restored.X);
        Assert.Equal(-5.5, restored.Y, 1);
        Assert.Equal(-300.0, restored.Z);
    }

    [Fact]
    public void Binary_write_and_read_roundtrip()
    {
        var original = new Vec3(42, 7.5, -100);
        var compact = CompactVec3.FromVec3(original);

        Span<byte> buf = stackalloc byte[6];
        var writer = new BitWriter(buf);
        compact.Write(ref writer);

        Assert.Equal(48, writer.BitPosition); // 3 × 16 bits

        var reader = new BitReader(buf.ToArray());
        var restored = CompactVec3.Read(ref reader);

        Assert.Equal(compact.X, restored.X);
        Assert.Equal(compact.Y, restored.Y);
        Assert.Equal(compact.Z, restored.Z);
    }

    [Fact]
    public void Size_is_6_bytes()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new BitWriter(buf);
        new CompactVec3(0, 0, 0).Write(ref writer);

        Assert.Equal(6, writer.ByteLength);
    }

    [Fact]
    public void Typical_game_coordinates_roundtrip_accurately()
    {
        // Typical region bounds: -500 to 500 XZ, -10 to 200 Y
        var positions = new[]
        {
            new Vec3(-500, -10, -500),
            new Vec3(500, 200, 500),
            new Vec3(0, 0, 0),
            new Vec3(123, 45.6, -789),
            new Vec3(-42, 1.1, 300),
        };

        foreach (var pos in positions)
        {
            var compact = CompactVec3.FromVec3(pos);
            var restored = compact.ToVec3();

            Assert.Equal(Math.Round(pos.X), restored.X);
            Assert.Equal(Math.Round(pos.Y, 1), restored.Y, 1);
            Assert.Equal(Math.Round(pos.Z), restored.Z);
        }
    }
}
