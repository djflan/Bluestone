using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Devices;

/// <summary>Where a device's implementation runs.</summary>
public enum DeviceOrigin
{
    /// <summary>Code shipped with Bluestone, trusted and run in process.</summary>
    BuiltIn,

    /// <summary>A third-party plugin, hosted in a separate worker process (ADR 0025).</summary>
    Plugin,
}

/// <summary>The usual shapes of device, derived from what a definition takes in and puts out.</summary>
public enum DeviceCategory
{
    /// <summary>Events in, events out: transposer, arpeggiator, filter.</summary>
    MidiEffect,

    /// <summary>Events in, audio out: a synthesizer or sampler.</summary>
    Instrument,

    /// <summary>Audio in, audio out.</summary>
    AudioEffect,

    Other,
}

/// <summary>
/// One parameter of a device: its range, default, and step count. Values are stored normalized
/// (0 to 1, see <see cref="ControlValue.FromFraction"/>) and converted with <see cref="ToPlain"/>.
/// </summary>
public sealed record ParameterDescriptor
{
    public ParameterDescriptor(ParameterId id, string name, double minimum, double maximum, double defaultValue, int steps = 0, string unit = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(unit);
        if (!(minimum < maximum) || double.IsInfinity(minimum) || double.IsInfinity(maximum))
        {
            throw new ArgumentException("A parameter's range must be finite and not empty.", nameof(maximum));
        }

        if (!(defaultValue >= minimum && defaultValue <= maximum))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "The default must be inside the range.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        Id = id;
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
        Default = defaultValue;
        Steps = steps;
        Unit = unit;
    }

    public ParameterId Id { get; }

    public string Name { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Default { get; }

    /// <summary>0 for a continuous parameter; otherwise the number of intervals, so 1 is a switch and 4 has five positions (as in VST3).</summary>
    public int Steps { get; }

    public string Unit { get; }

    /// <summary>The default as a stored value.</summary>
    public ControlValue DefaultValue => ToStored(Default);

    public ControlValue ToStored(double plain) => ControlValue.FromFraction((plain - Minimum) / (Maximum - Minimum));

    /// <summary>The parameter's value in its own units, snapped to its steps when it has them.</summary>
    public double ToPlain(ControlValue stored)
    {
        var fraction = stored.ToFraction();
        if (Steps > 0)
        {
            fraction = Math.Round(fraction * Steps) / Steps;
        }

        return Minimum + (fraction * (Maximum - Minimum));
    }
}

/// <summary>
/// What a device is: its identity, what signals it takes in and puts out, which events it handles, and
/// its parameters. A definition is declarative data. The code that runs it belongs to the signal layer
/// (built-in processors) or to a plugin worker, so a project can describe a device whose implementation
/// is not installed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Consumes"/> is what the device takes out of the signal. Whatever it does not consume passes
/// through to the next device, which is how an audio effect leaves notes alone and a note processor
/// leaves audio alone. For events, <see cref="Handles"/> narrows this further: a device sees, and may
/// replace, only the classes it names; every other event passes through in order (ADR 0022).
/// </para>
/// </remarks>
public sealed record DeviceDefinition
{
    private readonly ImmutableArray<ParameterDescriptor> _parameters = [];

    public required DeviceDefinitionId Id { get; init; }

    public required string Name { get; init; }

    public DeviceOrigin Origin { get; init; }

    public string? Vendor { get; init; }

    public string? Version { get; init; }

    /// <summary>Signal kinds the device takes in. An instrument consumes <see cref="SignalKinds.Events"/>.</summary>
    public SignalKinds Consumes { get; init; }

    /// <summary>Signal kinds the device puts out. An instrument produces <see cref="SignalKinds.Audio"/>.</summary>
    public SignalKinds Produces { get; init; }

    /// <summary>The event classes the device takes in and may change or drop. Unlisted classes pass through.</summary>
    public EventClass Handles { get; init; }

    public ImmutableArray<ParameterDescriptor> Parameters
    {
        get => _parameters;
        init
        {
            var seen = new HashSet<ParameterId>();
            foreach (var parameter in value)
            {
                ArgumentNullException.ThrowIfNull(parameter, nameof(Parameters));
                if (!seen.Add(parameter.Id))
                {
                    throw new ArgumentException($"Parameter {parameter.Id} appears more than once.", nameof(Parameters));
                }
            }

            _parameters = value.IsDefault ? [] : value;
        }
    }

    public DeviceCategory Category =>
        Consumes.HasFlag(SignalKinds.Events) && Produces.HasFlag(SignalKinds.Events) ? DeviceCategory.MidiEffect
        : Consumes.HasFlag(SignalKinds.Events) && Produces.HasFlag(SignalKinds.Audio) ? DeviceCategory.Instrument
        : Consumes.HasFlag(SignalKinds.Audio) && Produces.HasFlag(SignalKinds.Audio) ? DeviceCategory.AudioEffect
        : DeviceCategory.Other;

    public ParameterDescriptor? FindParameter(ParameterId id)
    {
        foreach (var parameter in _parameters)
        {
            if (parameter.Id == id)
            {
                return parameter;
            }
        }

        return null;
    }

    public DeviceReference ToReference() => new(Id, Name, Version);
}
