using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Routing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Profiles;
using Bluestone.Signal;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// A plugin MIDI effect's output reaches the plan and is routed downstream (prompt section 17): the reference
/// Transpose plugin runs in a real worker when the plan is compiled. When the worker is gone or hung, the events pass
/// through unchanged and the plan says why; compiling never fails. Workers are really killed and really hung.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginMidiInThePlanTests
{
    private static readonly ParameterId Semitones = new(0);
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static ControlValue SemitonesValue(int semitones) => ControlValue.FromFraction((semitones + 24) / 48.0);

    private static NoteEvent Note(long at, int note, long length = 240) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber(note), new Velocity(100));

    // Lead: two notes, a chord, a volume controller and an XG SysEx message; Lead → [Reference Transpose +7] → port.
    private static (ProjectSession Session, Track Lead, DeviceInstance Transpose, LoopbackPort Port, EndpointDirectory Directory) Setup(LoopbackMidiProvider provider, TrackEvent[]? content = null, Ppqn? ppqn = null, int semitones = 7)
    {
        var port = provider.CreatePort("Synth", "synth");
        var directory = new EndpointDirectory([provider]);
        TrackEvent[] events = content ??
        [
            Note(0, 60),
            new ControllerEvent(new Tick(0), One, new ControllerNumber(7), ControlValue.FromSevenBit(100)),
            new SysExEvent(new Tick(0), SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7])),
            Note(240, 64),
            Note(480, 60),
            Note(480, 67),
        ];
        var lead = Track.FromEvents(TrackId.New(), "Lead", events);
        var transpose = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Transpose).ToReference()).WithParameter(Semitones, SemitonesValue(semitones));
        var synth = ExternalInstrument.Create("Synth", new EndpointReference(LoopbackMidiProvider.ProviderId, port.OutputId.Value, "Synth"));
        var session = new ProjectSession();
        var project = session.Project with
        {
            Sequence = (ppqn is { } resolution ? Sequence.CreateEmpty(resolution) : session.Project.Sequence).WithTrack(lead),
            Instruments = [synth],
            Connections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(lead.Id), SignalNode.ExternalPart(synth.Id, synth.Ports[0].Id))],
        };
        session.Execute(new ProjectCommand("Set up", _ => project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(lead.Id)) with { Devices = [transpose] })));
        return (session, lead, transpose, port, directory);
    }

    private static string[] Notes(SignalGraphResult graph) =>
    [
        .. Assert.Single(graph.ExternalParts).Events.Select(e => e.Event switch
        {
            NoteEvent n => $"{n.Position.Value}:{n.Note.Value}/{n.Duration.Value}",
            ControllerEvent c => $"{c.Position.Value}:cc{c.Controller.Value}",
            SysExEvent s => $"{s.Position.Value}:sysex{s.Message.Length}",
            _ => e.Event.GetType().Name,
        }),
    ];

    [Fact]
    public async Task APluginMidiEffect_TransposesTheNotesInThePlan_AndTheTransposedNotesReachTheOutput()
    {
        await using var host = new PluginTestHost();
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        var (session, _, transpose, port, directory) = Setup(provider);
        using var _ = directory;
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Transpose]);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(transpose.Id)!);
        var catalog = bridge.Extend(DeviceCatalog.BuiltIn);

        var prepared = PlaybackRouting.Prepare(session.Project, catalog, ProfileCatalog.Empty, directory.GetEndpoints());

        // Notes move up a fifth with their positions and lengths intact; the controller and SysEx go around the plugin.
        Assert.Equal(["0:sysex9", "0:cc7", "0:67/240", "240:71/240", "480:67/240", "480:74/240"], Notes(prepared.Graph));
        Assert.DoesNotContain(prepared.Graph.Diagnostics, d => d.Device == transpose.Id);
        var plan = prepared.Compile(session.Project.Sequence);
        using var outputs = await PlaybackRouting.OpenAsync(prepared, directory, Ct);
        using var engine = new PlaybackEngine(clock, session.Project.Sequence.TempoMap);
        engine.SetOutputs(outputs.Outputs);
        engine.Load(plan);
        engine.Play(Tick.Zero);
        for (var i = 0; i < 40; i++)
        {
            engine.Pump();
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        var sent = port.Sent.Select(m => Convert.ToHexString(m.Bytes)).ToArray();
        Assert.Contains("904364", sent);
        Assert.Contains("904A64", sent);
        Assert.DoesNotContain("903C64", sent);
        Assert.Contains("F043104C00007E00F7", sent);
    }

    [Fact]
    public async Task AutomationOfThePluginsParameter_ChangesItsOutputFromThatTickOn_AndOutputThroughATapReachesAnotherTrack()
    {
        await using var host = new PluginTestHost();
        using var provider = new LoopbackMidiProvider(new VirtualClock(TimeSpan.FromSeconds(1)));
        var (session, lead, transpose, _, directory) = Setup(provider);
        using var _ = directory;
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(transpose.Id, Semitones), [new AutomationPoint(Tick.Zero, SemitonesValue(7), AutomationCurve.Hold), new AutomationPoint(new Tick(480), SemitonesValue(12), AutomationCurve.Hold)]);
        var harmony = Track.Create("Harmony");
        var project = session.Project with { Sequence = session.Project.Sequence.WithTrack(session.Project.Sequence.FindTrack(lead.Id)!.WithAutomation([lane])).WithTrack(harmony) };
        var synth = project.Instruments[0];
        project = project with
        {
            Connections =
            [
                SignalConnection.Create(SignalKind.Events, SignalNode.Device(transpose.Id), SignalNode.Track(harmony.Id)),
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(harmony.Id), SignalNode.ExternalPart(synth.Id, synth.Ports[0].Id)) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(2)) },
            ],
        };
        session.Execute(new ProjectCommand("Route through Harmony", _ => project));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Transpose]);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(transpose.Id)!);

        var graph = SignalGraph.Evaluate(session.Project, bridge.Extend(DeviceCatalog.BuiltIn));

        // +7 until tick 480, +12 from there; what the plugin put out travels through the tap to Harmony and on.
        Assert.Equal(["0:sysex9", "0:cc7", "0:67/240", "240:71/240", "480:72/240", "480:79/240"], Notes(graph));
        Assert.Equal(MidiChannel.FromNumber(2), graph.ExternalParts[0].ForcedChannel);
        Assert.Equal(transpose.Id, Assert.Single(graph.Parameters).Device);
    }

    [Fact]
    public async Task NotesShorterThanASampleFrame_AndTicksSharingAFrame_ComeBackExactly()
    {
        // At 30000 ticks per quarter and 120 bpm a tick is 0.8 frames at 48 kHz: one-tick notes start and end inside one
        // frame, and neighbouring ticks share frames. A plugin that leaves notes alone must give back the same notes.
        await using var host = new PluginTestHost();
        using var provider = new LoopbackMidiProvider(new VirtualClock(TimeSpan.FromSeconds(1)));
        TrackEvent[] hits = [Note(0, 36, length: 1), Note(1, 38, length: 1), Note(2, 42, length: 1), Note(3, 46, length: 3), Note(7, 49, length: 1)];
        var (session, _, transpose, _, directory) = Setup(provider, hits, new Ppqn(30_000), semitones: 0);
        using var _ = directory;
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Transpose]);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(transpose.Id)!);

        var graph = SignalGraph.Evaluate(session.Project, bridge.Extend(DeviceCatalog.BuiltIn));

        Assert.Equal(["0:36/1", "1:38/1", "2:42/1", "3:46/3", "7:49/1"], Notes(graph));
        Assert.DoesNotContain(graph.Diagnostics, d => d.Device == transpose.Id);
    }

    [Fact]
    public async Task WhenThePluginsWorkerIsKilled_ThePlanStillCompiles_WithTheNotesUnchanged_AndSaysWhy()
    {
        await using var host = new PluginTestHost();
        using var provider = new LoopbackMidiProvider(new VirtualClock(TimeSpan.FromSeconds(1)));
        var (session, _, transpose, _, directory) = Setup(provider);
        using var _ = directory;
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Transpose]);
        await bridge.SyncAsync(session.Project, Ct);
        var instance = bridge.InstanceOf(transpose.Id)!;
        host.Track(instance);
        var catalog = bridge.Extend(DeviceCatalog.BuiltIn);

        KillProcess(instance.WorkerProcessId!.Value);
        await WaitForStateAsync(instance, PluginInstanceState.Unavailable);
        var prepared = PlaybackRouting.Prepare(session.Project, catalog, ProfileCatalog.Empty, directory.GetEndpoints());

        Assert.Equal(["0:sysex9", "0:cc7", "0:60/240", "240:64/240", "480:60/240", "480:67/240"], Notes(prepared.Graph));
        var diagnostic = Assert.Single(prepared.Graph.Diagnostics, d => d.Device == transpose.Id);
        Assert.Equal(SignalDiagnosticCode.NotProcessedHere, diagnostic.Code);
        Assert.Contains("is not running", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("pass through it unchanged", diagnostic.Message, StringComparison.Ordinal);

        // A restart brings the transposition back on the next compile.
        Assert.True(await bridge.RestartAsync(transpose.Id, Ct));
        host.Track(instance);
        Assert.Equal(["0:sysex9", "0:cc7", "0:67/240", "240:71/240", "480:67/240", "480:74/240"], Notes(SignalGraph.Evaluate(session.Project, catalog)));
    }

    [Fact]
    public async Task WhenThePluginsWorkerHangs_ThePlanCompilesWithinTheRequestTimeout_AndTheHungWorkerIsStopped()
    {
        await using var host = new PluginTestHost(o => o with { RequestTimeout = TimeSpan.FromSeconds(2) });
        using var provider = new LoopbackMidiProvider(new VirtualClock(TimeSpan.FromSeconds(1)));
        var (session, _, transpose, _, directory) = Setup(provider);
        using var _ = directory;
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Transpose]);
        await bridge.SyncAsync(session.Project, Ct);
        var instance = bridge.InstanceOf(transpose.Id)!;
        host.Track(instance);
        var worker = instance.WorkerProcessId!.Value;
        Assert.True(await instance.InduceTestFaultAsync(TestFault.Hang));

        var graph = SignalGraph.Evaluate(session.Project, bridge.Extend(DeviceCatalog.BuiltIn));

        Assert.Equal(["0:sysex9", "0:cc7", "0:60/240", "240:64/240", "480:60/240", "480:67/240"], Notes(graph));
        Assert.Contains(graph.Diagnostics, d => d.Device == transpose.Id && d.Message.Contains("stopped while", StringComparison.Ordinal));
        await WaitUntilAsync(() => !IsRunning(worker), StatusTimeout, "the hung worker to be stopped");
        await WaitForStateAsync(instance, PluginInstanceState.Unavailable);
    }
}
