using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>
/// A MIDI 1.0 voice channel. <see cref="Index"/> is the zero-based wire value (0-15);
/// <see cref="Number"/> is the one-based number musicians see (1-16).
/// </summary>
public readonly record struct MidiChannel : IComparable<MidiChannel>
{
    public const int Count = 16;

    private MidiChannel(byte index) => Index = index;

    /// <summary>Zero-based channel index as encoded in the status byte (0-15).</summary>
    public byte Index { get; }

    /// <summary>One-based channel number as displayed to users (1-16).</summary>
    public int Number => Index + 1;

    public static MidiChannel FromIndex(int index) => new((byte)Guard.InRange(index, 0, Count - 1, nameof(index)));

    public static MidiChannel FromNumber(int number) => new((byte)(Guard.InRange(number, 1, Count, nameof(number)) - 1));

    /// <summary>All sixteen channels in ascending order.</summary>
    public static IEnumerable<MidiChannel> All
    {
        get
        {
            for (var i = 0; i < Count; i++)
            {
                yield return new MidiChannel((byte)i);
            }
        }
    }

    public int CompareTo(MidiChannel other) => Index.CompareTo(other.Index);

    public static bool operator <(MidiChannel left, MidiChannel right) => left.Index < right.Index;

    public static bool operator >(MidiChannel left, MidiChannel right) => left.Index > right.Index;

    public static bool operator <=(MidiChannel left, MidiChannel right) => left.Index <= right.Index;

    public static bool operator >=(MidiChannel left, MidiChannel right) => left.Index >= right.Index;

    public override string ToString() => Number.ToString(CultureInfo.InvariantCulture);
}
