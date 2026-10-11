using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Bluestone.Plugins.Protocol.Exchange;

public enum PluginEventKind : byte
{
    NoteOff = 1,
    NoteOn = 2,
    PolyPressure = 3,
    ControlChange = 4,
    ProgramChange = 5,
    ChannelPressure = 6,
    PitchBend = 7,
    SystemExclusive = 8,
}

/// <summary>
/// A fixed-size, timestamped MIDI 1.0 event for the data plane. System exclusive data (including F0 and F7) is held
/// inline up to <see cref="MaxSystemExclusiveBytes"/>; longer messages cannot be represented and are dropped and
/// counted by the exchange. A value type with no references, so spans of events cost no allocation.
/// </summary>
public struct PluginEvent : IEquatable<PluginEvent>
{
    public const int MaxSystemExclusiveBytes = 256;

    /// <summary>Bytes of one event record in the mapped file: a 16-byte header and the inline payload.</summary>
    internal const int RecordBytes = 16 + MaxSystemExclusiveBytes;

    private SystemExclusivePayload _payload;
    private ushort _payloadLength;

    /// <summary>Position inside the block, in sample frames from its start.</summary>
    public int SampleOffset { readonly get; private set; }

    public PluginEventKind Kind { readonly get; private set; }

    /// <summary>MIDI channel, 0 to 15. Zero for system exclusive.</summary>
    public byte Channel { readonly get; private set; }

    /// <summary>First data byte: note, controller, program, pressure, or pitch-bend LSB.</summary>
    public byte Data1 { readonly get; private set; }

    /// <summary>Second data byte: velocity, value, or pitch-bend MSB.</summary>
    public byte Data2 { readonly get; private set; }

    [UnscopedRef]
    public readonly ReadOnlySpan<byte> SystemExclusiveData => ((ReadOnlySpan<byte>)_payload)[.._payloadLength];

    public static PluginEvent NoteOn(int sampleOffset, int channel, int note, int velocity) =>
        CreateChannelEvent(PluginEventKind.NoteOn, sampleOffset, channel, note, velocity);

    public static PluginEvent NoteOff(int sampleOffset, int channel, int note, int velocity = 0) =>
        CreateChannelEvent(PluginEventKind.NoteOff, sampleOffset, channel, note, velocity);

    public static PluginEvent PolyPressure(int sampleOffset, int channel, int note, int pressure) =>
        CreateChannelEvent(PluginEventKind.PolyPressure, sampleOffset, channel, note, pressure);

    public static PluginEvent ControlChange(int sampleOffset, int channel, int controller, int value) =>
        CreateChannelEvent(PluginEventKind.ControlChange, sampleOffset, channel, controller, value);

    public static PluginEvent ProgramChange(int sampleOffset, int channel, int program) =>
        CreateChannelEvent(PluginEventKind.ProgramChange, sampleOffset, channel, program, 0);

    public static PluginEvent ChannelPressure(int sampleOffset, int channel, int pressure) =>
        CreateChannelEvent(PluginEventKind.ChannelPressure, sampleOffset, channel, pressure, 0);

