using System.Globalization;
using Bluestone.Domain.Midi;

namespace Bluestone.Midi.Wire;

/// <summary>MIDI 1.0 channel voice message types, valued as their status-byte high nibble.</summary>
public enum ChannelMessageKind : byte
{
    NoteOff = 0x80,
    NoteOn = 0x90,
    PolyPressure = 0xA0,
    ControlChange = 0xB0,
    ProgramChange = 0xC0,
    ChannelPressure = 0xD0,
    PitchBend = 0xE0,
}

/// <summary>
/// A compact, allocation-free MIDI 1.0 channel voice message: status byte plus up to two data bytes.
/// Instances are always well formed.
/// </summary>
public readonly record struct ChannelMessage
{
    private ChannelMessage(byte status, byte data1, byte data2)
    {
        Status = status;
        Data1 = data1;
        Data2 = DataLength(status) == 2 ? data2 : (byte)0;
    }

    public byte Status { get; }

    public byte Data1 { get; }

    /// <summary>The second data byte, or 0 for two-byte messages.</summary>
    public byte Data2 { get; }

    public ChannelMessageKind Kind => (ChannelMessageKind)(Status & 0xF0);

    public MidiChannel Channel => MidiChannel.FromIndex(Status & 0x0F);

    /// <summary>Total encoded length including the status byte (2 or 3).</summary>
    public int Length => 1 + DataLength(Status);

    /// <summary>True for note-on with non-zero velocity.</summary>
    public bool IsNoteOn => Kind == ChannelMessageKind.NoteOn && Data2 != 0;

    /// <summary>True for note-off, including the equivalent note-on with velocity 0.</summary>
    public bool IsNoteOff => Kind == ChannelMessageKind.NoteOff || (Kind == ChannelMessageKind.NoteOn && Data2 == 0);

    public static ChannelMessage NoteOn(MidiChannel channel, NoteNumber note, Velocity velocity) =>
        new(Compose(ChannelMessageKind.NoteOn, channel), note.Value, velocity.Value);

    public static ChannelMessage NoteOff(MidiChannel channel, NoteNumber note, Velocity releaseVelocity) =>
        new(Compose(ChannelMessageKind.NoteOff, channel), note.Value, releaseVelocity.Value);

    public static ChannelMessage PolyPressure(MidiChannel channel, NoteNumber note, SevenBitValue pressure) =>
        new(Compose(ChannelMessageKind.PolyPressure, channel), note.Value, pressure.Value);

    public static ChannelMessage ControlChange(MidiChannel channel, ControllerNumber controller, SevenBitValue value) =>
        new(Compose(ChannelMessageKind.ControlChange, channel), controller.Value, value.Value);

    public static ChannelMessage ProgramChange(MidiChannel channel, ProgramNumber program) =>
        new(Compose(ChannelMessageKind.ProgramChange, channel), program.Value, 0);

    public static ChannelMessage ChannelPressure(MidiChannel channel, SevenBitValue pressure) =>
        new(Compose(ChannelMessageKind.ChannelPressure, channel), pressure.Value, 0);

    public static ChannelMessage PitchBend(MidiChannel channel, FourteenBitValue value) =>
        new(Compose(ChannelMessageKind.PitchBend, channel), value.Lsb, value.Msb);

    /// <summary>Parses raw bytes. <paramref name="data2"/> is ignored for two-byte messages.</summary>
    public static bool TryCreate(byte status, byte data1, byte data2, out ChannelMessage message)
    {
        if (status is < 0x80 or >= 0xF0 || data1 > 0x7F || (DataLength(status) == 2 && data2 > 0x7F))
        {
            message = default;
            return false;
        }

        message = new ChannelMessage(status, data1, data2);
        return true;
    }

    /// <summary>The number of data bytes that follow a channel status byte (1 or 2).</summary>
    public static int DataLength(byte status) => (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;

    public NoteNumber Note => new(Data1);

    public Velocity Velocity => new(Data2);

    public ControllerNumber Controller => new(Data1);

    public SevenBitValue ControllerValue => new(Data2);

    public ProgramNumber Program => new(Data1);

    public FourteenBitValue PitchBendValue => FourteenBitValue.FromBytes(Data2, Data1);

    /// <summary>The same message addressed to another channel.</summary>
    public ChannelMessage WithChannel(MidiChannel channel) => new((byte)((Status & 0xF0) | channel.Index), Data1, Data2);

    /// <summary>Writes the encoded message and returns the number of bytes written.</summary>
    public int CopyTo(Span<byte> destination)
    {
        var length = Length;
        if (destination.Length < length)
        {
            throw new ArgumentException("Destination is too small for the message.", nameof(destination));
        }

        destination[0] = Status;
        destination[1] = Data1;
        if (length == 3)
        {
            destination[2] = Data2;
        }

        return length;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind} ch{Channel.Number} {Data1} {Data2}");

    private static byte Compose(ChannelMessageKind kind, MidiChannel channel) => (byte)((byte)kind | channel.Index);
}
