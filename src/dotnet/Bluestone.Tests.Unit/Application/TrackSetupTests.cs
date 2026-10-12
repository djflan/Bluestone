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
}
