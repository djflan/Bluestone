using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Midi;

namespace Bluestone.Midi.SysEx;

public enum UniversalMessageKind
{
    /// <summary>A well-formed Universal message whose sub-IDs Bluestone does not name.</summary>
    Other,
    IdentityRequest,
    IdentityReply,
    GeneralMidiOn,
    GeneralMidiOff,
    GeneralMidi2On,
    MasterVolume,
    MasterBalance,
    MasterFineTuning,
    MasterCoarseTuning,
}

/// <summary>The device a MIDI Identity Reply describes. Family and member are as transmitted (LSB first).</summary>
public sealed record DeviceIdentity(ImmutableArray<byte> ManufacturerId, int Family, int Member, ImmutableArray<byte> Version);

/// <summary>A standardized (manufacturer-independent) SysEx message.</summary>
/// <param name="Kind">What the sub-IDs identify, or <see cref="UniversalMessageKind.Other"/>.</param>
/// <param name="IsRealtime">True for Universal Real Time (<c>7F</c>), false for Non-Real Time (<c>7E</c>).</param>
/// <param name="SubId1">Sub-ID #1, the message category.</param>
/// <param name="SubId2">Sub-ID #2, the message within the category.</param>
/// <param name="DeviceId">The target or source device; <c>7F</c> means all devices.</param>
public sealed record UniversalMessage(UniversalMessageKind Kind, bool IsRealtime, byte DeviceId, byte SubId1, byte SubId2) : SysExInterpretation
{
    /// <summary>For master volume, balance, and tuning, the 14-bit value.</summary>
    public FourteenBitValue? Value { get; init; }

    /// <summary>For an identity reply, the device that replied.</summary>
    public DeviceIdentity? Identity { get; init; }

    public override string Summary => Kind switch
    {
        UniversalMessageKind.IdentityRequest => "Identity Request",
        UniversalMessageKind.IdentityReply => string.Create(CultureInfo.InvariantCulture, $"Identity Reply · manufacturer {Convert.ToHexString(Identity!.ManufacturerId.AsSpan())}"),
        UniversalMessageKind.GeneralMidiOn => "GM System On",
        UniversalMessageKind.GeneralMidiOff => "GM System Off",
        UniversalMessageKind.GeneralMidi2On => "GM2 System On",
        UniversalMessageKind.MasterVolume => "Master Volume",
        UniversalMessageKind.MasterBalance => "Master Balance",
        UniversalMessageKind.MasterFineTuning => "Master Fine Tuning",
        UniversalMessageKind.MasterCoarseTuning => "Master Coarse Tuning",
        _ => string.Create(CultureInfo.InvariantCulture, $"Universal {(IsRealtime ? "Real Time" : "Non-Real Time")} {SubId1:X2} {SubId2:X2}"),
    };
}

/// <summary>Universal Non-Real Time (<c>7E</c>) and Real Time (<c>7F</c>) SysEx, as defined by the MIDI 1.0 specification.</summary>
public static class UniversalDialect
{
    public const byte NonRealtime = 0x7E;
    public const byte Realtime = 0x7F;

    /// <param name="body">The message without <c>F0</c> and <c>F7</c>, starting with <c>7E</c> or <c>7F</c>.</param>
    internal static UniversalMessage? Interpret(ReadOnlySpan<byte> body)
    {
        // ID, device, sub-ID #1, sub-ID #2.
        if (body.Length < 4)
        {
            return null;
        }

        var realtime = body[0] == Realtime;
        var (device, sub1, sub2) = (body[1], body[2], body[3]);
        var data = body[4..];
        var kind = (realtime, sub1, sub2) switch
        {
            (false, 0x06, 0x01) => UniversalMessageKind.IdentityRequest,
            (false, 0x06, 0x02) => UniversalMessageKind.IdentityReply,
            (false, 0x09, 0x01) => UniversalMessageKind.GeneralMidiOn,
            (false, 0x09, 0x02) => UniversalMessageKind.GeneralMidiOff,
            (false, 0x09, 0x03) => UniversalMessageKind.GeneralMidi2On,
            (true, 0x04, 0x01) => UniversalMessageKind.MasterVolume,
            (true, 0x04, 0x02) => UniversalMessageKind.MasterBalance,
            (true, 0x04, 0x03) => UniversalMessageKind.MasterFineTuning,
            (true, 0x04, 0x04) => UniversalMessageKind.MasterCoarseTuning,
            _ => UniversalMessageKind.Other,
        };

        var message = new UniversalMessage(kind, realtime, device, sub1, sub2);
        switch (kind)
        {
            case UniversalMessageKind.MasterVolume or UniversalMessageKind.MasterBalance
                or UniversalMessageKind.MasterFineTuning or UniversalMessageKind.MasterCoarseTuning:
                // Value is sent LSB first.
                return data.Length == 2 ? message with { Value = FourteenBitValue.FromBytes(data[1], data[0]) } : message with { Kind = UniversalMessageKind.Other };
            case UniversalMessageKind.IdentityReply:
                return ReadIdentity(data) is { } identity ? message with { Identity = identity } : message with { Kind = UniversalMessageKind.Other };
            default:
                return message;
        }
    }

    // Manufacturer (1 byte, or 00 plus 2), family (2), member (2), software revision (4).
    private static DeviceIdentity? ReadIdentity(ReadOnlySpan<byte> data)
    {
        var idLength = data.Length > 0 && data[0] == 0x00 ? 3 : 1;
        if (data.Length != idLength + 8)
        {
            return null;
        }

        var rest = data[idLength..];
        return new DeviceIdentity([.. data[..idLength]], rest[0] | (rest[1] << 7), rest[2] | (rest[3] << 7), [.. rest[4..]]);
    }
}
