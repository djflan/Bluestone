using System.Globalization;

namespace Bluestone.Domain.Time;

/// <summary>
/// Sequence resolution in pulses (ticks) per quarter note. Limited to 1-32767 so every
/// sequence can be represented in a Standard MIDI File header.
/// </summary>
/// <remarks><c>default(Ppqn)</c> is invalid; consumers validate with <see cref="EnsureValid"/>.</remarks>
public readonly record struct Ppqn
{
    public const int MaxValue = 0x7FFF;

    /// <summary>Bluestone's default resolution for new sequences.</summary>
    public static readonly Ppqn Default = new(960);

    public Ppqn(int ticksPerQuarterNote) =>
        TicksPerQuarterNote = Guard.InRange(ticksPerQuarterNote, 1, MaxValue, nameof(ticksPerQuarterNote));

    public int TicksPerQuarterNote { get; }

    public bool IsValid => TicksPerQuarterNote > 0;

    public TickSpan QuarterNote => new(TicksPerQuarterNote);

    internal Ppqn EnsureValid(string paramName) =>
        IsValid ? this : throw new ArgumentException("PPQN must be initialized.", paramName);

    public override string ToString() => TicksPerQuarterNote.ToString(CultureInfo.InvariantCulture);
}
