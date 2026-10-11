using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Midi.Wire;

namespace Bluestone.Midi.Files;

/// <summary>Standard MIDI File format types.</summary>
public enum SmfFormat
{
    /// <summary>Format 0: one track containing every channel.</summary>
    SingleTrack = 0,

    /// <summary>Format 1: simultaneous tracks; the first conventionally holds tempo and meter.</summary>
    MultiTrack = 1,

    /// <summary>Format 2: independent single-track patterns.</summary>
    MultiSequence = 2,
}

/// <summary>
/// The timing division from an SMF header: either ticks per quarter note, or SMPTE frames per
/// second with ticks per frame.
/// </summary>
public readonly record struct SmfDivision
{
    private SmfDivision(int ticksPerQuarterNote, int framesPerSecond, int ticksPerFrame)
    {
        TicksPerQuarterNote = ticksPerQuarterNote;
        FramesPerSecond = framesPerSecond;
        TicksPerFrame = ticksPerFrame;
    }

    /// <summary>Ticks per quarter note, or 0 for SMPTE timing.</summary>
    public int TicksPerQuarterNote { get; }

    /// <summary>SMPTE frame rate (24, 25, 29 for 29.97 drop-frame, or 30), or 0 for metrical timing.</summary>
    public int FramesPerSecond { get; }

    public int TicksPerFrame { get; }

    public bool IsSmpte => FramesPerSecond != 0;

    public static SmfDivision Metrical(int ticksPerQuarterNote) =>
        ticksPerQuarterNote is >= 1 and <= 0x7FFF
            ? new SmfDivision(ticksPerQuarterNote, 0, 0)
            : throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote), ticksPerQuarterNote, "Ticks per quarter note must be 1-32767.");

    public static SmfDivision Smpte(int framesPerSecond, int ticksPerFrame)
    {
        if (framesPerSecond is not (24 or 25 or 29 or 30))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), framesPerSecond, "SMPTE frame rate must be 24, 25, 29, or 30.");
        }

        if (ticksPerFrame is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerFrame), ticksPerFrame, "Ticks per frame must be 1-255.");
        }

        return new SmfDivision(0, framesPerSecond, ticksPerFrame);
    }
}

/// <summary>An event in an SMF track at an absolute tick.</summary>
public abstract record SmfEvent(long Tick);

public sealed record SmfChannelEvent(long Tick, ChannelMessage Message) : SmfEvent(Tick);

/// <summary>
/// An <c>F0</c> SysEx event. <see cref="Data"/> is everything after the <c>F0</c> status as stored in
/// the file, normally ending with <c>F7</c>. A packet without a trailing <c>F7</c> begins a SysEx message
/// continued by later <see cref="SmfEscapeEvent"/> packets.
/// </summary>
public sealed record SmfSysExEvent(long Tick, ByteBlock Data) : SmfEvent(Tick)
{
    public bool IsComplete => Data.Length > 0 && Data[^1] == SysExMessage.End;
}

/// <summary>An <c>F7</c> event: bytes sent verbatim (a SysEx continuation, or an escaped message).</summary>
public sealed record SmfEscapeEvent(long Tick, ByteBlock Data) : SmfEvent(Tick);

/// <summary>A meta event (<c>FF</c>). End-of-track is not represented; see <see cref="SmfTrack.EndTick"/>.</summary>
public sealed record SmfMetaEvent(long Tick, byte Type, ByteBlock Data) : SmfEvent(Tick);

/// <summary>A file chunk: a track, or an unrecognized chunk preserved verbatim.</summary>
public abstract record SmfChunk;

/// <summary>An <c>MTrk</c> chunk. Events are in file order with non-decreasing ticks.</summary>
public sealed record SmfTrack : SmfChunk
{
    public SmfTrack(IEnumerable<SmfEvent> events, long endTick)
    {
        ArgumentNullException.ThrowIfNull(events);
        Events = [.. events];
        long previous = 0;
        foreach (var e in Events)
        {
            ArgumentNullException.ThrowIfNull(e, nameof(events));
            if (e.Tick < previous)
            {
                throw new ArgumentException("Track events must be in non-decreasing tick order.", nameof(events));
            }

            previous = e.Tick;
        }

        EndTick = Math.Max(endTick, previous);
    }

    public ImmutableArray<SmfEvent> Events { get; }

    /// <summary>The tick of the end-of-track meta event, never earlier than the last event.</summary>
    public long EndTick { get; }
}

/// <summary>A chunk with an unrecognized four-character type, kept so it can be written back.</summary>
public sealed record SmfUnknownChunk(string Type, ByteBlock Data) : SmfChunk;

/// <summary>A parsed Standard MIDI File, close to its on-disk structure.</summary>
public sealed record SmfFile(SmfFormat Format, SmfDivision Division, ImmutableArray<SmfChunk> Chunks)
{
    public IEnumerable<SmfTrack> Tracks => Chunks.OfType<SmfTrack>();
}

/// <summary>Well-known meta event types.</summary>
public static class SmfMetaType
{
    public const byte SequenceNumber = 0x00;
    public const byte Text = 0x01;
    public const byte Copyright = 0x02;
    public const byte TrackName = 0x03;
    public const byte InstrumentName = 0x04;
    public const byte Lyric = 0x05;
    public const byte Marker = 0x06;
    public const byte CuePoint = 0x07;
    public const byte ChannelPrefix = 0x20;
    public const byte Port = 0x21;
    public const byte EndOfTrack = 0x2F;
    public const byte Tempo = 0x51;
    public const byte SmpteOffset = 0x54;
    public const byte TimeSignature = 0x58;
    public const byte KeySignature = 0x59;
    public const byte SequencerSpecific = 0x7F;
}
