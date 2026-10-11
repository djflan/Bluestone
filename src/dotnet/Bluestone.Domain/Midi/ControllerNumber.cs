using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>A MIDI 1.0 control change number (0-127), with names for the defined controllers Bluestone relies on.</summary>
public readonly record struct ControllerNumber
{
    public static readonly ControllerNumber BankSelectMsb = new(0);
    public static readonly ControllerNumber ModulationWheel = new(1);
    public static readonly ControllerNumber DataEntryMsb = new(6);
    public static readonly ControllerNumber ChannelVolume = new(7);
    public static readonly ControllerNumber Pan = new(10);
    public static readonly ControllerNumber Expression = new(11);
    public static readonly ControllerNumber BankSelectLsb = new(32);
    public static readonly ControllerNumber DataEntryLsb = new(38);
    public static readonly ControllerNumber SustainPedal = new(64);
    public static readonly ControllerNumber DataIncrement = new(96);
    public static readonly ControllerNumber DataDecrement = new(97);
    public static readonly ControllerNumber NrpnLsb = new(98);
    public static readonly ControllerNumber NrpnMsb = new(99);
    public static readonly ControllerNumber RpnLsb = new(100);
    public static readonly ControllerNumber RpnMsb = new(101);
    public static readonly ControllerNumber AllSoundOff = new(120);
    public static readonly ControllerNumber ResetAllControllers = new(121);
    public static readonly ControllerNumber AllNotesOff = new(123);

    public ControllerNumber(int value) => Value = Guard.SevenBit(value, nameof(value));

    public byte Value { get; }

    /// <summary>True for bank select MSB (0) and LSB (32), which must precede a program change to take effect.</summary>
    public bool IsBankSelect => Value is 0 or 32;

    /// <summary>True for channel mode messages (120-127), which are commands rather than continuous controllers.</summary>
    public bool IsChannelMode => Value >= 120;

    /// <summary>True for the RPN/NRPN parameter-number selectors (98-101).</summary>
    public bool IsParameterNumberSelector => Value is >= 98 and <= 101;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
