using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

public sealed class DeviceAndRoutingCommandsTests
{
    private static readonly DeviceDefinitionLookup Definitions = DeviceCatalog.BuiltIn.With(TestDevices.Synth).With(TestDevices.Filter).With(TestDevices.Delay).With(TestDevices.MidiFx).Lookup;

    private static (EditHistory History, Track A, Track B) TwoTracks()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(a).WithTrack(b) });
        return (history, a, b);
    }

    [Fact]
    public void Scenario14_MovingADevice_KeepsItsIdentityParametersAndAutomation()
    {
        var (history, a, _) = TwoTracks();
        var fx = TestDevices.Instance(TestDevices.MidiFx).WithParameter(new ParameterId(1), ControlValue.FromFraction(0.25));
        var synth = TestDevices.Instance(TestDevices.Synth);
        var filter = TestDevices.Instance(TestDevices.Filter);
        history.Execute(DeviceCommands.InsertDevice(a.Id, fx, Definitions));
        history.Execute(DeviceCommands.InsertDevice(a.Id, synth, Definitions));
        history.Execute(DeviceCommands.InsertDevice(a.Id, filter, Definitions));
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(filter.Id, new ParameterId(1)), [new AutomationPoint(Tick.Zero, ControlValue.Max)]);
        history.Execute(AutomationCommands.AddLane(a.Id, lane));

        history.Execute(DeviceCommands.MoveDevice(fx.Id, 2, Definitions));

        var chain = history.Current.ChainOf(a.Id)!;
        Assert.Equal([synth.Id, filter.Id, fx.Id], chain.Devices.Select(d => d.Id));
        Assert.Equal(ControlValue.FromFraction(0.25), chain.Find(fx.Id)!.ValueOf(new ParameterId(1)));
        Assert.Equal(filter.Id, history.Current.Sequence.FindTrack(a.Id)!.Automation.Single().Target.Device);
        Assert.Equal("Move Device", history.UndoLabel);
    }

    [Fact]
    public void Scenario14_AMoveThatWouldBreakATap_IsRefused_AndChangesNothing()
    {
        // An audio tap after the MIDI effect (behind the synth) sends audio to track B. Moving the effect in
        // front of the synth would leave the tap with no audio to send, so the move is refused.
        var (history, a, b) = TwoTracks();
        var fx = TestDevices.Instance(TestDevices.MidiFx);
        var synth = TestDevices.Instance(TestDevices.Synth);
        history.Execute(DeviceCommands.InsertDevice(a.Id, synth, Definitions));
        history.Execute(DeviceCommands.InsertDevice(a.Id, fx, Definitions));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Audio, SignalNode.Device(fx.Id), SignalNode.Track(b.Id)), Definitions));
        var before = history.Current;

        var refused = Assert.Throws<CommandRefusedException>(() => history.Execute(DeviceCommands.MoveDevice(fx.Id, 0, Definitions)));

        Assert.Contains("has no audio to send", refused.Message, StringComparison.Ordinal);
        Assert.Equal(RoutingIssueCode.NothingToCarry, Assert.Single(refused.Issues).Code);
        Assert.Same(before, history.Current);
    }

    [Fact]
    public void Scenario10_AConnectionThatWouldFeedBack_IsRefused()
    {
        var (history, a, b) = TwoTracks();
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id)), Definitions));

        var refused = Assert.Throws<CommandRefusedException>(() =>
            history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(b.Id), SignalNode.Track(a.Id)), Definitions)));

        Assert.Contains("feed back", refused.Message, StringComparison.Ordinal);
        Assert.Single(history.Current.Connections);
    }

    [Fact]
    public void Scenario7_LoadingOnePresetOntoTwoTracks_CreatesIndependentDevices()
    {
        var (history, a, b) = TwoTracks();
        history.Execute(DeviceCommands.InsertDevice(a.Id, BuiltInDevices.CreateArpeggiator(rate: 1), Definitions));
        history.Execute(DeviceCommands.InsertDevice(a.Id, TestDevices.Instance(TestDevices.Synth) with { State = new PluginState(ByteBlock.Copy([1, 2, 3]), "test-v1") }, Definitions));
        var preset = ChainPresetSerializer.Deserialize(ChainPresetSerializer.Serialize(DeviceCommands.SavePreset(history.Current, history.Current.ChainOf(a.Id)!.Id, "Arp synth")));
        var c = Track.Create("c");
        history.Execute(ProjectCommands.AddTrack(c));

        history.Execute(DeviceCommands.LoadPreset(b.Id, preset, Definitions));
        history.Execute(DeviceCommands.LoadPreset(c.Id, preset, Definitions));

        var original = history.Current.ChainOf(a.Id)!.Devices;
        var onB = history.Current.ChainOf(b.Id)!.Devices;
        var onC = history.Current.ChainOf(c.Id)!.Devices;
        Assert.Empty(original.Select(d => d.Id).Intersect(onB.Select(d => d.Id)));
        Assert.Empty(original.Concat(onB).Select(d => d.Id).Intersect(onC.Select(d => d.Id)));
        Assert.Equal(6, original.Concat(onB).Concat(onC).Select(d => d.Id).Distinct().Count());
        Assert.Equal(original.Select(d => (d.Definition, d.Parameters.Length)), onB.Select(d => (d.Definition, d.Parameters.Length)));
        Assert.Equal([1, 2, 3], onC[1].State!.Data.ToArray());

        // Changing one copy changes nothing in the others.
        history.Execute(DeviceCommands.SetParameter(onB[0].Id, BuiltInDevices.ArpeggiatorRate, ControlValue.Max));
        Assert.NotEqual(ControlValue.Max, history.Current.ChainOf(c.Id)!.Devices[0].ValueOf(BuiltInDevices.ArpeggiatorRate));
        Assert.NotEqual(ControlValue.Max, history.Current.ChainOf(a.Id)!.Devices[0].ValueOf(BuiltInDevices.ArpeggiatorRate));
    }

    [Fact]
    public void MovingAChain_KeepsItsIdentity_UnlikeLoadingAPreset()
    {
        var (history, a, b) = TwoTracks();
        var arp = BuiltInDevices.CreateArpeggiator();
        history.Execute(DeviceCommands.InsertDevice(a.Id, arp, Definitions));
        var chain = history.Current.ChainOf(a.Id)!;

        history.Execute(DeviceCommands.MoveChain(chain.Id, ChainOwner.ForTrack(b.Id), Definitions));

        Assert.Null(history.Current.ChainOf(a.Id));
        Assert.Equal((chain.Id, arp.Id), (history.Current.ChainOf(b.Id)!.Id, history.Current.ChainOf(b.Id)!.Devices[0].Id));
        var rack = DeviceChain.Create(ChainOwner.Rack, "FX");
        history.Execute(DeviceCommands.AddRack(rack));
        Assert.Throws<CommandRefusedException>(() => history.Execute(DeviceCommands.MoveChain(rack.Id, ChainOwner.ForTrack(b.Id), Definitions)));
    }

    [Fact]
    public void RemovingADevice_RemovesItsTaps_ButKeepsAutomationThatTargetsIt()
    {
        var (history, a, b) = TwoTracks();
        var arp = BuiltInDevices.CreateArpeggiator();
        history.Execute(DeviceCommands.InsertDevice(a.Id, arp, Definitions));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Device(arp.Id), SignalNode.Track(b.Id)), Definitions));
        history.Execute(AutomationCommands.AddLane(a.Id, new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(arp.Id, BuiltInDevices.ArpeggiatorRate), [new AutomationPoint(Tick.Zero, ControlValue.Max)])));

        history.Execute(DeviceCommands.RemoveDevice(arp.Id, Definitions));

        Assert.Empty(history.Current.ChainOf(a.Id)!.Devices);
        Assert.Empty(history.Current.Connections);
        Assert.Single(history.Current.Sequence.FindTrack(a.Id)!.Automation);
    }

    [Fact]
    public void BypassAndParameterEdits_AreUndoable()
    {
        var (history, a, _) = TwoTracks();
        var transpose = BuiltInDevices.CreateTranspose(3);
        history.Execute(DeviceCommands.InsertDevice(a.Id, transpose, Definitions));

        history.Execute(DeviceCommands.SetBypassed(transpose.Id, true, Definitions));
        history.Execute(DeviceCommands.SetParameter(transpose.Id, BuiltInDevices.TransposeSemitones, BuiltInDevices.Transpose.Parameters[0].ToStored(9)));

        var device = history.Current.FindDevice(transpose.Id)!.Value.Device;
        Assert.Equal((true, 9), (device.IsBypassed, BuiltInDevices.TransposeOf(device)));
        history.Undo();
        history.Undo();
        Assert.Equal((false, 3), (history.Current.FindDevice(transpose.Id)!.Value.Device.IsBypassed, BuiltInDevices.TransposeOf(history.Current.FindDevice(transpose.Id)!.Value.Device)));
    }

    [Fact]
    public void ARackSharedByTwoTracks_CanBeAddedConnectedAndRemoved()
    {
        var (history, a, b) = TwoTracks();
        var rack = DeviceChain.Create(ChainOwner.Rack, "XG") with { Devices = [TestDevices.Instance(TestDevices.Synth)] };
        var bus = MixerChannel.Create("XG out");
        history.Execute(DeviceCommands.AddRack(rack));
        history.Execute(RoutingCommands.AddMixerChannel(bus));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Rack(rack.Id)), Definitions));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(b.Id), SignalNode.Rack(rack.Id)), Definitions));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Audio, SignalNode.Rack(rack.Id), SignalNode.Mixer(bus.Id)), Definitions));
        Assert.Equal(3, history.Current.Connections.Length);

        history.Execute(DeviceCommands.RemoveRack(rack.Id));

        Assert.Empty(history.Current.Connections);
        Assert.Single(history.Current.Mixer.Channels);
    }

    [Fact]
    public void AnInstrumentInUse_CannotBeRemoved()
    {
        var (history, a, _) = TwoTracks();
        history.Execute(ProjectCommands.SetTrackOutput(a.Id, new TrackOutput { Endpoint = new EndpointReference("coremidi", "1", "MU2000") }));
        var instrument = history.Current.Instruments.Single();

        var refused = Assert.Throws<CommandRefusedException>(() => history.Execute(RoutingCommands.RemoveInstrument(instrument.Id)));

        Assert.Contains("1 connection sends to MU2000", refused.Message, StringComparison.Ordinal);
        history.Execute(ProjectCommands.SetTrackOutput(a.Id, TrackOutput.None));
        history.Execute(RoutingCommands.RemoveInstrument(instrument.Id));
        Assert.Empty(history.Current.Instruments);
    }

    [Fact]
    public void Scenario9_AddingAudioToAnInstrumentTrack_MakesItHybrid_AfterConsent_WithoutChangingAnythingElse()
    {
        var note = new NoteEvent(Tick.Zero, new TickSpan(10), MidiChannel.FromNumber(1), NoteNumber.MiddleC, Velocity.Max);
        var keys = Track.FromEvents(TrackId.New(), "Keys", [note]);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(keys) });
        history.Execute(ProjectCommands.SetTrackOutput(keys.Id, new TrackOutput { Endpoint = new EndpointReference("coremidi", "1", "MU2000"), Channel = MidiChannel.FromNumber(4), Transpose = 2 }));
        var before = history.Current;
        var clip = new AudioClip(ClipId.New(), new Tick(1920), new TickSpan(960), TickSpan.Zero, new AudioSource("vox.wav", 48_000, 48_000, 1));

        var asked = Assert.Throws<CommandRefusedException>(() => history.Execute(TrackRoleCommands.AddAudioClip(keys.Id, clip, Definitions)));
        Assert.Contains("no audio output", asked.Message, StringComparison.Ordinal);
        Assert.Same(before, history.Current);

        history.Execute(TrackRoleCommands.AddAudioClip(keys.Id, clip, Definitions, confirmed: true));

        var after = history.Current;
        var track = after.Sequence.FindTrack(keys.Id)!;
        Assert.Equal(TrackRole.Hybrid, track.Role);
        Assert.Equal(2, track.Clips.Length);
        Assert.Equal(note.Id, track.ArrangedEvents.Single().Id);
        Assert.Equal(TrackOutputs.Read(before, keys.Id), TrackOutputs.Read(after, keys.Id));
        Assert.Equal(before.ChainOf(keys.Id), after.ChainOf(keys.Id));
        Assert.Single(after.Mixer.Channels);
        Assert.Contains(after.Connections, c => c.Kind == SignalKind.Audio && c.Source == SignalNode.Track(keys.Id));
    }

    [Fact]
    public void ChangingARoleThatWouldHideClips_IsRefused()
    {
        var keys = Track.FromEvents(TrackId.New(), "Keys", [new NoteEvent(Tick.Zero, new TickSpan(10), MidiChannel.FromNumber(1), NoteNumber.MiddleC, Velocity.Max)]);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(keys) });

        Assert.Throws<CommandRefusedException>(() => history.Execute(TrackRoleCommands.SetRole(keys.Id, TrackRole.Audio, Definitions)));
        history.Execute(TrackRoleCommands.SetRole(keys.Id, TrackRole.Hybrid, Definitions));

        Assert.Equal(TrackRole.Hybrid, history.Current.Sequence.FindTrack(keys.Id)!.Role);
    }

    [Fact]
    public void RecordingNotesInsideAnAudioClip_IsRefused_AndOutsideItWorks()
    {
        var vocals = new Track(TrackId.New(), "Vocals", [new AudioClip(ClipId.New(), new Tick(960), new TickSpan(960), TickSpan.Zero, new AudioSource("v.wav", 48_000, 48_000, 1))], role: TrackRole.Hybrid);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(vocals) });
        NoteEvent At(long tick) => new(new Tick(tick), new TickSpan(10), MidiChannel.FromNumber(1), NoteNumber.MiddleC, Velocity.Max);

        var refused = Assert.Throws<CommandRefusedException>(() => history.Execute(ProjectCommands.Record(vocals.Id, [At(1000)], null)));
        history.Execute(ProjectCommands.Record(vocals.Id, [At(0)], null));

        Assert.Contains("inside an audio clip", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, history.Current.Sequence.FindTrack(vocals.Id)!.Clips.Length);
    }

    [Fact]
    public void RoleAndOutputEdits_ThatWouldBreakRouting_AreRefused()
    {
        var (history, a, b) = TwoTracks();
        history.Execute(ProjectCommands.SetTrackOutput(a.Id, new TrackOutput { Endpoint = new EndpointReference("coremidi", "1", "MU2000") }));
        history.Execute(TrackRoleCommands.SetRole(b.Id, TrackRole.Audio, Definitions));

        // An audio track has no events, so its events connection to hardware would carry nothing.
        Assert.Throws<CommandRefusedException>(() => history.Execute(TrackRoleCommands.SetRole(a.Id, TrackRole.Audio, Definitions)));
        Assert.Throws<CommandRefusedException>(() => history.Execute(ProjectCommands.SetTrackOutput(b.Id, new TrackOutput { Endpoint = new EndpointReference("coremidi", "1", "MU2000") })));

        Assert.Equal(TrackRole.Instrument, history.Current.Sequence.FindTrack(a.Id)!.Role);
        Assert.Null(TrackOutputs.PrimaryConnection(history.Current, b.Id));
    }

    [Fact]
    public void Tracks_CanOnlyJoinAGroupTrack()
    {
        var (history, a, b) = TwoTracks();
        var band = Track.Create("Band", TrackRole.Group);
        history.Execute(ProjectCommands.AddTrack(band));

        Assert.Throws<CommandRefusedException>(() => history.Execute(GroupCommands.SetGroup(a.Id, b.Id, Definitions)));
        history.Execute(GroupCommands.SetGroup(a.Id, band.Id, Definitions));

        Assert.Equal(band.Id, history.Current.Sequence.FindTrack(a.Id)!.Group);
    }
}
