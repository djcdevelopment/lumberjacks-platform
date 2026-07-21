using System.Buffers;
using System.Runtime.CompilerServices;

namespace Game.Contracts.Protocol.Binary;

/// <summary>
/// Bit-level packing writer for compact binary serialization.
/// Writes values using the minimum number of bits specified.
/// All multi-bit values are written in big-endian bit order.
/// </summary>
public ref struct BitWriter
{
    private readonly Span<byte> _buffer;
    private int _bitPosition;

    public BitWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _bitPosition = 0;
    }

    /// <summary>Current write position in bits.</summary>
    public int BitPosition => _bitPosition;

    /// <summary>Number of full bytes written (rounds up).</summary>
    public int ByteLength => (_bitPosition + 7) >> 3;

    /// <summary>The written portion of the buffer.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer[..ByteLength];

    /// <summary>
    /// Write up to 32 bits of an unsigned value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(uint value, int bitCount)
    {
        if (bitCount is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Must be 1-32");

        // Mask to the requested bit count
        if (bitCount < 32)
            value &= (1u << bitCount) - 1;

        for (int i = bitCount - 1; i >= 0; i--)
        {
            int byteIndex = _bitPosition >> 3;
            int bitIndex = 7 - (_bitPosition & 7);

            if (byteIndex >= _buffer.Length)
                throw new InvalidOperationException("BitWriter buffer overflow");

            if (((value >> i) & 1) == 1)
                _buffer[byteIndex] |= (byte)(1 << bitIndex);
            else
                _buffer[byteIndex] &= (byte)~(1 << bitIndex);

            _bitPosition++;
        }
    }

    /// <summary>Write a single boolean as 1 bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);

    /// <summary>Write a byte (8 bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value) => WriteBits(value, 8);

    /// <summary>Write a 16-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort value) => WriteBits(value, 16);

    /// <summary>Write a signed 16-bit integer (two's complement).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16(short value) => WriteBits((uint)(ushort)value, 16);

    /// <summary>Write a 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint value) => WriteBits(value, 32);

    /// <summary>
    /// Write a variable-length integer using 7-bit encoding.
    /// Small values (0-127) use 1 byte; values up to 16383 use 2 bytes, etc.
    /// </summary>
    public void WriteVarInt(uint value)
    {
        do
        {
            var chunk = value & 0x7Fu;
            value >>= 7;
            if (value > 0)
                chunk |= 0x80u; // continuation bit
            WriteBits(chunk, 8);
        } while (value > 0);
    }

    /// <summary>
    /// Write a UTF-8 string prefixed with a VarInt length.
    /// </summary>
    public void WriteString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteVarInt((uint)bytes.Length);
        foreach (var b in bytes)
            WriteByte(b);
    }

    /// <summary>
    /// Write raw bytes directly (byte-aligned for efficiency, pads any partial byte first).
    /// </summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            WriteByte(b);
    }
}
