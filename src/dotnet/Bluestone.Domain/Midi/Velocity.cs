using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>
/// A 7-bit note velocity (0-127). On the wire a note-on with velocity 0 means note-off,
/// so sounding notes require <see cref="IsAudible"/>.
/// </summary>
public readonly record struct Velocity
{
    public static readonly Velocity Off = new(0);

    /// <summary>The conventional release velocity used when none is specified.</summary>
    public static readonly Velocity DefaultRelease = new(64);

    public static readonly Velocity Max = new(127);

    public Velocity(int value) => Value = Guard.SevenBit(value, nameof(value));

    public byte Value { get; }

    public bool IsAudible => Value > 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
