namespace Bluestone.Midi.Wire;

/// <summary>The broad class of a single MIDI 1.0 byte-stream message.</summary>
public enum MidiMessageClass
{
    Invalid,
    Channel,
    SystemExclusive,
    SystemCommon,
    SystemRealtime,
}

/// <summary>Validation of complete MIDI 1.0 messages as they appear on the wire (no running status).</summary>
public static class MidiWire
{
    /// <summary>
    /// Classifies <paramref name="message"/> if it is exactly one complete, well-formed message:
    /// a channel message with its data bytes, a SysEx message <c>F0 … F7</c>, a system common message,
    /// or a single realtime byte. Anything else is <see cref="MidiMessageClass.Invalid"/>.
    /// </summary>
    public static MidiMessageClass Classify(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            return MidiMessageClass.Invalid;
        }

        var status = message[0];
        switch (status)
        {
            case < 0x80:
                return MidiMessageClass.Invalid;
            case < 0xF0:
                var channelLength = (status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
                return message.Length == channelLength && AreDataBytes(message[1..]) ? MidiMessageClass.Channel : MidiMessageClass.Invalid;
            case 0xF0:
                return message.Length >= 2 && message[^1] == 0xF7 && AreDataBytes(message[1..^1])
                    ? MidiMessageClass.SystemExclusive
                    : MidiMessageClass.Invalid;
            case 0xF1 or 0xF3:
                return message.Length == 2 && AreDataBytes(message[1..]) ? MidiMessageClass.SystemCommon : MidiMessageClass.Invalid;
            case 0xF2:
                return message.Length == 3 && AreDataBytes(message[1..]) ? MidiMessageClass.SystemCommon : MidiMessageClass.Invalid;
            case 0xF6:
                return message.Length == 1 ? MidiMessageClass.SystemCommon : MidiMessageClass.Invalid;
            case 0xF8 or 0xFA or 0xFB or 0xFC or 0xFE or 0xFF:
                return message.Length == 1 ? MidiMessageClass.SystemRealtime : MidiMessageClass.Invalid;
            default:
                // F4, F5, F9, FD are undefined; a lone F7 is not a message.
                return MidiMessageClass.Invalid;
        }
    }

    private static bool AreDataBytes(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0;
}
