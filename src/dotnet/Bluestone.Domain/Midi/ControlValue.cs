using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>
/// A controller, pitch bend, or pressure amount at MIDI 2.0 resolution (32 bits, unsigned).
/// MIDI 1.0 values convert up with the MIDI 2.0 min-center-max scaling and back down without loss,
/// so 0 stays <see cref="Min"/>, the middle of a 7- or 14-bit range is exactly <see cref="Center"/>
/// (pitch bend at rest), and the top stays <see cref="Max"/>.
/// </summary>
public readonly record struct ControlValue(uint Value) : IComparable<ControlValue>
{
    public static readonly ControlValue Min = new(0);

    public static readonly ControlValue Center = new(0x8000_0000);

    public static readonly ControlValue Max = new(uint.MaxValue);

    public static ControlValue FromSevenBit(int value) => new(ScaleUp((uint)Guard.SevenBit(value, nameof(value)), 7));

    public static ControlValue FromFourteenBit(int value) => new(ScaleUp((uint)Guard.InRange(value, 0, FourteenBitValue.MaxValue, nameof(value)), 14));

    /// <summary>
    /// A value from 0.0 (<see cref="Min"/>) to 1.0 (<see cref="Max"/>), clamped. Device parameters use this
    /// scale (the one VST3 calls normalized), so one stored number means the same on every device.
    /// </summary>
    public static ControlValue FromFraction(double fraction) =>
        double.IsNaN(fraction) ? Min : new((uint)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * uint.MaxValue));

    /// <summary>The value as a fraction from 0.0 to 1.0.</summary>
    public double ToFraction() => Value / (double)uint.MaxValue;

    /// <summary>The value at 7-bit resolution (0-127), as MIDI 1.0 sends it.</summary>
    public int ToSevenBit() => (int)(Value >> 25);

    /// <summary>The value at 14-bit resolution (0-16383), as MIDI 1.0 pitch bend sends it.</summary>
    public int ToFourteenBit() => (int)(Value >> 18);

    public int CompareTo(ControlValue other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    // MIDI 2.0 UMP specification, "Min-Center-Max" upscaling: values above the center repeat their
    // low bits into the new low-order bits so the maximum maps to the maximum.
    private static uint ScaleUp(uint value, int bits)
    {
        var scaleBits = 32 - bits;
        var shifted = value << scaleBits;
        if (value <= 1u << (bits - 1))
        {
            return shifted;
        }

        var repeatBits = bits - 1;
        var repeat = value & ((1u << repeatBits) - 1);
        repeat = scaleBits > repeatBits ? repeat << (scaleBits - repeatBits) : repeat >> (repeatBits - scaleBits);
        while (repeat != 0)
        {
            shifted |= repeat;
            repeat >>= repeatBits;
        }

        return shifted;
    }

    public static bool operator <(ControlValue left, ControlValue right) => left.Value < right.Value;

    public static bool operator >(ControlValue left, ControlValue right) => left.Value > right.Value;

    public static bool operator <=(ControlValue left, ControlValue right) => left.Value <= right.Value;

    public static bool operator >=(ControlValue left, ControlValue right) => left.Value >= right.Value;
}
