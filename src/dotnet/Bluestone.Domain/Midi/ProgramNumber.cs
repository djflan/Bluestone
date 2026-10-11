using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>
/// A MIDI program (patch) number. <see cref="Value"/> is the zero-based wire value (0-127);
/// <see cref="Number"/> is the one-based number most instruments display (1-128).
/// </summary>
public readonly record struct ProgramNumber
{
    public ProgramNumber(int value) => Value = Guard.SevenBit(value, nameof(value));

    public byte Value { get; }

    public int Number => Value + 1;

    public static ProgramNumber FromNumber(int number) => new(Guard.InRange(number, 1, 128, nameof(number)) - 1);

    public override string ToString() => Number.ToString(CultureInfo.InvariantCulture);
}
