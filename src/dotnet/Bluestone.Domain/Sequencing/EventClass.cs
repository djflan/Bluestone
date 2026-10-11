namespace Bluestone.Domain.Sequencing;

/// <summary>
/// Families of events, so a device can say which ones it processes. The rest pass through it untouched
/// (ADR 0022): a note processor never sees, and so never loses, SysEx or controller data.
/// </summary>
[Flags]
public enum EventClass
{
    None = 0,

    /// <summary>Notes, note releases, and per-note pressure: everything keyed by a note number.</summary>
    Notes = 1,

    /// <summary>Controllers, pitch bend, and channel pressure.</summary>
    Controllers = 2,

    /// <summary>Program and bank selections.</summary>
    Programs = 4,

    /// <summary>System exclusive messages and raw bytes, including manufacturer-specific data.</summary>
    SystemExclusive = 8,

    /// <summary>File-level meta events. They are never transmitted, so they never enter the signal graph.</summary>
    Meta = 16,

    /// <summary>Everything that can be sent to an instrument.</summary>
    Transmitted = Notes | Controllers | Programs | SystemExclusive,

    All = Transmitted | Meta,
}

public static class EventClassification
{
    public static EventClass Of(TrackEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            NoteEvent or NoteOffEvent or PolyPressureEvent => EventClass.Notes,
            ControllerEvent or PitchBendEvent or ChannelPressureEvent => EventClass.Controllers,
            ProgramEvent => EventClass.Programs,
            SysExEvent or RawMidiEvent => EventClass.SystemExclusive,
            MetaEvent => EventClass.Meta,
            _ => EventClass.None,
        };
    }
}
