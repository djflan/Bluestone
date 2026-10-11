using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Signal;

/// <summary>Events, chains, projects, and a catalog for signal tests: the built-ins plus the test instrument and a plugin.</summary>
internal static class SignalFixture
{
    public static readonly Ppqn Resolution = new(480);

    /// <summary>The XG System On message.</summary>
    public static readonly SysExMessage XgSystemOn = SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7]);

    public static readonly DeviceDefinition Recorder = new()
    {
        Id = new DeviceDefinitionId("test.recorder"),
        Name = "Recorder",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Notes,
    };

    public static readonly DeviceDefinition PluginArp = new()
    {
        Id = new DeviceDefinitionId("vst3:0123456789ABCDEF"),
        Name = "Plugin Arp",
        Origin = DeviceOrigin.Plugin,
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Notes,
    };

    public static DeviceCatalog Catalog { get; } = DeviceCatalog.BuiltIn
        .With(TestDevices.Synth)
        .With(TestDevices.Filter)
        .With(PluginArp);

    /// <summary>The catalog, with <see cref="Recorder"/> devices run by <paramref name="recorder"/>.</summary>
    public static DeviceCatalog CatalogWith(RecordingProcessor recorder) => Catalog.With(Recorder, _ => recorder);

    public static NoteEvent Note(long at, int note = 60, int channel = 1, long length = 120) =>
        new(new Tick(at), new TickSpan(length), MidiChannel.FromNumber(channel), new NoteNumber(note), new Velocity(100));

    public static ControllerEvent Volume(long at, int value, int channel = 1) =>
        new(new Tick(at), MidiChannel.FromNumber(channel), new ControllerNumber(7), ControlValue.FromSevenBit(value));

    public static ProgramEvent Program(long at, int program, int channel = 1) =>
        new(new Tick(at), MidiChannel.FromNumber(channel), new ProgramSelection(new ProgramNumber(program)));

    public static SysExEvent XgOn(long at = 0) => new(new Tick(at), XgSystemOn);

    /// <summary>Events as they come out of the track at index <paramref name="origin"/>: in canonical order, keyed by their index.</summary>
    public static SignalEvent[] Keyed(int origin, params TrackEvent[] events) =>
        [.. events.Order(Comparer<TrackEvent>.Create(EventOrder.Compare)).Select((e, i) => new SignalEvent(e, origin, i))];

    public static DeviceChain Chain(params DeviceInstance[] devices) =>
        DeviceChain.Create(ChainOwner.Rack, "test") with { Devices = [.. devices] };

    public static DeviceInstance Instance(DeviceDefinition definition) => DeviceInstance.Create(definition.ToReference());

    public static ImmutableArray<SignalEvent> Run(DeviceChain chain, ReadOnlySpan<SignalEvent> input, DeviceCatalog? catalog = null, ImmutableArray<ParameterChange> changes = default, IChainObserver? observer = null)
    {
        var runner = new ChainRunner(chain, catalog ?? Catalog, Resolution);
        var output = new SignalBuffer();
        runner.Run(SignalBlock.Everything, input, output, changes.IsDefault ? [] : changes.AsSpan(), observer);
        return [.. output.Events];
    }

    public static IEnumerable<NoteEvent> Notes(this IEnumerable<SignalEvent> events) => events.Select(e => e.Event).OfType<NoteEvent>();

    public static Track TrackOf(string name, params TrackEvent[] events) => Track.FromEvents(TrackId.New(), name, events);

    public static Project NewProject() => Project.CreateNew("signal") with { Sequence = Sequence.CreateEmpty(Resolution) };

    public static Project With(this Project project, Track track, params DeviceInstance[] devices)
    {
        project = project with { Sequence = project.Sequence.WithTrack(track) };
        return devices.Length == 0 ? project : project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [.. devices] });
    }

    public static Project WithRack(this Project project, out DeviceChain rack, string name, params DeviceInstance[] devices)
    {
        rack = DeviceChain.Create(ChainOwner.Rack, name) with { Devices = [.. devices] };
        return project.WithChain(rack);
    }

    public static Project WithInstrument(this Project project, out ExternalInstrument instrument, string name = "MU2000")
    {
        instrument = ExternalInstrument.Create(name, new EndpointReference("coremidi", "1", name)).WithPort(new ExternalPort("B", "Port B", new EndpointReference("coremidi", "2", name + " B")));
        return project with { Instruments = project.Instruments.Add(instrument) };
    }

    public static Project Connect(this Project project, SignalNode source, SignalNode destination, ChannelMapping? mapping = null, SignalKind kind = SignalKind.Events) =>
        project.Connect(out _, source, destination, mapping, kind);

    public static Project Connect(this Project project, out SignalConnection connection, SignalNode source, SignalNode destination, ChannelMapping? mapping = null, SignalKind kind = SignalKind.Events)
    {
        connection = SignalConnection.Create(kind, source, destination) with { Mapping = mapping ?? ChannelMapping.Preserve };
        return project with { Connections = project.Connections.Add(connection) };
    }

    public static Project WithMixerChannel(this Project project, out MixerChannel channel)
    {
        channel = MixerChannel.Create("Bus");
        return project with { Mixer = project.Mixer.With(channel) };
    }

    public static ImmutableArray<RoutingIssue> Validate(this Project project) => SignalRoutingValidator.Validate(project, Catalog.Lookup);

    public static SignalGraphResult Evaluate(this Project project) => SignalGraph.Evaluate(project, Catalog);

    public static ExternalPartFeed PartFor(this SignalGraphResult result, SignalConnection connection) =>
        Assert.Single(result.ExternalParts, p => p.Connection == connection.Id);

    public static SoftwareInstrumentFeed InstrumentFor(this SignalGraphResult result, DeviceInstance device) =>
        Assert.Single(result.SoftwareInstruments, i => i.Device == device.Id);
}

/// <summary>A real processor that passes its input on and keeps everything it was given.</summary>
internal sealed class RecordingProcessor : ISignalProcessor
{
    public List<TrackEvent> Received { get; } = [];

    public List<(ParameterId Id, ControlValue Value)> Parameters { get; } = [];

    public void SetParameter(ParameterId parameter, ControlValue value) => Parameters.Add((parameter, value));

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        foreach (var e in input)
        {
            Received.Add(e.Event);
            output.Add(e);
        }
    }

    public void Reset()
    {
    }
}

/// <summary>Keeps what a chain shows after each device and at instrument inputs.</summary>
internal sealed class CollectingObserver : IChainObserver
{
    public Dictionary<DeviceId, ImmutableArray<SignalEvent>> After { get; } = [];

    public Dictionary<DeviceId, ImmutableArray<SignalEvent>> Instruments { get; } = [];

    public void AfterDevice(DeviceId device, ReadOnlySpan<SignalEvent> events) => After[device] = [.. events];

    public void InstrumentInput(DeviceId device, ReadOnlySpan<SignalEvent> events) => Instruments[device] = [.. events];
}
