using System.Collections.Immutable;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Routing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Profiles;
using Bluestone.Signal;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// VST3 and CLAP plugins as project devices, through <see cref="PluginDeviceHost"/>, with the Rust test plugins: their
/// real parameters join the catalog, edits in the project reach the plugin and edits from the plugin reach the project,
/// state is saved with the project and restored on reopen and after a crash, automation is a parameter feed, and a
/// MIDI effect is rendered into the plan (ADR 0028).
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class ThirdPartyPluginDeviceTests
{
    private const int Frames = 64;
    private static readonly ParameterId GainParameter = new(0);

    public static TheoryData<string> Formats() => NativePluginTests.Formats();

    private static void Set(ProjectSession session, Project project) => session.Execute(new ProjectCommand("Set up", _ => project));

    private static async Task<PluginDeviceHost> BridgeAsync(PluginTestHost host, ProjectSession session, PluginIdentity plugin, string format)
    {
        var bridge = new PluginDeviceHost(host.Manager, session, [], maxBlockFrames: Frames);
        bridge.SetAvailable([plugin], new Dictionary<PluginIdentity, string> { [plugin] = NativePluginTests.ModulePath(format) }.ToImmutableDictionary());
        await bridge.SyncAsync(session.Project, Ct);
        return bridge;
    }

    private static float[] Run(PluginInstance instance)
    {
        var driver = new BlockDriver(instance, Frames);
        Array.Fill(driver.Input, 1.0f);
        for (var i = 0; i < 4 && driver.Process().Outcome != ProcessOutcome.Processed; i++)
        {
        }

        driver.Process();
        return driver.Output;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task APluginDevice_HasItsRealParameters_IsSavedReopenedAndRestarted_AndItsAutomationIsAFeed(string format)
    {
        await using var host = new PluginTestHost();
        var gain = (await NativePluginTests.ScanAsync(host, format))["Test Gain"];
        var session = new ProjectSession();
        var track = Track.Create("Vocals", TrackRole.Audio);
        var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(gain).ToReference());
        Set(session, (session.Project with { Sequence = session.Project.Sequence.WithTrack(track) }).WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [device] }));
        Assert.Equal($"plugin:{format}:BluestoneTestPlugins/{gain.PluginId}", device.Definition.Id.Value);

        byte[] saved;
        await using (var bridge = await BridgeAsync(host, session, gain, format))
        {
            var instance = bridge.InstanceOf(device.Id)!;
            host.Track(instance);
            Assert.Equal("Running", bridge.StatusOf(device)!.Text);
            Assert.True(bridge.StatusOf(device)!.CanOpenEditor);
            var definition = bridge.Extend(DeviceCatalog.BuiltIn).Find(device.Definition.Id)!;
            Assert.Equal(["Gain", "Invert"], definition.Parameters.Select(p => p.Name));

            // An edit in the project reaches the running plugin; an edit the plugin makes reaches the project.
            session.Execute(DeviceCommands.SetParameter(device.Id, GainParameter, ControlValue.FromFraction(0.25)));
            await bridge.SyncAsync(session.Project, Ct);
            Assert.All(Run(instance), s => Assert.Equal(0.5f, s, 4));
            // The plugin sets Invert itself (as from its editor); the edit is then recorded and not sent back.
            await instance.SetParameterAsync(1, 1.0, Ct);
            bridge.RecordEdit(new PluginParameterEdit(device.Id, 1, 1.0));
            Assert.Equal(1.0, session.Project.FindDevice(device.Id)!.Value.Device.ValueOf(new ParameterId(1))!.Value.ToFraction(), 6);
            await bridge.SyncAsync(session.Project, Ct);
            Assert.All(Run(instance), s => Assert.Equal(-0.5f, s, 4));

            Assert.True(await bridge.CaptureStateAsync(device.Id, Ct));
            Assert.Equal($"bluestone.{format}-state", session.Project.FindDevice(device.Id)!.Value.Device.State!.Format);
            saved = ProjectSerializer.Default.Serialize(new ProjectDocument(session.Project));
        }

        var reopened = new ProjectSession();
        Set(reopened, ProjectSerializer.Default.Deserialize(saved).Project);
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(device.Id, GainParameter), [new AutomationPoint(Tick.Zero, ControlValue.Max, AutomationCurve.Hold)]);
        Set(reopened, reopened.Project with { Sequence = reopened.Project.Sequence.WithTrack(reopened.Project.Sequence.FindTrack(track.Id)!.WithAutomation([lane])) });
        await using var again = await BridgeAsync(host, reopened, gain, format);
        var restored = again.InstanceOf(device.Id)!;
        host.Track(restored);
        Assert.All(Run(restored), s => Assert.Equal(-0.5f, s, 4));

        KillProcess(restored.WorkerProcessId!.Value);
        await WaitForStateAsync(restored, PluginInstanceState.Unavailable);
        Assert.Equal("Crashed", again.StatusOf(reopened.Project.FindDevice(device.Id)!.Value.Device)!.Text);
        Assert.True(await again.RestartAsync(device.Id, Ct));
        host.Track(restored);
        Assert.All(Run(restored), s => Assert.Equal(-0.5f, s, 4));

        var catalog = again.Extend(DeviceCatalog.BuiltIn);
        Assert.DoesNotContain(SignalRoutingValidator.Validate(reopened.Project, catalog.Lookup), i => i.Code is RoutingIssueCode.AutomationDeviceMissing or RoutingIssueCode.AutomationParameterUnknown);
        Assert.Equal(device.Id, Assert.Single(SignalGraph.Evaluate(reopened.Project, catalog).Parameters).Device);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task APluginMidiEffect_IsRenderedIntoThePlan(string format)
    {
        await using var host = new PluginTestHost();
        var transposePlugin = (await NativePluginTests.ScanAsync(host, format))["Test Transpose"];
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        var port = provider.CreatePort("Synth", "synth");
        using var directory = new EndpointDirectory([provider]);
        var one = MidiChannel.FromNumber(1);
        var lead = Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(240), one, new NoteNumber(60), new Velocity(100)), new NoteEvent(new Tick(480), new TickSpan(240), one, new NoteNumber(64), new Velocity(100))]);
        var transpose = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(transposePlugin).ToReference()).WithParameter(new ParameterId(0), ControlValue.FromFraction((5 + 24) / 48.0));
        var synth = ExternalInstrument.Create("Synth", new EndpointReference(LoopbackMidiProvider.ProviderId, port.OutputId.Value, "Synth"));
        var session = new ProjectSession();
        var project = session.Project with
        {
            Sequence = session.Project.Sequence.WithTrack(lead),
            Instruments = [synth],
            Connections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(lead.Id), SignalNode.ExternalPart(synth.Id, synth.Ports[0].Id))],
        };
        Set(session, project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(lead.Id)) with { Devices = [transpose] }));
        await using var bridge = await BridgeAsync(host, session, transposePlugin, format);
        host.Track(bridge.InstanceOf(transpose.Id)!);

        var prepared = PlaybackRouting.Prepare(session.Project, bridge.Extend(DeviceCatalog.BuiltIn), ProfileCatalog.Empty, directory.GetEndpoints());

        var notes = Assert.Single(prepared.Graph.ExternalParts).Events.Select(e => e.Event).OfType<NoteEvent>().Select(n => (n.Position.Value, n.Note.Value, n.Duration.Value));
        Assert.Equal([(0L, 65, 240L), (480L, 69, 240L)], notes);
        Assert.DoesNotContain(prepared.Graph.Diagnostics, d => d.Device == transpose.Id);
    }
}
