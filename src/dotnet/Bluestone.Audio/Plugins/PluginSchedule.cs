using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio.Plugins;

/// <summary>A plugin event at a song frame (the event's own sample offset is ignored; the node sets it per block).</summary>
public readonly record struct ScheduledEvent(long Frame, PluginEvent Event);

/// <summary>A parameter change at a song frame. Value is normalized, 0 to 1.</summary>
public readonly record struct ScheduledParameterChange(long Frame, uint Parameter, double Value);

/// <summary>
/// What one plugin device receives over the song: events (from the software-instrument feed) and parameter changes
/// (from device automation), each sorted by song frame at the engine's rate. Immutable; built on a control thread and
/// handed to a <see cref="PluginNode"/>, which reads it on the audio thread.
/// </summary>
public sealed class PluginSchedule
{
    private const int NoteSlots = 16 * 128;

    // Indices of the note-on and note-off events, grouped by channel and note (slots), in event order; the events of
    // slot s are _noteEvents[_slotStarts[s].._slotStarts[s + 1]].
    private readonly int[] _noteEvents;
    private readonly int[] _slotStarts = new int[NoteSlots + 1];

    public PluginSchedule(IEnumerable<ScheduledEvent> events, IEnumerable<ScheduledParameterChange> parameters)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(parameters);

        // Stable sorts keep the canonical order of events at the same frame.
        Events = [.. events.OrderBy(e => e.Frame)];
        Parameters = [.. parameters.OrderBy(p => p.Frame)];
        foreach (var e in Events)
        {
            if (IsNote(e.Event))
            {
                _slotStarts[SlotOf(e.Event) + 1]++;
            }
        }

        for (var slot = 0; slot < NoteSlots; slot++)
        {
            _slotStarts[slot + 1] += _slotStarts[slot];
        }

        _noteEvents = new int[_slotStarts[NoteSlots]];
        var next = _slotStarts[..NoteSlots];
        for (var i = 0; i < Events.Length; i++)
        {
            if (IsNote(Events[i].Event))
            {
                _noteEvents[next[SlotOf(Events[i].Event)]++] = i;
            }
        }
    }

    public static PluginSchedule Empty { get; } = new([], []);

    public ScheduledEvent[] Events { get; }

    public ScheduledParameterChange[] Parameters { get; }

    /// <summary>Whether <paramref name="other"/> holds the same events and parameter changes at the same frames.</summary>
    public bool HasSameContent(PluginSchedule other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other)
            || (Events.AsSpan().SequenceEqual(other.Events) && Parameters.AsSpan().SequenceEqual(other.Parameters));
    }

    /// <summary>
    /// Whether the note in <paramref name="slot"/> (channel × 128 + note) sounds just before <paramref name="frame"/>:
    /// its last note-on or note-off before that frame is a note-on. Allocates nothing.
    /// </summary>
    internal bool Sounds(int slot, long frame)
    {
        int lo = _slotStarts[slot], hi = _slotStarts[slot + 1];
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Events[_noteEvents[mid]].Frame < frame)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo > _slotStarts[slot] && IsNoteOn(Events[_noteEvents[lo - 1]].Event);
    }

    internal static int SlotOf(in PluginEvent e) => (e.Channel << 7) | e.Data1;

    internal static bool IsNoteOn(in PluginEvent e) => e.Kind == PluginEventKind.NoteOn && e.Data2 > 0;

    private static bool IsNote(in PluginEvent e) => e.Kind is PluginEventKind.NoteOn or PluginEventKind.NoteOff;

    /// <summary>Index of the first event at or after <paramref name="frame"/>.</summary>
    internal int FirstEventAt(long frame)
    {
        int lo = 0, hi = Events.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Events[mid].Frame < frame)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Index of the first parameter change at or after <paramref name="frame"/>.</summary>
    internal int FirstParameterAt(long frame)
    {
        int lo = 0, hi = Parameters.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Parameters[mid].Frame < frame)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