    /// <summary>A pitch bend with a 14-bit value, 8192 being centre.</summary>
    public static PluginEvent PitchBend(int sampleOffset, int channel, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 16383);
        return CreateChannelEvent(PluginEventKind.PitchBend, sampleOffset, channel, value & 0x7F, value >> 7);
    }

    /// <summary>Creates a system exclusive event; false when the message is longer than <see cref="MaxSystemExclusiveBytes"/>.</summary>
    public static bool TryCreateSystemExclusive(int sampleOffset, ReadOnlySpan<byte> message, out PluginEvent result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleOffset);
        result = default;
        if (message.Length > MaxSystemExclusiveBytes)
        {
            return false;
        }

        result.SampleOffset = sampleOffset;
        result.Kind = PluginEventKind.SystemExclusive;
        message.CopyTo(result._payload);
        result._payloadLength = (ushort)message.Length;
        return true;
    }

    /// <summary>The same event at another position in the block.</summary>
    public readonly PluginEvent WithSampleOffset(int sampleOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleOffset);
        var copy = this;
        copy.SampleOffset = sampleOffset;
        return copy;
    }

    /// <summary>The same channel event with a different first data byte, for example a transposed note.</summary>
    public readonly PluginEvent WithData1(int data1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(data1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data1, 127);
        if (Kind == PluginEventKind.SystemExclusive)
        {
            throw new InvalidOperationException("System exclusive events have no data bytes.");
        }

        var copy = this;
        copy.Data1 = (byte)data1;
        return copy;
    }

    public readonly bool Equals(PluginEvent other) =>
        SampleOffset == other.SampleOffset && Kind == other.Kind && Channel == other.Channel && Data1 == other.Data1
        && Data2 == other.Data2 && SystemExclusiveData.SequenceEqual(other.SystemExclusiveData);

    public override readonly bool Equals(object? obj) => obj is PluginEvent other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(SampleOffset, Kind, Channel, Data1, Data2, _payloadLength);

    public static bool operator ==(PluginEvent left, PluginEvent right) => left.Equals(right);

    public static bool operator !=(PluginEvent left, PluginEvent right) => !left.Equals(right);

    public override readonly string ToString() => Kind == PluginEventKind.SystemExclusive
        ? $"{SampleOffset}: SystemExclusive[{_payloadLength}]"
        : $"{SampleOffset}: {Kind} ch{Channel} {Data1} {Data2}";

    internal readonly void WriteTo(Span<byte> record)
    {
        BinaryPrimitives.WriteInt32LittleEndian(record, SampleOffset);
        record[4] = (byte)Kind;
        record[5] = Channel;
        record[6] = Data1;
        record[7] = Data2;
        BinaryPrimitives.WriteUInt16LittleEndian(record[8..], _payloadLength);
        SystemExclusiveData.CopyTo(record[16..]);
    }

    /// <summary>
    /// Reads a record written by the other process. Returns false, leaving <paramref name="target"/> unspecified, for
    /// anything out of contract, so a misbehaving peer can never make this side index out of range.
    /// </summary>
    internal static bool TryReadFrom(ReadOnlySpan<byte> record, int frames, ref PluginEvent target)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(record);
        var kind = (PluginEventKind)record[4];
        var channel = record[5];
        var data1 = record[6];
        var data2 = record[7];
        var length = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        if (offset < 0 || offset >= Math.Max(frames, 1) || kind is < PluginEventKind.NoteOff or > PluginEventKind.SystemExclusive)
        {
            return false;
        }

        if (kind == PluginEventKind.SystemExclusive)
        {
            if (length > MaxSystemExclusiveBytes || channel != 0 || data1 != 0 || data2 != 0)
            {
                return false;
            }
        }
        else if (length != 0 || channel > 15 || data1 > 127 || data2 > 127)
        {
            return false;
        }

        target.SampleOffset = offset;
        target.Kind = kind;
        target.Channel = channel;
        target.Data1 = data1;
        target.Data2 = data2;
        target._payloadLength = length;
        record.Slice(16, length).CopyTo(target._payload);
        return true;
    }

    private static PluginEvent CreateChannelEvent(PluginEventKind kind, int sampleOffset, int channel, int data1, int data2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 15);
        ArgumentOutOfRangeException.ThrowIfNegative(data1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data1, 127);
        ArgumentOutOfRangeException.ThrowIfNegative(data2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data2, 127);
        return new PluginEvent
        {
            SampleOffset = sampleOffset,
            Kind = kind,
            Channel = (byte)channel,
            Data1 = (byte)data1,
            Data2 = (byte)data2,
        };
    }

    [InlineArray(MaxSystemExclusiveBytes)]
    private struct SystemExclusivePayload
    {
        private byte _element0;
    }
}
