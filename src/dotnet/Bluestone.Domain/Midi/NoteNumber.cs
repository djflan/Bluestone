using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>A MIDI note number (0-127). Middle C is 60.</summary>
public readonly record struct NoteNumber : IComparable<NoteNumber>
{
    public static readonly NoteNumber MiddleC = new(60);

    public NoteNumber(int value) => Value = Guard.SevenBit(value, nameof(value));

    public byte Value { get; }

    /// <summary>Transposes by a number of semitones, failing if the result leaves 0-127.</summary>
    public bool TryTranspose(int semitones, out NoteNumber result)
    {
        var transposed = Value + semitones;
        if ((uint)transposed > 127)
        {
            result = default;
            return false;
        }

        result = new NoteNumber(transposed);
        return true;
    }

    public int CompareTo(NoteNumber other) => Value.CompareTo(other.Value);

    public static bool operator <(NoteNumber left, NoteNumber right) => left.Value < right.Value;

    public static bool operator >(NoteNumber left, NoteNumber right) => left.Value > right.Value;

    public static bool operator <=(NoteNumber left, NoteNumber right) => left.Value <= right.Value;

    public static bool operator >=(NoteNumber left, NoteNumber right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
