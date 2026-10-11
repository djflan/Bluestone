using System.Globalization;
using System.Windows.Input;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Presentation;

/// <summary>One automation lane shown under its track in the arrangement.</summary>
public sealed record AutomationLaneViewModel(TrackViewModel Track, AutomationLaneId Id, AutomationTarget Target)
{
    /// <summary>For example "Volume (CC 7) · ch 1", "CC 74 · ch 2", "Pitch Bend · ch 1", or "Filter Cutoff · ch 1" for a hardware parameter.</summary>
    public string Name => $"{(Target.IsInstrument ? Track.HardwareParameterName(Target) : AutomationOption.NameOf(Target))} · ch {Target.Channel.Number.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A parameter of the track's external instrument part, named by its profile, offered when adding automation (ADR 0031).</summary>
/// <param name="Name">The parameter's name in the profile.</param>
/// <param name="Target">The part's parameter: the instrument, port, channel, and parameter key.</param>
/// <param name="Add">Adds a lane for it to the track.</param>
public sealed record HardwareParameterOption(string Name, AutomationTarget Target, ICommand Add)
{
    public override string ToString() => Name;
}

/// <summary>A kind of lane offered when adding automation; the channel comes from the track.</summary>
public sealed record AutomationOption(string Name, AutomationParameter Parameter, int Controller = 0)
{
    public static readonly AutomationOption Volume = new("Volume", AutomationParameter.Controller, 7);
    public static readonly AutomationOption Pan = new("Pan", AutomationParameter.Controller, 10);
    public static readonly AutomationOption Expression = new("Expression", AutomationParameter.Controller, 11);
    public static readonly AutomationOption Modulation = new("Modulation", AutomationParameter.Controller, 1);
    public static readonly AutomationOption Sustain = new("Sustain", AutomationParameter.Controller, 64);
    public static readonly AutomationOption Brightness = new("Brightness", AutomationParameter.Controller, 74);
    public static readonly AutomationOption PitchBend = new("Pitch Bend", AutomationParameter.PitchBend);
    public static readonly AutomationOption ChannelPressure = new("Channel Pressure", AutomationParameter.ChannelPressure);

    public static IReadOnlyList<AutomationOption> All { get; } = [Volume, Pan, Expression, Modulation, Sustain, Brightness, PitchBend, ChannelPressure];

    public AutomationTarget On(MidiChannel channel) => Parameter switch
    {
        AutomationParameter.PitchBend => AutomationTarget.ForPitchBend(channel),
        AutomationParameter.ChannelPressure => AutomationTarget.ForChannelPressure(channel),
        _ => AutomationTarget.ForController(channel, new ControllerNumber(Controller)),
    };

    /// <summary>The option as a menu shows it, for example "Volume (CC 7)".</summary>
    public override string ToString() => Parameter == AutomationParameter.Controller
        ? string.Create(CultureInfo.InvariantCulture, $"{Name} (CC {Controller})")
        : Name;

    internal static string NameOf(AutomationTarget target) => target.Parameter switch
    {
        AutomationParameter.PitchBend => PitchBend.Name,
        AutomationParameter.ChannelPressure => ChannelPressure.Name,
        _ => All.FirstOrDefault(o => o.Parameter == AutomationParameter.Controller && o.Controller == target.Controller.Value) is { } known
            ? string.Create(CultureInfo.InvariantCulture, $"{known.Name} (CC {known.Controller})")
            : string.Create(CultureInfo.InvariantCulture, $"CC {target.Controller.Value}"),
    };
}
