using Game.Contracts.Protocol.Binary;
using Xunit;

namespace Game.Contracts.Tests;

public class BitWriterReaderTests
{
    [Fact]
    public void WriteBits_and_ReadBits_roundtrip_single_bit()
    {
        Span<byte> buf = stackalloc byte[1];
        var writer = new BitWriter(buf);
        writer.WriteBits(1, 1);
        writer.WriteBits(0, 1);
        writer.WriteBits(1, 1);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(1u, reader.ReadBits(1));
        Assert.Equal(0u, reader.ReadBits(1));
        Assert.Equal(1u, reader.ReadBits(1));
    }

    [Fact]
    public void WriteBits_and_ReadBits_roundtrip_multi_bit()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new BitWriter(buf);
        writer.WriteBits(0b1010, 4);
        writer.WriteBits(0b110011, 6);
        writer.WriteBits(42, 8);
        writer.WriteBits(12345, 16);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(0b1010u, reader.ReadBits(4));
        Assert.Equal(0b110011u, reader.ReadBits(6));
        Assert.Equal(42u, reader.ReadBits(8));
        Assert.Equal(12345u, reader.ReadBits(16));
    }

    [Fact]
    public void WriteBool_and_ReadBool_roundtrip()
    {
        Span<byte> buf = stackalloc byte[1];
        var writer = new BitWriter(buf);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteBool(true);

        var reader = new BitReader(buf.ToArray());
        Assert.True(reader.ReadBool());
        Assert.False(reader.ReadBool());
        Assert.True(reader.ReadBool());
    }

    [Fact]
    public void WriteByte_and_ReadByte_roundtrip()
    {
        Span<byte> buf = stackalloc byte[3];
        var writer = new BitWriter(buf);
        writer.WriteByte(0);
        writer.WriteByte(255);
        writer.WriteByte(127);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(255, reader.ReadByte());
        Assert.Equal(127, reader.ReadByte());
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)32767)]
    [InlineData(ushort.MaxValue)]
    public void WriteUInt16_and_ReadUInt16_roundtrip(ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        var writer = new BitWriter(buf);
        writer.WriteUInt16(value);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(value, reader.ReadUInt16());
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)-1)]
    [InlineData(short.MinValue)]
    [InlineData(short.MaxValue)]
    [InlineData((short)1234)]
    [InlineData((short)-5678)]
    public void WriteInt16_and_ReadInt16_roundtrip(short value)
    {
        Span<byte> buf = stackalloc byte[2];
        var writer = new BitWriter(buf);
        writer.WriteInt16(value);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(value, reader.ReadInt16());
    }

    [Fact]
    public void WriteUInt32_and_ReadUInt32_roundtrip()
    {
        Span<byte> buf = stackalloc byte[4];
        var writer = new BitWriter(buf);
        writer.WriteUInt32(0xDEADBEEF);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(0xDEADBEEFu, reader.ReadUInt32());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(16383u)]
    [InlineData(16384u)]
    [InlineData(65535u)]
    [InlineData(1_000_000u)]
    public void WriteVarInt_and_ReadVarInt_roundtrip(uint value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new BitWriter(buf);
        writer.WriteVarInt(value);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(value, reader.ReadVarInt());
    }

    [Fact]
    public void VarInt_small_values_use_one_byte()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new BitWriter(buf);
        writer.WriteVarInt(42);
        Assert.Equal(8, writer.BitPosition); // 1 byte
    }

    [Fact]
    public void VarInt_medium_values_use_two_bytes()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new BitWriter(buf);
        writer.WriteVarInt(300);
        Assert.Equal(16, writer.BitPosition); // 2 bytes
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("Hello, World! 🌍")]
    [InlineData("player_move")]
    public void WriteString_and_ReadString_roundtrip(string value)
    {
        Span<byte> buf = stackalloc byte[256];
        var writer = new BitWriter(buf);
        writer.WriteString(value);

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(value, reader.ReadString());
    }

    [Fact]
    public void Mixed_types_roundtrip_across_byte_boundaries()
    {
        // This test verifies correctness when writes span byte boundaries
        Span<byte> buf = stackalloc byte[16];
        var writer = new BitWriter(buf);
        writer.WriteBits(0b101, 3);       // 3 bits
        writer.WriteBits(42, 8);          // crosses byte boundary
        writer.WriteBool(true);           // 1 bit
        writer.WriteUInt16(0xCAFE);       // 16 bits, crosses boundary
        writer.WriteBits(0b11110000, 8);  // 8 bits

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(0b101u, reader.ReadBits(3));
        Assert.Equal(42u, reader.ReadBits(8));
        Assert.True(reader.ReadBool());
        Assert.Equal((ushort)0xCAFE, reader.ReadUInt16());
        Assert.Equal(0b11110000u, reader.ReadBits(8));
    }

    [Fact]
    public void ByteLength_rounds_up()
    {
        Span<byte> buf = stackalloc byte[2];
        var writer = new BitWriter(buf);
        writer.WriteBits(1, 1);
        Assert.Equal(1, writer.ByteLength);

        writer.WriteBits(0, 8);
        Assert.Equal(2, writer.ByteLength); // 9 bits = 2 bytes
    }

    [Fact]
    public void WriteBits_overflow_throws()
    {
        var buf = new byte[1];
        var writer = new BitWriter(buf);
        writer.WriteByte(0xFF);

        // ref struct can't be used in lambda, so use manual try/catch
        bool threw = false;
        try { writer.WriteBool(true); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw, "Expected InvalidOperationException for buffer overflow");
    }

    [Fact]
    public void ReadBits_underflow_throws()
    {
        var reader = new BitReader(new byte[1]);
        reader.ReadByte(); // consume all 8 bits

        // ref struct can't be used in lambda, so use manual try/catch
        bool threw = false;
        try { reader.ReadBool(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw, "Expected InvalidOperationException for buffer underflow");
    }

    [Fact]
    public void BitsRemaining_tracks_correctly()
    {
        var reader = new BitReader(new byte[2]); // 16 bits
        Assert.Equal(16, reader.BitsRemaining);

        reader.ReadBits(5);
        Assert.Equal(11, reader.BitsRemaining);

        reader.ReadByte();
        Assert.Equal(3, reader.BitsRemaining);
    }

    [Fact]
    public void WriteBits_masks_excess_bits()
    {
        Span<byte> buf = stackalloc byte[1];
        var writer = new BitWriter(buf);
        writer.WriteBits(0xFF, 4); // Only bottom 4 bits should be written

        var reader = new BitReader(buf.ToArray());
        Assert.Equal(0x0Fu, reader.ReadBits(4));
    }
}
