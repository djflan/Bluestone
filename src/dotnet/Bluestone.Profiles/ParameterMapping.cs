using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Profiles;

/// <summary>How a mapped parameter is reached over MIDI 1.0.</summary>
public enum ParameterMappingKind
{
    /// <summary>A control change, 7-bit, or 14-bit as an MSB controller (0-31) and its LSB partner (32-63).</summary>
    Controller,

    /// <summary>A registered parameter: CC 101/100 select it, CC 6 (and CC 38 for 14 bits) set it.</summary>
    Rpn,

    /// <summary>A non-registered parameter: CC 99/98 select it, CC 6 (and CC 38 for 14 bits) set it.</summary>
    Nrpn,

    /// <summary>A SysEx message from a template with the value, channel, device number, and checksum filled in.</summary>
    SystemExclusive,
}

/// <summary>Which vendor's checksum a SysEx parameter message ends with. Both are the 7-bit sum to zero (see <see cref="SysExChecksum"/>).</summary>
public enum ChecksumConvention
{
    /// <summary>Roland data set (DT1): covers the address and data.</summary>
    Roland,

    /// <summary>Yamaha: covers the byte count (if any), address, and data.</summary>
    Yamaha,
}

/// <summary>What a token in a SysEx parameter template stands for.</summary>
internal enum MappingTokenKind
{
    Literal,

    /// <summary>Bits <c>Shift</c> to <c>Shift + Width − 1</c> of the value.</summary>
    Value,

    /// <summary>The part's channel, 0-15.</summary>
    Channel,

    /// <summary>The template's device number byte.</summary>
    Device,

    /// <summary>The checksum of the bytes from the checksum's start up to this token.</summary>
    Checksum,
}

internal readonly record struct MappingToken(MappingTokenKind Kind, byte Literal = 0, int Shift = 0, int Width = 0);

/// <summary>
/// One named parameter of an external instrument and the MIDI 1.0 that sets it (ADR 0031): a control change
/// (7 or 14 bits), an RPN or NRPN (with 7- or 14-bit data entry), or a SysEx template. Automation values are
/// normalized (0 to 1) and scaled to the parameter's range (<see cref="Min"/> to <see cref="Max"/>, in the
/// integers the instrument receives). Validated when the profile loads.
/// </summary>
public sealed record ParameterMapping
{
    /// <summary>The highest value a SysEx parameter can hold (16 bits).</summary>
    public const int MaxSysExValue = 0xFFFF;

    /// <summary>The RPN null selection: CC 101 and CC 100 at 127 deselect any parameter.</summary>
    public const int NullParameter = 127;

