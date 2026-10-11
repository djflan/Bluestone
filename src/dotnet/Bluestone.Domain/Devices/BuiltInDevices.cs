using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Devices;

public enum ArpeggiatorPattern
{
    Up,
    Down,

    /// <summary>Up, then down, without repeating the top and bottom notes.</summary>
    UpDown,
}

/// <summary>
/// The devices that ship with Bluestone, as definitions: what they are, which events they handle, and their
/// parameters. They are data like any other definition; the processors that run them live in the signal
/// layer (ADR 0022). Their IDs and parameter IDs are stable and are stored in projects.
/// </summary>
public static class BuiltInDevices
{
    public const int MaxTranspose = 48;
    public const int MaxArpeggiatorOctaves = 4;

    public static readonly ParameterId TransposeSemitones = new(1);

    public static readonly ParameterId FilterNotes = new(1);
    public static readonly ParameterId FilterControllers = new(2);
    public static readonly ParameterId FilterPrograms = new(3);
    public static readonly ParameterId FilterSystemExclusive = new(4);

    public static readonly ParameterId ArpeggiatorRate = new(1);
    public static readonly ParameterId ArpeggiatorPatternParameter = new(2);
    public static readonly ParameterId ArpeggiatorOctaves = new(3);
    public static readonly ParameterId ArpeggiatorGate = new(4);

    /// <summary>Moves notes by -48 to 48 semitones; notes leaving 0-127 are dropped and reported.</summary>
    public static DeviceDefinition Transpose { get; } = new()
    {
        Id = new DeviceDefinitionId("bluestone.midi.transpose"),
        Name = "Transpose",
        Origin = DeviceOrigin.BuiltIn,
        Vendor = "Bluestone",
        Version = "1",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Notes,
        Parameters = [new ParameterDescriptor(TransposeSemitones, "Semitones", -MaxTranspose, MaxTranspose, 0, steps: 2 * MaxTranspose, unit: "st")],
    };

    /// <summary>Removes whole classes of events. The explicit way to drop events; each switch passes its class when on.</summary>
    public static DeviceDefinition EventFilter { get; } = new()
    {
        Id = new DeviceDefinitionId("bluestone.midi.event-filter"),
        Name = "Event Filter",
        Origin = DeviceOrigin.BuiltIn,
        Vendor = "Bluestone",
        Version = "1",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Transmitted,
        Parameters =
        [
            Switch(FilterNotes, "Pass notes"),
            Switch(FilterControllers, "Pass controllers"),
            Switch(FilterPrograms, "Pass program changes"),
            Switch(FilterSystemExclusive, "Pass system exclusive"),
        ],
    };

    /// <summary>Plays held notes one at a time. Rate 0 to 3 is quarters, eighths, sixteenths, thirty-seconds.</summary>
    public static DeviceDefinition Arpeggiator { get; } = new()
    {
        Id = new DeviceDefinitionId("bluestone.midi.arpeggiator"),
        Name = "Arpeggiator",
        Origin = DeviceOrigin.BuiltIn,
        Vendor = "Bluestone",
        Version = "1",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Notes,
        Parameters =
        [
            new ParameterDescriptor(ArpeggiatorRate, "Rate", 0, 3, 2, steps: 3),
            new ParameterDescriptor(ArpeggiatorPatternParameter, "Pattern", 0, 2, 0, steps: 2),
            new ParameterDescriptor(ArpeggiatorOctaves, "Octaves", 1, MaxArpeggiatorOctaves, 1, steps: MaxArpeggiatorOctaves - 1),
            new ParameterDescriptor(ArpeggiatorGate, "Gate", 0.05, 1, 0.5, unit: "%"),
        ],
    };

    /// <summary>The event classes each Event Filter switch passes, in parameter order.</summary>
    public static IReadOnlyList<(ParameterId Parameter, EventClass Class)> FilterSwitches { get; } =
    [
        (FilterNotes, EventClass.Notes),
        (FilterControllers, EventClass.Controllers),
        (FilterPrograms, EventClass.Programs),
        (FilterSystemExclusive, EventClass.SystemExclusive),
    ];

    public static IReadOnlyList<DeviceDefinition> All { get; } = [Transpose, EventFilter, Arpeggiator];

    public static DeviceDefinition? Find(DeviceDefinitionId id)
    {
        foreach (var definition in All)
        {
            if (definition.Id == id)
            {
                return definition;
            }
        }

        return null;
    }

    public static bool IsTranspose(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Definition.Id == Transpose.Id;
    }

    public static DeviceInstance CreateTranspose(int semitones) => WithTranspose(DeviceInstance.Create(Transpose.ToReference()), semitones);

    /// <summary><paramref name="device"/> (a Transpose device) set to <paramref name="semitones"/>.</summary>
    public static DeviceInstance WithTranspose(DeviceInstance device, int semitones)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(semitones, -MaxTranspose);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(semitones, MaxTranspose);
        return device.WithParameter(TransposeSemitones, Transpose.Parameters[0].ToStored(semitones));
    }

    /// <summary>The semitones a Transpose device is set to.</summary>
    public static int TransposeOf(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return SemitonesFrom(device.ValueOf(TransposeSemitones) ?? Transpose.Parameters[0].DefaultValue);
    }

    /// <summary>A stored Transpose value in semitones.</summary>
    public static int SemitonesFrom(ControlValue stored) => (int)Math.Round(Transpose.Parameters[0].ToPlain(stored));

    /// <summary>An Event Filter device that removes <paramref name="blocked"/>.</summary>
    public static DeviceInstance CreateEventFilter(EventClass blocked)
    {
        var device = DeviceInstance.Create(EventFilter.ToReference());
        foreach (var (parameter, eventClass) in FilterSwitches)
        {
            device = device.WithParameter(parameter, blocked.HasFlag(eventClass) ? ControlValue.Min : ControlValue.Max);
        }

        return device;
    }

    /// <summary>An Arpeggiator device; <paramref name="rate"/> is 0 (quarters) to 3 (thirty-seconds).</summary>
    public static DeviceInstance CreateArpeggiator(int rate = 2, ArpeggiatorPattern pattern = ArpeggiatorPattern.Up, int octaves = 1, double gate = 0.5) =>
        DeviceInstance.Create(Arpeggiator.ToReference())
            .WithParameter(ArpeggiatorRate, Arpeggiator.Parameters[0].ToStored(rate))
            .WithParameter(ArpeggiatorPatternParameter, Arpeggiator.Parameters[1].ToStored((int)pattern))
            .WithParameter(ArpeggiatorOctaves, Arpeggiator.Parameters[2].ToStored(octaves))
            .WithParameter(ArpeggiatorGate, Arpeggiator.Parameters[3].ToStored(gate));

    private static ParameterDescriptor Switch(ParameterId id, string name) => new(id, name, 0, 1, 1, steps: 1);
}
