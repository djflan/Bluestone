using Bluestone.Application.Editing;
using Bluestone.Application.Routing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

/// <summary>
/// Acceptance scenarios 1, 5, and 8 (prompt section 19) end to end: project model, routing validation, the
/// signal graph, and, for hardware, the plan and the bytes the port receives. The other scenarios are listed
/// with their tests in docs/architecture.md.
/// </summary>
public sealed class AcceptanceScenarioTests
{
    private static readonly DeviceCatalog Catalog = DeviceCatalog.BuiltIn.With(TestDevices.Synth).With(TestDevices.Filter).With(TestDevices.Delay).With(TestDevices.MidiFx);

    private static NoteEvent Note(long at, int note, int channel) =>
        new(new Tick(at), new TickSpan(120), MidiChannel.FromNumber(channel), new NoteNumber(note), new Velocity(100));

    [Fact]
    public void Scenario1_FourTracksShareOneXgSynth_WithOneStereoOutputToTheMixer()
    {
        var tracks = new[] { ("Piano", 1), ("Strings", 2), ("Bass", 3), ("Drums", 10) }
            .Select(t => Track.FromEvents(TrackId.New(), t.Item1, [Note(0, 60, t.Item2), Note(480, 62, t.Item2)]))
            .ToList();
        var xg = TestDevices.Instance(TestDevices.Synth) with { Name = "XG Softsynth" };
        var rack = DeviceChain.Create(ChainOwner.Rack, "XG") with { Devices = [xg, TestDevices.Instance(TestDevices.Delay)] };
        var channel = MixerChannel.Create("XG");
        var history = new EditHistory(Project.CreateNew() with { Sequence = tracks.Aggregate(Sequence.CreateEmpty(Ppqn.Default), (s, t) => s.WithTrack(t)) });
        history.Execute(DeviceCommands.AddRack(rack));
        history.Execute(RoutingCommands.AddMixerChannel(channel));
        foreach (var track in tracks)
        {
            history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.Rack(rack.Id)), Catalog.Lookup));
        }

        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Audio, SignalNode.Rack(rack.Id), SignalNode.Mixer(channel.Id)), Catalog.Lookup));
        var project = history.Current;

        var result = SignalGraph.Evaluate(project, Catalog);

        // One synthesizer instance receives every part, merged in canonical order, each on its own channel.
        var feed = Assert.Single(result.SoftwareInstruments);
        Assert.Equal(xg.Id, feed.Device);
        Assert.Equal([1, 2, 3, 10, 1, 2, 3, 10], feed.Events.Select(e => ((NoteEvent)e.Event).Channel.Number));
        Assert.Single(project.Chains, c => c.Devices.Any(d => d.Definition.Id == TestDevices.Synth.Id));
        // One audio path, into one mixer channel: four tracks do not need four channels.
        Assert.Single(project.Mixer.Channels);
        Assert.Single(project.Connections, c => c.Kind == SignalKind.Audio);
        Assert.Empty(SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(project, Catalog.Lookup)));
        Assert.Contains(result.Diagnostics, d => d.Code == SignalDiagnosticCode.NotAudible);
    }

    [Fact]
    public void Scenario5_AutomationTargetsEachDeviceDirectly_AndSurvivesReordering()
    {
        var track = Track.FromEvents(TrackId.New(), "Lead", [Note(0, 60, 1)]);
        var fx = BuiltInDevices.CreateTranspose(0);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var filter = TestDevices.Instance(TestDevices.Filter);
        var delay = TestDevices.Instance(TestDevices.Delay);
        static AutomationLane Lane(DeviceId device, ParameterId parameter, ControlValue value) =>
            new(AutomationLaneId.New(), AutomationTarget.ForDevice(device, parameter), [new AutomationPoint(Tick.Zero, value, AutomationCurve.Hold)]);
        var twelve = BuiltInDevices.Transpose.Parameters[0].ToStored(12);
        track = track.WithAutomation([
            Lane(fx.Id, BuiltInDevices.TransposeSemitones, twelve),
            Lane(synth.Id, new ParameterId(1), ControlValue.Max),
            Lane(filter.Id, new ParameterId(1), ControlValue.Center),
        ]);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });
        foreach (var device in new[] { fx, synth, filter, delay })
        {
            history.Execute(DeviceCommands.InsertDevice(track.Id, device, Catalog.Lookup));
        }

        var before = SignalGraph.Evaluate(history.Current, Catalog);
        history.Execute(DeviceCommands.MoveDevice(filter.Id, 3, Catalog.Lookup));
        var after = SignalGraph.Evaluate(history.Current, Catalog);

        foreach (var result in new[] { before, after })
        {
            // The MIDI effect's automation reached it (notes are transposed), not the events stream.
            Assert.Equal(72, ((NoteEvent)Assert.Single(Assert.Single(result.SoftwareInstruments).Events).Event).Note.Value);
            // The synth and the filter get their own parameter changes, delivered to them by ID.
            Assert.Equal(new HashSet<DeviceId> { synth.Id, filter.Id }, result.Parameters.Select(p => p.Device).ToHashSet());
            Assert.Equal(ControlValue.Center, Assert.Single(result.Parameters.Single(p => p.Device == filter.Id).Changes).Value);
        }

        Assert.Equal([fx.Id, synth.Id, delay.Id, filter.Id], history.Current.ChainOf(track.Id)!.Devices.Select(d => d.Id));
        Assert.Equal([fx.Id, synth.Id, filter.Id], history.Current.Sequence.FindTrack(track.Id)!.Automation.Select(l => l.Target.Device));
    }

    [Fact]
    public async Task Scenario8_ATrackPlaysThroughAnExternalYamahaInstrument_OnItsPortAndChannel()
    {
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        var portA = provider.CreatePort("MU2000 A", "mu-a");
        var portB = provider.CreatePort("MU2000 B", "mu-b");
        using var directory = new EndpointDirectory([provider]);
        var mu2000 = new ExternalInstrument
        {
            Id = ExternalInstrumentId.New(),
            Name = "Yamaha MU2000",
            Profile = new ProfileReference("bluestone.generic.xg", "Generic XG"),
            OperatingMode = "XG",
            Ports =
            [
                new ExternalPort("A", "Port A", new EndpointReference(LoopbackMidiProvider.ProviderId, portA.OutputId.Value, "MU2000 A")),
                new ExternalPort("B", "Port B", new EndpointReference(LoopbackMidiProvider.ProviderId, portB.OutputId.Value, "MU2000 B")),
            ],
        };
        var strings = Track.FromEvents(TrackId.New(), "Strings", [Note(0, 64, 1)]);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(new Ppqn(500)).WithTrack(strings) });
        history.Execute(RoutingCommands.AddInstrument(mu2000));
        history.Execute(RoutingCommands.Connect(
            SignalConnection.Create(SignalKind.Events, SignalNode.Track(strings.Id), SignalNode.ExternalPart(mu2000.Id, "B")) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(4)) },
            Catalog.Lookup));
        var project = history.Current;

        var prepared = PlaybackRouting.Prepare(project, Catalog, ProfileCatalog.Empty, directory.GetEndpoints());
        using var outputs = await PlaybackRouting.OpenAsync(prepared, directory, TestContext.Current.CancellationToken);
        using var engine = new PlaybackEngine(clock, project.Sequence.TempoMap);
        engine.SetOutputs(outputs.Outputs);
        engine.Load(prepared.Compile(project.Sequence));
        engine.Play(Tick.Zero);
        engine.Pump();
        clock.Advance(TimeSpan.FromSeconds(1));
        engine.Pump();

        Assert.Equal([portB.OutputId], prepared.Slots);
        Assert.Empty(portA.Sent);
        Assert.Equal(["934064", "834040"], portB.Sent.Select(m => Convert.ToHexString(m.Bytes)));
    }
}
