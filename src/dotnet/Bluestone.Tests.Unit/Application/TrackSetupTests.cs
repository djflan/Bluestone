using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

/// <summary>
/// DAW-first track defaults: a new track is hybrid and heard at the master, and inserting something that makes sound into a
/// track whose audio goes nowhere gives it a path, as explicit, undoable connections that respect deliberate routing.
/// </summary>
public sealed class TrackSetupTests
{
    private static readonly DeviceDefinitionLookup Definitions = DeviceCatalog.BuiltIn.With(TestDevices.Synth).With(TestDevices.Filter).With(TestDevices.MidiFx).Lookup;

    private static EditHistory Empty() => new(Project.CreateNew());

    private static SignalConnection[] AudioFrom(Project project, TrackId track) =>
        [.. project.Connections.Where(c => c.Kind == SignalKind.Audio && c.Source == SignalNode.Track(track))];

    [Fact]
    public void ANewTrack_IsHybrid_AndHeardAtTheMaster_InOneUndoStep()
    {
        var history = Empty();
        var track = Track.Create("Track 1", TrackRole.Hybrid);

        history.Execute(TrackSetup.NewTrack(track, Definitions));

        Assert.Equal(TrackRole.Hybrid, history.Current.Sequence.FindTrack(track.Id)!.Role);
        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, track.Id)).Destination);
        Assert.Equal("New Track", history.UndoLabel);
        history.Undo();
        Assert.Empty(history.Current.Sequence.Tracks);
        Assert.Empty(history.Current.Connections);
    }

    [Fact]
    public void InsertingAnInstrument_IntoATrackWhoseAudioGoesNowhere_GivesItAPathToTheMaster_UndoneTogether()
    {
        var track = Track.Create("Imported");
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });
        var synth = TestDevices.Instance(TestDevices.Synth);

        history.Execute(DeviceCommands.InsertDevice(track.Id, synth, Definitions));

        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, track.Id)).Destination);
        Assert.Equal(TrackRole.Hybrid, history.Current.Sequence.FindTrack(track.Id)!.Role);
        history.Undo();
        Assert.Null(history.Current.ChainOf(track.Id));
        Assert.Empty(history.Current.Connections);
        Assert.Equal(TrackRole.Instrument, history.Current.Sequence.FindTrack(track.Id)!.Role);
    }

    [Fact]
    public void TheInstrumentCanBeRemovedAgain_AndTheTrackStaysHeard()
    {
        var track = Track.Create("Lead");
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });
        var synth = TestDevices.Instance(TestDevices.Synth);
        history.Execute(DeviceCommands.InsertDevice(track.Id, synth, Definitions));

        history.Execute(DeviceCommands.RemoveDevice(synth.Id, Definitions));

        Assert.Empty(history.Current.ChainOf(track.Id)!.Devices);
        Assert.Single(AudioFrom(history.Current, track.Id));
    }

    [Fact]
    public void InsertingANoteEffect_AddsNoAudioPath()
    {
        var track = Track.Create("Lead");
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });

        history.Execute(DeviceCommands.InsertDevice(track.Id, TestDevices.Instance(TestDevices.MidiFx), Definitions));

        Assert.Empty(history.Current.Connections);
        Assert.Equal(TrackRole.Instrument, history.Current.Sequence.FindTrack(track.Id)!.Role);
    }

    [Fact]
    public void DeliberateAudioRouting_IsLeftAlone()
    {
        // The track already sends its audio to a mixer channel, so inserting an instrument changes no routing.
        var track = Track.Create("Pad", TrackRole.Hybrid);
        var channel = MixerChannel.Create("Pads");
        var toChannel = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Mixer(channel.Id));
        var history = new EditHistory(Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track),
            Mixer = Project.CreateNew().Mixer.With(channel),
            Connections = [toChannel],
        });

        history.Execute(DeviceCommands.InsertDevice(track.Id, TestDevices.Instance(TestDevices.Synth), Definitions));

        Assert.Equal([toChannel], history.Current.Connections);
    }

    [Fact]
    public void AnAudioTapFromTheChain_CountsAsAPath()
    {
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });
        history.Execute(DeviceCommands.InsertDevice(track.Id, synth, Definitions));
        history.Execute(TrackSetup.SetAudioDestination([track.Id], null, Definitions));
        var tap = SignalConnection.Create(SignalKind.Audio, SignalNode.Device(synth.Id), SignalNode.Master);
        history.Execute(RoutingCommands.Connect(tap, Definitions));

        history.Execute(DeviceCommands.InsertDevice(track.Id, TestDevices.Instance(TestDevices.Filter), Definitions));

        Assert.Equal([tap], history.Current.Connections);
    }

    [Fact]
    public void AGroupedTrack_IsHeardThroughItsGroup()
    {
        var group = new Track(TrackId.New(), "Group 1", [], role: TrackRole.Group);
        var member = new Track(TrackId.New(), "Bass", [], role: TrackRole.Hybrid, group: group.Id);
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(group).WithTrack(member) };

        var routed = TrackSetup.WithAudioPath(project, member.Id);

        Assert.Equal(SignalNode.Track(group.Id), Assert.Single(AudioFrom(routed, member.Id)).Destination);
    }

    [Fact]
    public void SettingTheAudioDestination_RepointsAddsOrRemovesTheOutputsFirstAudioConnection()
    {
        var history = Empty();
        var a = Track.Create("A", TrackRole.Hybrid);
        var b = Track.Create("B", TrackRole.Hybrid);
        var channel = MixerChannel.Create("Bus");
        history.Execute(TrackSetup.NewTrack(a, Definitions));
        history.Execute(TrackSetup.NewTrack(b, Definitions));
        history.Execute(RoutingCommands.AddMixerChannel(channel));
        history.Execute(TrackSetup.SetAudioDestination([b.Id], null, Definitions));
        Assert.Empty(AudioFrom(history.Current, b.Id));

        history.Execute(TrackSetup.SetAudioDestination([a.Id, b.Id], SignalNode.Mixer(channel.Id), Definitions));

        Assert.Equal("Change Audio Output (2 Tracks)", history.UndoLabel);
        Assert.All([a.Id, b.Id], t => Assert.Equal(SignalNode.Mixer(channel.Id), Assert.Single(AudioFrom(history.Current, t)).Destination));
        history.Undo();
        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, a.Id)).Destination);
        Assert.Empty(AudioFrom(history.Current, b.Id));
    }

    [Fact]
    public void OnlyThePrimaryAudioOutput_IsRepointed_AndASendToTheChosenDestination_TakesItsPlace()
    {
        var history = Empty();
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var bus = MixerChannel.Create("Bus");
        var verb = MixerChannel.Create("Verb");
        history.Execute(TrackSetup.NewTrack(track, Definitions));
        history.Execute(RoutingCommands.AddMixerChannel(bus));
        history.Execute(RoutingCommands.AddMixerChannel(verb));
        var send = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Mixer(verb.Id));
        history.Execute(RoutingCommands.Connect(send, Definitions));

        history.Execute(TrackSetup.SetAudioDestination([track.Id], SignalNode.Mixer(bus.Id), Definitions));

        Assert.Equal([SignalNode.Mixer(bus.Id), SignalNode.Mixer(verb.Id)], AudioFrom(history.Current, track.Id).Select(c => c.Destination));
        Assert.Equal(SignalNode.Mixer(bus.Id), TrackSetup.PrimaryAudioOutput(history.Current, track.Id)!.Destination);

        // Choosing the send's destination makes the send primary instead of sending the same audio there twice.
        history.Execute(TrackSetup.SetAudioDestination([track.Id], SignalNode.Mixer(verb.Id), Definitions));

        Assert.Equal([send], AudioFrom(history.Current, track.Id));
    }

    [Fact]
    public void AnAudioDestinationThatWouldFeedBack_IsRefused_AndChangesNothing()
    {
        var history = Empty();
        var a = Track.Create("A", TrackRole.Hybrid);
        var b = Track.Create("B", TrackRole.Hybrid);
        history.Execute(TrackSetup.NewTrack(a, Definitions));
        history.Execute(TrackSetup.NewTrack(b, Definitions));
        history.Execute(TrackSetup.SetAudioDestination([b.Id], SignalNode.Track(a.Id), Definitions));
        var before = history.Current;

        Assert.Throws<CommandRefusedException>(() => history.Execute(TrackSetup.SetAudioDestination([a.Id], SignalNode.Track(b.Id), Definitions)));

        Assert.Same(before, history.Current);
    }

    [Fact]
    public void RemovingTheDeviceWhoseTapWasTheOnlyPath_LeavesTheTrackUnrouted_RatherThanRoutingItAgainBehindTheMusiciansBack()
    {
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });
        history.Execute(DeviceCommands.InsertDevice(track.Id, synth, Definitions));
        history.Execute(TrackSetup.SetAudioDestination([track.Id], null, Definitions));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Audio, SignalNode.Device(synth.Id), SignalNode.Master), Definitions));

        history.Execute(DeviceCommands.RemoveDevice(synth.Id, Definitions));

        Assert.Empty(history.Current.Connections);
        Assert.False(TrackSetup.HasAudioPath(history.Current, track.Id));
    }

    [Fact]
    public void APresetOrAMovedChainWithAnInstrument_IsHeardLikeAnInsertedInstrument()
    {
        var imported = Track.Create("Imported");
        var other = Track.Create("Other");
        var rack = DeviceChain.Create(ChainOwner.Rack, "Synths") with { Devices = [TestDevices.Instance(TestDevices.Synth)] };
        var history = new EditHistory((Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(imported).WithTrack(other) }).WithChain(rack));
        var preset = DeviceChainPreset.From(DeviceChain.Create(ChainOwner.Rack, "p") with { Devices = [TestDevices.Instance(TestDevices.MidiFx), TestDevices.Instance(TestDevices.Synth)] }, "Lead");

        history.Execute(DeviceCommands.LoadPreset(imported.Id, preset, Definitions));

        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, imported.Id)).Destination);
        Assert.Equal(TrackRole.Hybrid, history.Current.Sequence.FindTrack(imported.Id)!.Role);

        history.Execute(DeviceCommands.MoveChain(rack.Id, ChainOwner.ForTrack(other.Id), Definitions));

        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, other.Id)).Destination);

        // A preset of note effects alone adds no path.
        var notesOnly = DeviceChainPreset.From(DeviceChain.Create(ChainOwner.Rack, "p") with { Devices = [TestDevices.Instance(TestDevices.MidiFx)] }, "Arp");
        var third = Track.Create("Third");
        var plain = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(third) });
        plain.Execute(DeviceCommands.LoadPreset(third.Id, notesOnly, Definitions));
        Assert.Empty(plain.Current.Connections);
    }

    [Fact]
    public void NoAudioOutput_RemovesTheSendsToo_SoNoSendBecomesThePrimaryOutput()
    {
        var history = Empty();
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var verb = MixerChannel.Create("Verb");
        history.Execute(TrackSetup.NewTrack(track, Definitions));
        history.Execute(RoutingCommands.AddMixerChannel(verb));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Mixer(verb.Id)), Definitions));

        history.Execute(TrackSetup.SetAudioDestination([track.Id], null, Definitions));

        Assert.Empty(AudioFrom(history.Current, track.Id));
        Assert.Null(TrackSetup.PrimaryAudioOutput(history.Current, track.Id));
    }

    [Fact]
    public void AGroupedTracksAudioDestination_IsRefused_BecauseItsSoundGoesThroughTheGroup()
    {
        var group = new Track(TrackId.New(), "Group 1", [], role: TrackRole.Group);
        var member = new Track(TrackId.New(), "Bass", [], role: TrackRole.Hybrid, group: group.Id);
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(group).WithTrack(member) };
        var history = new EditHistory(TrackSetup.WithAudioPath(project, member.Id));
        var before = history.Current;

        var refused = Assert.Throws<CommandRefusedException>(() => history.Execute(TrackSetup.SetAudioDestination([member.Id], SignalNode.Master, Definitions)));

        Assert.Contains("Group 1", refused.Message, StringComparison.Ordinal);
        Assert.Same(before, history.Current);
    }

    [Fact]
    public void AnInstrumentInsertedIntoAGroupMember_IsHeardThroughTheGroup_AndStillHeardAfterItLeaves()
    {
        var history = Empty();
        var bass = Track.Create("Bass");
        history.Execute(ProjectCommands.AddTrack(bass));
        var group = TrackId.New();
        history.Execute(GroupCommands.CreateGroup(group, "Group 1", [bass.Id], Definitions));
        Assert.Empty(AudioFrom(history.Current, bass.Id));

        history.Execute(DeviceCommands.InsertDevice(bass.Id, TestDevices.Instance(TestDevices.Synth), Definitions));

        Assert.Equal(SignalNode.Track(group), Assert.Single(AudioFrom(history.Current, bass.Id)).Destination);
        Assert.Contains(history.Current.ParkedConnections, c => c.Kind == SignalKind.Audio && c.Source == SignalNode.Track(bass.Id) && c.Destination == SignalNode.Master);

        history.Execute(GroupCommands.SetGroup(bass.Id, null, Definitions));

        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, bass.Id)).Destination);
        Assert.Empty(history.Current.ParkedConnections);
    }
}
