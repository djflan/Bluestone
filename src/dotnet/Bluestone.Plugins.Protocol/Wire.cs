using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Bluestone.Plugins.Protocol;

/// <summary>Little-endian writer for control-plane payloads. Control plane only; it allocates.</summary>
internal sealed class WireWriter
{
    private byte[] _buffer;

    public WireWriter(int initialCapacity = 256) => _buffer = new byte[initialCapacity];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    public void WriteByte(byte value) => Reserve(1)[0] = value;

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);

    public void WriteGuid(Guid value)
    {
        if (!value.TryWriteBytes(Reserve(16)))
        {
            throw new InvalidOperationException("Could not write a GUID.");
        }
    }

    /// <summary>A string as a uint16 UTF-8 byte count followed by the bytes.</summary>
    public void WriteString(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        if (count > ProtocolLimits.MaxStringBytes)
        {
            throw new ProtocolException($"String of {count} bytes exceeds the {ProtocolLimits.MaxStringBytes}-byte limit.");
        }

        WriteUInt16((ushort)count);
        Encoding.UTF8.GetBytes(value, Reserve(count));
    }

    public void WriteOptionalString(string? value)
    {
        WriteBool(value is not null);
        if (value is not null)
        {
            WriteString(value);
        }
    }

    /// <summary>A byte array as a uint32 count followed by the bytes.</summary>
    public void WriteBytes(ImmutableArray<byte> value)
    {
        WriteUInt32((uint)value.Length);
        value.AsSpan().CopyTo(Reserve(value.Length));
    }

    private Span<byte> Reserve(int count)
    {
        if (Length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        }

        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }
}

/// <summary>
/// Bounds-checked little-endian reader. Every failure is a <see cref="ProtocolException"/>; counts are checked against
/// the remaining bytes before anything is allocated, so a hostile length cannot cause a large allocation.
/// </summary>
internal ref struct WireReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte() => Take(1)[0];

    public bool ReadBool() => ReadByte() switch
    {
        0 => false,
        1 => true,
        var other => throw new ProtocolException($"Invalid boolean byte {other}."),
    };

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public Guid ReadGuid() => new(Take(16));

    /// <summary>An int32 that must lie in [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public int ReadInt32InRange(int min, int max, string what)
    {
        var value = ReadInt32();
        if (value < min || value > max)
        {
            throw new ProtocolException($"{what} {value} is outside [{min}, {max}].");
        }

        return value;
    }

    public double ReadNormalized(string what)
    {
        var value = ReadDouble();
        if (!NormalizedValue.IsValid(value))
        {
            throw new ProtocolException($"{what} {value} is not a normalized value in [0, 1].");
        }

        return value;
    }

    public string ReadString()
    {
        var count = ReadUInt16();
        if (count > ProtocolLimits.MaxStringBytes)
        {
            throw new ProtocolException($"String of {count} bytes exceeds the {ProtocolLimits.MaxStringBytes}-byte limit.");
        }

        var bytes = Take(count);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProtocolException("String is not valid UTF-8.", ex);
        }
    }

    public string? ReadOptionalString() => ReadBool() ? ReadString() : null;

    public ImmutableArray<byte> ReadBytes(int maxBytes)
    {
        var count = ReadUInt32();
        if (count > (uint)maxBytes)
        {
            throw new ProtocolException($"Byte array of {count} bytes exceeds the {maxBytes}-byte limit.");
        }

        return [.. Take((int)count)];
    }

    /// <summary>
    /// An element count that must not exceed <paramref name="max"/> and must fit in the remaining bytes given each
    /// element takes at least <paramref name="minElementBytes"/> bytes.
    /// </summary>
    public int ReadCount(int max, int minElementBytes)
    {
        var count = ReadUInt32();
        if (count > (uint)max)
        {
            throw new ProtocolException($"Count {count} exceeds the limit of {max}.");
        }

        if ((long)count * minElementBytes > Remaining)
        {
            throw new ProtocolException($"Count {count} does not fit in the {Remaining} remaining bytes.");
        }

        return (int)count;
    }

    public readonly void EnsureEnd()
    {
        if (Remaining != 0)
        {
            throw new ProtocolException($"{Remaining} unexpected trailing bytes.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new ProtocolException($"Truncated data: needed {count} bytes, {Remaining} remain.");
        }

        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }
}
