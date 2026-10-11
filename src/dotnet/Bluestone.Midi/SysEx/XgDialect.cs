using System.Collections.Immutable;
using System.Globalization;

namespace Bluestone.Midi.SysEx;

/// <summary>The parameter blocks of the XG address map that Bluestone distinguishes.</summary>
public enum XgArea
{
    /// <summary><c>00 00 xx</c>: master tune, volume, transpose, System On, and resets.</summary>
    System,

    /// <summary><c>02 01 00</c>–<c>1F</c>.</summary>
    Reverb,

    /// <summary><c>02 01 20</c>–<c>3F</c>.</summary>
    Chorus,

    /// <summary><c>02 01 40</c>–<c>7F</c>.</summary>
    Variation,

    /// <summary><c>03 nn xx</c>, insertion effect <c>nn</c>.</summary>
    Insertion,

    /// <summary><c>08 nn xx</c>, part <c>nn</c>.</summary>
    MultiPart,

    /// <summary><c>3n rr xx</c>, drum setup <c>n</c>, note <c>rr</c>.</summary>
    DrumSetup,

    /// <summary>Any other address, including model-specific extensions.</summary>
    Other,
}

/// <summary>A three-byte XG parameter address (high, mid, low).</summary>
public readonly record struct XgAddress(byte High, byte Mid, byte Low)
{
    public XgArea Area => (High, Mid, Low) switch
    {
        (0x00, 0x00, _) => XgArea.System,
        (0x02, 0x01, < 0x20) => XgArea.Reverb,
        (0x02, 0x01, < 0x40) => XgArea.Chorus,
        (0x02, 0x01, _) => XgArea.Variation,
        (0x03, _, _) => XgArea.Insertion,
        (0x08, _, _) => XgArea.MultiPart,
        ( >= 0x30 and <= 0x3F, _, _) => XgArea.DrumSetup,
        _ => XgArea.Other,
    };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{High:X2} {Mid:X2} {Low:X2}");

    /// <summary>Names the block and parameter, using one-based part, insertion, and drum setup numbers.</summary>
    internal string Describe() => Area switch
    {
        XgArea.System => string.Create(CultureInfo.InvariantCulture, $"System parameter {Low:X2}"),
        XgArea.Reverb or XgArea.Chorus or XgArea.Variation => string.Create(CultureInfo.InvariantCulture, $"{Area} parameter {Low:X2}"),
        XgArea.Insertion => string.Create(CultureInfo.InvariantCulture, $"Insertion {Mid + 1} parameter {Low:X2}"),
        XgArea.MultiPart => string.Create(CultureInfo.InvariantCulture, $"Multi Part {Mid + 1} parameter {Low:X2}"),
        XgArea.DrumSetup => string.Create(CultureInfo.InvariantCulture, $"Drum Setup {(High & 0x0F) + 1} note {Mid} parameter {Low:X2}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"parameter {this}"),
    };
}

/// <summary>
/// An XG parameter change (<c>F0 43 1n 4C hh mm ll dd… F7</c>), including XG System On and All
/// Parameter Reset, which are parameter changes at <c>00 00 7E</c> and <c>00 00 7F</c>.
/// </summary>
/// <param name="DeviceNumber">The device number <c>n</c>, 0–15.</param>
/// <param name="Address">The parameter's address.</param>
/// <param name="Data">The parameter's value bytes; multi-byte parameters are as transmitted.</param>
public sealed record XgParameterChange(byte DeviceNumber, XgAddress Address, ImmutableArray<byte> Data) : SysExInterpretation
{
    public bool IsSystemOn => Address is { High: 0x00, Mid: 0x00, Low: 0x7E };

    public bool IsAllParameterReset => Address is { High: 0x00, Mid: 0x00, Low: 0x7F };

    /// <summary>For multi part parameters, the one-based part number.</summary>
    public int? Part => Address.Area == XgArea.MultiPart ? Address.Mid + 1 : null;

    public override string Summary =>
        IsSystemOn ? "XG System On"
        : IsAllParameterReset ? "XG All Parameter Reset"
        : $"XG {Address.Describe()}";
}

/// <summary>An XG bulk dump (<c>F0 43 0n 4C bh bl hh mm ll dd… cs F7</c>).</summary>
/// <param name="DeviceNumber">The device number <c>n</c>, 0–15.</param>
/// <param name="Address">The address of the first byte of the dumped block.</param>
/// <param name="Data">The dumped bytes, without byte count, address, or checksum.</param>
/// <param name="ChecksumValid">False when the checksum does not match; the message is still preserved and sent as is.</param>
public sealed record XgBulkDump(byte DeviceNumber, XgAddress Address, ImmutableArray<byte> Data, bool ChecksumValid) : SysExInterpretation
{
    public override string Summary =>
        string.Create(CultureInfo.InvariantCulture, $"XG Bulk Dump · {Address.Describe()} · {Data.Length} bytes{(ChecksumValid ? string.Empty : " · bad checksum")}");
}

/// <summary>
/// Yamaha XG (model <c>4C</c>) parameter changes and bulk dumps. Other Yamaha models, and XG
/// requests, are left uninterpreted.
/// </summary>
public static class XgDialect
{
    public const byte Yamaha = 0x43;
    public const byte Model = 0x4C;

    /// <param name="body">The message without <c>F0</c> and <c>F7</c>, starting with <c>43</c>.</param>
    internal static SysExInterpretation? Interpret(ReadOnlySpan<byte> body)
    {
        // 43, device byte (high nibble: 1 = parameter change, 0 = bulk dump), model.
        if (body.Length < 3 || body[2] != Model)
        {
            return null;
        }

        var device = (byte)(body[1] & 0x0F);
        switch (body[1] >> 4)
        {
            case 0x1 when body.Length >= 7:
                return new XgParameterChange(device, new XgAddress(body[3], body[4], body[5]), [.. body[6..]]);
            case 0x0 when body.Length >= 9:
                // Byte count (2), address (3), data, checksum. The checksum makes count through checksum sum to 0 (mod 128).
                var count = (body[3] << 7) | body[4];
                var data = body[8..^1];
                if (data.Length != count)
                {
                    return null;
                }

                var sum = 0;
                foreach (var b in body[3..])
                {
                    sum += b;
                }

                return new XgBulkDump(device, new XgAddress(body[5], body[6], body[7]), [.. data], (sum & 0x7F) == 0);
            default:
                return null;
        }
    }
}
