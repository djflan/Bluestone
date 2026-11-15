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
        // The track already outputs through its parent Group, so inserting an instrument changes no routing.
        var group = Track.Create("Pads", TrackRole.Group);
        var track = Track.Create("Pad", TrackRole.Hybrid).WithGroup(group.Id);
        var toGroup = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Track(group.Id));
        var groupOutput = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(group.Id), SignalNode.Master);
        var history = new EditHistory(Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track).WithTrack(group),
            Connections = [toGroup, groupOutput],
        });

        history.Execute(DeviceCommands.InsertDevice(track.Id, TestDevices.Instance(TestDevices.Synth), Definitions));

        Assert.Equal([toGroup, groupOutput], history.Current.Connections);
    }

    [Fact]
    public void AnAudioTapFromTheChain_CountsAsAPath()
    {
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) });
        history.Execute(DeviceCommands.InsertDevice(track.Id, synth, Definitions));
        var channel = MixerChannel.Create("Device tap");
        history.Execute(RoutingCommands.AddMixerChannel(channel));
        var tap = SignalConnection.Create(SignalKind.Audio, SignalNode.Device(synth.Id), SignalNode.Mixer(channel.Id)) with { AudioPurpose = AudioRoutePurpose.Internal };
        history.Execute(RoutingCommands.Connect(tap, Definitions));

        history.Execute(DeviceCommands.InsertDevice(track.Id, TestDevices.Instance(TestDevices.Filter), Definitions));

        Assert.Contains(tap, history.Current.Connections);
        Assert.Single(history.Current.Connections, c => c.AudioPurpose == AudioRoutePurpose.Output && c.Source == SignalNode.Track(track.Id));
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
    public void GroupingSeveralTracks_RepointsTheirOutputsThroughTheGroupInOneUndoStep()
    {
        var history = Empty();
        var a = Track.Create("A", TrackRole.Hybrid);
        var b = Track.Create("B", TrackRole.Hybrid);
        var group = Track.Create("Bus", TrackRole.Group);
        history.Execute(TrackSetup.NewTrack(a, Definitions));
        history.Execute(TrackSetup.NewTrack(b, Definitions));
        history.Execute(TrackSetup.NewTrack(group, Definitions));
        history.Execute(GroupCommands.SetGroup([a.Id, b.Id], group.Id, Definitions));

        Assert.Equal("Add To Group", history.UndoLabel);
        Assert.All([a.Id, b.Id], t => Assert.Equal(SignalNode.Track(group.Id), Assert.Single(AudioFrom(history.Current, t)).Destination));
        history.Undo();
        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, a.Id)).Destination);
        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, b.Id)).Destination);
    }

    [Fact]
    public void OnlyThePrimaryAudioOutput_IsRepointed_AndASendToTheChosenDestination_TakesItsPlace()
    {
        var history = Empty();
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var effect = Track.Create("Verb", TrackRole.Effect);
        history.Execute(TrackSetup.NewTrack(track, Definitions));
        history.Execute(TrackSetup.NewTrack(effect, Definitions));
        var send = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Track(effect.Id)) with { AudioPurpose = AudioRoutePurpose.Send };
        history.Execute(RoutingCommands.Connect(send, Definitions));

        history.Execute(TrackSetup.SetAudioDestination([track.Id], SignalNode.Master, Definitions));

        Assert.Equal([AudioRoutePurpose.Output, AudioRoutePurpose.Send], AudioFrom(history.Current, track.Id).Select(c => c.AudioPurpose));
        Assert.Equal(SignalNode.Master, TrackSetup.PrimaryAudioOutput(history.Current, track.Id)!.Destination);

        // A send to an Effect track cannot become the ordinary Output; the send remains a separate route.
        Assert.Throws<CommandRefusedException>(() => history.Execute(TrackSetup.SetAudioDestination([track.Id], SignalNode.Track(effect.Id), Definitions)));

        Assert.Contains(send, history.Current.Connections);
        Assert.Equal(SignalNode.Master, TrackSetup.PrimaryAudioOutput(history.Current, track.Id)!.Destination);
    }

    [Fact]
    public void AnAudioDestinationThatWouldFeedBack_IsRefused_AndChangesNothing()
    {
        var a = Track.Create("A", TrackRole.Group);
        var b = Track.Create("B", TrackRole.Group).WithGroup(a.Id);
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(a).WithTrack(b),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(a.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(b.Id), SignalNode.Track(a.Id)),
            ],
        };
        var history = new EditHistory(project);
        var before = history.Current;

        Assert.Throws<CommandRefusedException>(() => history.Execute(RoutingCommands.Connect(
            SignalConnection.Create(SignalKind.Audio, SignalNode.Track(a.Id), SignalNode.Track(b.Id)) with { AudioPurpose = AudioRoutePurpose.Internal }, Definitions)));

        Assert.Same(before, history.Current);
    }

    [Fact]
    public void RemovingTheDeviceWhoseTapWasTheOnlyPath_LeavesTheTrackUnrouted_RatherThanRoutingItAgainBehindTheMusiciansBack()
    {
        var track = Track.Create("Lead", TrackRole.Instrument);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var channel = MixerChannel.Create("Device tap");
        var project = (Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track),
            Mixer = Project.CreateNew().Mixer.With(channel),
        }).WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [synth] }) with
        {
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Device(synth.Id), SignalNode.Mixer(channel.Id)) with { AudioPurpose = AudioRoutePurpose.Internal }],
        };
        var history = new EditHistory(project);

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
    public void RemovingAnAudioOutput_IsRefused_AndNeverPromotesASendToAnOutput()
    {
        var history = Empty();
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var effect = Track.Create("Verb", TrackRole.Effect);
        history.Execute(TrackSetup.NewTrack(track, Definitions));
        history.Execute(TrackSetup.NewTrack(effect, Definitions));
        var send = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Track(effect.Id)) with { AudioPurpose = AudioRoutePurpose.Send };
        history.Execute(RoutingCommands.Connect(send, Definitions));

        Assert.Throws<CommandRefusedException>(() => history.Execute(TrackSetup.SetAudioDestination([track.Id], null, Definitions)));

        Assert.Single(AudioFrom(history.Current, track.Id), c => c.AudioPurpose == AudioRoutePurpose.Output);
        Assert.Contains(send, history.Current.Connections);
        Assert.Equal(SignalNode.Master, TrackSetup.PrimaryAudioOutput(history.Current, track.Id)!.Destination);
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
        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, group)).Destination);
        Assert.Contains(history.Current.ParkedConnections, c => c.Kind == SignalKind.Audio && c.Source == SignalNode.Track(bass.Id) && c.Destination == SignalNode.Master);

        history.Execute(GroupCommands.SetGroup(bass.Id, null, Definitions));

        Assert.Equal(SignalNode.Master, Assert.Single(AudioFrom(history.Current, bass.Id)).Destination);
        Assert.Empty(history.Current.ParkedConnections);
    }
}
