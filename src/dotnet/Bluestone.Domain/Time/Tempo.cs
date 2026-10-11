using System.Globalization;

namespace Bluestone.Domain.Time;

/// <summary>
/// Tempo as microseconds per quarter note, the exact unit used by MIDI files.
/// Beats per minute are derived and always refer to quarter notes.
/// </summary>
/// <remarks><c>default(Tempo)</c> is invalid; use <see cref="Default"/>.</remarks>
public readonly record struct Tempo
{
    /// <summary>The largest value a Standard MIDI File tempo event can hold (24 bits).</summary>
    public const int MaxMicrosecondsPerQuarterNote = 0xFFFFFF;

    /// <summary>120 quarter notes per minute, the MIDI default when no tempo is specified.</summary>
    public static readonly Tempo Default = new(500_000);

    public Tempo(int microsecondsPerQuarterNote) =>
        MicrosecondsPerQuarterNote = Guard.InRange(microsecondsPerQuarterNote, 1, MaxMicrosecondsPerQuarterNote, nameof(microsecondsPerQuarterNote));

    public int MicrosecondsPerQuarterNote { get; }

    public bool IsValid => MicrosecondsPerQuarterNote > 0;

    public double BeatsPerMinute => 60_000_000.0 / MicrosecondsPerQuarterNote;

    /// <summary>Creates a tempo from quarter notes per minute, rounding to the nearest microsecond.</summary>
    public static Tempo FromBeatsPerMinute(double beatsPerMinute)
    {
        if (!double.IsFinite(beatsPerMinute) || beatsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute), beatsPerMinute, "Tempo must be a positive, finite number of beats per minute.");
        }

        var microseconds = Math.Round(60_000_000.0 / beatsPerMinute, MidpointRounding.AwayFromZero);
        if (microseconds < 1 || microseconds > MaxMicrosecondsPerQuarterNote)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute), beatsPerMinute, "Tempo is outside the range a MIDI file can represent.");
        }

        return new Tempo((int)microseconds);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{BeatsPerMinute:0.###} BPM");
}
