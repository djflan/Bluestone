using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>A generic 7-bit MIDI data value (0-127), such as a controller value or pressure amount.</summary>
public readonly record struct SevenBitValue
{
    public static readonly SevenBitValue Min = new(0);

    public static readonly SevenBitValue Max = new(127);

    public SevenBitValue(int value) => Value = Guard.SevenBit(value, nameof(value));

    public byte Value { get; }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