    internal ParameterMapping(string id, string name, ParameterMappingKind kind, int bits, int min, int max, int? defaultValue)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Bits = bits;
        Min = min;
        Max = max;
        Default = defaultValue;
    }

    /// <summary>The parameter's key in the profile; automation lanes name it.</summary>
    public string Id { get; }

    public string Name { get; }

    public ParameterMappingKind Kind { get; }

    /// <summary>7 or 14 for controllers, RPNs, and NRPNs; for SysEx, the bits <see cref="Max"/> needs.</summary>
    public int Bits { get; }

    /// <summary>The value sent for a normalized 0.</summary>
    public int Min { get; }

    /// <summary>The value sent for a normalized 1.</summary>
    public int Max { get; }

    /// <summary>The instrument's own default, when the profile states it.</summary>
    public int? Default { get; }

    /// <summary>The controller (the MSB controller for 14 bits), for <see cref="ParameterMappingKind.Controller"/>.</summary>
    public ControllerNumber Controller { get; init; }

    /// <summary>The parameter number's MSB, for RPNs and NRPNs.</summary>
    public SevenBitValue ParameterMsb { get; init; }

    /// <summary>The parameter number's LSB, for RPNs and NRPNs.</summary>
    public SevenBitValue ParameterLsb { get; init; }

    /// <summary>For RPNs and NRPNs: whether the RPN null (CC 101/100 at 127) follows, so a stray data entry changes nothing.</summary>
    public bool NullAfter { get; init; }

    /// <summary>The device number byte a SysEx template's <c>{device}</c> sends.</summary>
    public SevenBitValue DeviceNumber { get; init; }

    /// <summary>The checksum convention of a SysEx template with a <c>{checksum}</c>, or null.</summary>
    public ChecksumConvention? Checksum { get; init; }

    /// <summary>The position (F0 is 0) of the first byte the checksum covers.</summary>
    public int ChecksumStart { get; init; }

    internal ImmutableArray<MappingToken> Tokens { get; init; } = [];

    /// <summary>A SysEx template as written, for display; null for other kinds.</summary>
    public string? Pattern => Kind != ParameterMappingKind.SystemExclusive ? null : string.Join(' ', Tokens.Select(t => t.Kind switch
    {
        MappingTokenKind.Literal => t.Literal.ToString("X2", CultureInfo.InvariantCulture),
        MappingTokenKind.Value => t.Shift == 0 && t.Width == 7 ? "{value}" : string.Create(CultureInfo.InvariantCulture, $"{{value[{t.Shift + t.Width - 1}:{t.Shift}]}}"),
        MappingTokenKind.Channel => "{channel}",
        MappingTokenKind.Device => "{device}",
        _ => "{checksum}",
    }));

    /// <summary>The value sent for <paramref name="value"/>: <see cref="Min"/> + round(fraction × (<see cref="Max"/> − <see cref="Min"/>)).</summary>
    public int Scale(ControlValue value) =>
        Min + (int)Math.Round(value.ToFraction() * (Max - Min), MidpointRounding.AwayFromZero);

    /// <summary>
    /// The events that set the parameter to <paramref name="value"/> on the part on <paramref name="channel"/>, in
    /// the order they must be sent: controllers (MSB before LSB; select, data entry, then the optional null) or one
    /// SysEx message. They are rendered, not stored, so they have no IDs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside <see cref="Min"/> to <see cref="Max"/>.</exception>
    public ImmutableArray<TrackEvent> Encode(int value, MidiChannel channel, Tick position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, Min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, Max);
        switch (Kind)
        {
            case ParameterMappingKind.Controller when Bits == 14:
                return [Cc(Controller.Value, value >> 7), Cc(Controller.Value + 32, value & 0x7F)];
            case ParameterMappingKind.Controller:
                return [Cc(Controller.Value, value)];
            case ParameterMappingKind.Rpn or ParameterMappingKind.Nrpn:
                var events = ImmutableArray.CreateBuilder<TrackEvent>(6);
                var (msb, lsb) = Kind == ParameterMappingKind.Rpn ? (101, 100) : (99, 98);
                events.Add(Cc(msb, ParameterMsb.Value));
                events.Add(Cc(lsb, ParameterLsb.Value));
                events.Add(Cc(6, Bits == 14 ? value >> 7 : value));
                if (Bits == 14)
                {
                    events.Add(Cc(38, value & 0x7F));
                }

                if (NullAfter)
                {
                    events.Add(Cc(101, NullParameter));
                    events.Add(Cc(100, NullParameter));
                }

                return events.ToImmutable();
            default:
                return [new SysExEvent(default, position, RenderSysEx(value, channel))];
        }

        ControllerEvent Cc(int controller, int data) => new(default, position, channel, new ControllerNumber(controller), ControlValue.FromSevenBit(data));
    }

    private SysExMessage RenderSysEx(int value, MidiChannel channel)
    {
        var bytes = new byte[Tokens.Length];
        for (var i = 0; i < Tokens.Length; i++)
        {
            var token = Tokens[i];
            bytes[i] = token.Kind switch
            {
                MappingTokenKind.Literal => token.Literal,
                MappingTokenKind.Value => (byte)((value >> token.Shift) & ((1 << token.Width) - 1)),
                MappingTokenKind.Channel => channel.Index,
                MappingTokenKind.Device => DeviceNumber.Value,
                _ => SysExChecksum.Compute(bytes.AsSpan(ChecksumStart, i - ChecksumStart)),
            };
        }

        return SysExMessage.Create(bytes);
    }
}
