using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>
/// A 14-bit MIDI value (0-16383) carried as two 7-bit bytes, used by pitch bend and
/// RPN/NRPN data entry. <see cref="Center"/> (8192) is the pitch-bend rest position.
/// </summary>
public readonly record struct FourteenBitValue
{
    public const int MaxValue = 0x3FFF;

    public static readonly FourteenBitValue Center = new(0x2000);

    public FourteenBitValue(int value) => Value = (ushort)Guard.InRange(value, 0, MaxValue, nameof(value));

    public ushort Value { get; }

    /// <summary>The most significant 7 bits.</summary>
    public byte Msb => (byte)(Value >> 7);

    /// <summary>The least significant 7 bits.</summary>
    public byte Lsb => (byte)(Value & 0x7F);

    /// <summary>The value relative to <see cref="Center"/>, in the range -8192 to 8191.</summary>
    public int OffsetFromCenter => Value - Center.Value;

    public static FourteenBitValue FromBytes(int msb, int lsb) =>
        new((Guard.SevenBit(msb, nameof(msb)) << 7) | Guard.SevenBit(lsb, nameof(lsb)));

    public static FourteenBitValue FromOffsetFromCenter(int offset) =>
        new(Guard.InRange(offset, -0x2000, 0x1FFF, nameof(offset)) + 0x2000);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
