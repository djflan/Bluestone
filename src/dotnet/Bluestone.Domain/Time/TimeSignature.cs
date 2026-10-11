using System.Globalization;
using System.Numerics;

namespace Bluestone.Domain.Time;

/// <summary>A time signature such as 4/4 or 6/8. The denominator is a power of two from 1 to 128.</summary>
/// <remarks><c>default(TimeSignature)</c> is invalid; use <see cref="CommonTime"/>.</remarks>
public readonly record struct TimeSignature
{
    public const int MaxDenominator = 128;

    public static readonly TimeSignature CommonTime = new(4, 4);

    public TimeSignature(int numerator, int denominator)
    {
        Numerator = Guard.InRange(numerator, 1, 255, nameof(numerator));
        if (denominator < 1 || denominator > MaxDenominator || !BitOperations.IsPow2(denominator))
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "Denominator must be a power of two from 1 to 128.");
        }

        Denominator = denominator;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    /// <summary>The denominator as a power of two, as stored in MIDI files (4 → 2).</summary>
    public int DenominatorExponent => BitOperations.Log2((uint)Denominator);

    public bool IsValid => Numerator > 0;

    public static TimeSignature FromExponent(int numerator, int denominatorExponent) =>
        new(numerator, 1 << Guard.InRange(denominatorExponent, 0, 7, nameof(denominatorExponent)));

    /// <summary>Ticks in one beat (one denominator note), if that is a whole number at this resolution.</summary>
    public bool TryGetTicksPerBeat(Ppqn ppqn, out TickSpan ticksPerBeat)
    {
        var wholeNote = (long)ppqn.TicksPerQuarterNote * 4;
        if (!IsValid || !ppqn.IsValid || wholeNote % Denominator != 0)
        {
            ticksPerBeat = TickSpan.Zero;
            return false;
        }

        ticksPerBeat = new TickSpan(wholeNote / Denominator);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
}
