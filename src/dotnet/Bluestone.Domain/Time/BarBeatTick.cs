using System.Globalization;

namespace Bluestone.Domain.Time;

/// <summary>
/// A musical position for display and entry: one-based bar and beat, and a zero-based tick within
/// the beat. Beats are denominator notes of the prevailing time signature.
/// </summary>
public readonly record struct BarBeatTick
{
    public BarBeatTick(long bar, int beat, long tick)
    {
        if (bar < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bar), bar, "Bars are numbered from 1.");
        }

        Bar = bar;
        Beat = Guard.InRange(beat, 1, int.MaxValue, nameof(beat));
        Tick = Guard.NonNegative(tick, nameof(tick));
    }

    public long Bar { get; }

    public int Beat { get; }

    public long Tick { get; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Bar}.{Beat}.{Tick}");
}
