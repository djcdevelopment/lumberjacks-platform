using System.Runtime.CompilerServices;

namespace Game.Contracts.Protocol.Binary;

/// <summary>
/// Bit-level unpacking reader for compact binary deserialization.
/// Mirrors BitWriter — reads values in the same big-endian bit order.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _bitPosition;

    public BitReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _bitPosition = 0;
    }

    /// <summary>Current read position in bits.</summary>
    public int BitPosition => _bitPosition;

    /// <summary>Number of bits remaining in the buffer.</summary>
    public int BitsRemaining => (_buffer.Length * 8) - _bitPosition;

    /// <summary>
    /// Read up to 32 bits as an unsigned value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int bitCount)
    {
        if (bitCount is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Must be 1-32");

        uint value = 0;
        for (int i = bitCount - 1; i >= 0; i--)
        {
            int byteIndex = _bitPosition >> 3;
            int bitIndex = 7 - (_bitPosition & 7);

            if (byteIndex >= _buffer.Length)
                throw new InvalidOperationException("BitReader buffer underflow");

            if ((_buffer[byteIndex] & (1 << bitIndex)) != 0)
                value |= (1u << i);

            _bitPosition++;
        }
        return value;
    }

    /// <summary>Read a single boolean (1 bit).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadBits(1) == 1;

    /// <summary>Read a byte (8 bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte() => (byte)ReadBits(8);

    /// <summary>Read a 16-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16() => (ushort)ReadBits(16);

    /// <summary>Read a signed 16-bit integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16() => (short)(ushort)ReadBits(16);

    /// <summary>Read a 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32() => ReadBits(32);

    /// <summary>
    /// Read a variable-length integer (7-bit encoding, matches BitWriter.WriteVarInt).
    /// </summary>
    public uint ReadVarInt()
    {
        uint result = 0;
        int shift = 0;
        byte chunk;
        do
        {
            if (shift >= 35)
                throw new InvalidOperationException("VarInt too large");

            chunk = ReadByte();
            result |= (uint)(chunk & 0x7F) << shift;
            shift += 7;
        } while ((chunk & 0x80) != 0);

        return result;
    }

    /// <summary>
    /// Read a UTF-8 string prefixed with a VarInt length.
    /// </summary>
    public string ReadString()
    {
        var length = (int)ReadVarInt();
        if (length == 0) return string.Empty;

        Span<byte> bytes = length <= 256
            ? stackalloc byte[length]
            : new byte[length];

        for (int i = 0; i < length; i++)
            bytes[i] = ReadByte();

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Read raw bytes.
    /// </summary>
    public void ReadBytes(Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
            destination[i] = ReadByte();
    }
}
