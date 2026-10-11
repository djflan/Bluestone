using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal.Live;

/// <summary>The MIDI 1.0 channel messages live input carries. System messages are never live input.</summary>
public enum LiveEventKind : byte
{
    NoteOff,
    NoteOn,
    PolyPressure,
    ControlChange,
    ProgramChange,
    ChannelPressure,

    /// <summary>A 14-bit bend: <see cref="LiveEvent.Data1"/> is the low seven bits, <see cref="LiveEvent.Data2"/> the high.</summary>
    PitchBend,
}

/// <summary>
/// One live channel message at a tick of the live clock (ADR 0035). A value type, so live processing allocates
/// nothing. A note-on always has a velocity above zero; a note-on with velocity 0 is read as a note-off.
/// </summary>
/// <param name="Tick">Position on the live clock, which only moves forward (it is not the song position).</param>
/// <param name="Kind">The message.</param>
/// <param name="Channel">0 to 15.</param>
/// <param name="Data1">Note number, controller number, program, pressure, or the bend's low bits.</param>
/// <param name="Data2">Velocity, controller value, poly pressure, or the bend's high bits.</param>
public readonly record struct LiveEvent(long Tick, LiveEventKind Kind, byte Channel, byte Data1, byte Data2)
{
    /// <summary>Channels times note numbers: the size of a table keyed by <see cref="NoteSlot"/>.</summary>
    public const int NoteSlots = 16 * 128;

    public bool IsNoteOn => Kind == LiveEventKind.NoteOn;

    public bool IsNoteOff => Kind == LiveEventKind.NoteOff;

    /// <summary>The note's place in a 16 × 128 table (channel and note number), for note-keyed events.</summary>
    public int NoteSlot => (Channel << 7) | Data1;

    /// <summary>The class a device's <c>Handles</c> is compared with (passthrough by class, ADR 0022).</summary>
    public EventClass Class => Kind switch
    {
        LiveEventKind.NoteOff or LiveEventKind.NoteOn or LiveEventKind.PolyPressure => EventClass.Notes,
        LiveEventKind.ProgramChange => EventClass.Programs,
        _ => EventClass.Controllers,
    };

    /// <summary>The MIDI 1.0 status byte.</summary>
    public byte Status => (byte)(Kind switch
    {
        LiveEventKind.NoteOff => 0x80,
        LiveEventKind.NoteOn => 0x90,
        LiveEventKind.PolyPressure => 0xA0,
        LiveEventKind.ControlChange => 0xB0,
        LiveEventKind.ProgramChange => 0xC0,
        LiveEventKind.ChannelPressure => 0xD0,
        _ => 0xE0,
    } | Channel);

    public static LiveEvent NoteOn(long tick, int channel, int note, int velocity) =>
        velocity == 0 ? NoteOff(tick, channel, note) : new(tick, LiveEventKind.NoteOn, (byte)channel, (byte)note, (byte)velocity);

    public static LiveEvent NoteOff(long tick, int channel, int note, int velocity = 64) => new(tick, LiveEventKind.NoteOff, (byte)channel, (byte)note, (byte)velocity);

    /// <summary>Reads a MIDI 1.0 channel message (status, then data bytes). Returns false for anything else.</summary>
    public static bool TryFromMidi(long tick, byte status, byte data1, byte data2, out LiveEvent result)
    {
        result = default;
        if (status < 0x80 || status >= 0xF0 || data1 > 127 || data2 > 127)
        {
            return false;
        }

        var channel = (byte)(status & 0x0F);
        var kind = (status & 0xF0) switch
        {
            0x80 => LiveEventKind.NoteOff,
            0x90 => data2 == 0 ? LiveEventKind.NoteOff : LiveEventKind.NoteOn,
            0xA0 => LiveEventKind.PolyPressure,
            0xB0 => LiveEventKind.ControlChange,
            0xC0 => LiveEventKind.ProgramChange,
            0xD0 => LiveEventKind.ChannelPressure,
            _ => LiveEventKind.PitchBend,
        };

        // A note-on at velocity 0 is a release at the default release velocity.
        result = new LiveEvent(tick, kind, channel, data1, (status & 0xF0) == 0x90 && data2 == 0 ? (byte)64 : data2);
        return true;
    }

    public LiveEvent At(long tick) => this with { Tick = tick };
}

/// <summary>
/// A fixed-capacity list of <see cref="LiveEvent"/>s. It never grows: what does not fit is counted in
/// <see cref="Dropped"/>, so live processing allocates nothing once its buffers exist. One owner at a time.
/// </summary>
public sealed class LiveEventBuffer
{
    public const int DefaultCapacity = 512;

    private readonly LiveEvent[] _items;

    public LiveEventBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new LiveEvent[capacity];
    }

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    /// <summary>Events that did not fit, since the buffer was created.</summary>
    public long Dropped { get; private set; }

    public ReadOnlySpan<LiveEvent> Events => _items.AsSpan(0, Count);

    public LiveEvent this[int index] => (uint)index < (uint)Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public void Add(in LiveEvent e)
    {
        if (Count < _items.Length)
        {
            _items[Count++] = e;
        }
        else
        {
            Dropped++;
        }
    }

    public void AddRange(ReadOnlySpan<LiveEvent> events)
    {
        foreach (var e in events)
        {
            Add(e);
        }
    }

    public void Clear() => Count = 0;

    /// <summary>Sorts by tick in place, keeping the order of events at the same tick (an insertion sort: linear on sorted input).</summary>
    public void SortByTick()
    {
        var items = _items.AsSpan(0, Count);
        for (var i = 1; i < items.Length; i++)
        {
            var item = items[i];
            var j = i - 1;
            while (j >= 0 && items[j].Tick > item.Tick)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = item;
        }
    }
}
