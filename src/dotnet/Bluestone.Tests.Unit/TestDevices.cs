using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Tests.Unit;

/// <summary>
/// Device definitions for tests: a MIDI effect, an instrument, and two audio effects. They are only data;
/// tests that need to run one supply a processor for it.
/// </summary>
internal static class TestDevices
{
    public static readonly DeviceDefinition MidiFx = new()
    {
        Id = new DeviceDefinitionId("test.midi-fx"),
        Name = "MIDI FX",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Notes,
        Parameters = [new ParameterDescriptor(new ParameterId(1), "Rate", 1, 16, 4, steps: 15)],
    };

    public static readonly DeviceDefinition Synth = new()
    {
        Id = new DeviceDefinitionId("test.synth"),
        Name = "Synth",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Audio,
        Handles = EventClass.Notes | EventClass.Controllers | EventClass.Programs,
        Parameters = [new ParameterDescriptor(new ParameterId(1), "Release", 0, 5, 0.3, unit: "s")],
    };

    public static readonly DeviceDefinition Filter = new()
    {
        Id = new DeviceDefinitionId("test.filter"),
        Name = "Filter",
        Consumes = SignalKinds.Audio,
        Produces = SignalKinds.Audio,
        Parameters = [new ParameterDescriptor(new ParameterId(1), "Cutoff", 20, 20_000, 1000, unit: "Hz")],
    };

    public static readonly DeviceDefinition Delay = new()
    {
        Id = new DeviceDefinitionId("test.delay"),
        Name = "Delay",
        Consumes = SignalKinds.Audio,
        Produces = SignalKinds.Audio,
        Parameters = [new ParameterDescriptor(new ParameterId(1), "Feedback", 0, 1, 0.4)],
    };

    public static ImmutableArray<DeviceDefinition> All => [MidiFx, Synth, Filter, Delay];

    public static DeviceDefinition? Lookup(DeviceDefinitionId id) => All.FirstOrDefault(d => d.Id == id);

    public static DeviceInstance Instance(DeviceDefinition definition) => DeviceInstance.Create(definition.ToReference());
}
