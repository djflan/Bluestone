using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

/// <summary>Putting tracks in groups routes them there and back, as one undo step, refusing routing errors (ADR 0030).</summary>
public sealed class GroupCommandsTests
{
    private static readonly DeviceCatalog Catalog = DeviceCatalog.BuiltIn.With(TestDevices.Synth);
    private static readonly DeviceDefinitionLookup Definitions = Catalog.Lookup;

    private static Track Notes(string name, int note) =>
        Track.FromEvents(TrackId.New(), name, [new NoteEvent(Tick.Zero, new TickSpan(120), MidiChannel.FromNumber(1), new NoteNumber(note), new Velocity(100))]);

    /// <summary>Two tracks sent to one MU2000, forced to channels 1 and 2, and an empty group track.</summary>
    private static (EditHistory History, Track Bass, Track Keys, Track Band, ExternalInstrument Mu2000) Band()
    {
        var bass = Notes("Bass", 40);
        var keys = Notes("Keys", 60);
        var band = Track.Create("Band", TrackRole.Group);
        var mu2000 = ExternalInstrument.Create("MU2000", new EndpointReference("test", "mu", "MU2000"));
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(band).WithTrack(bass).WithTrack(keys),
            Instruments = [mu2000],
            Connections =
            [
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(bass.Id), SignalNode.ExternalPart(mu2000.Id)) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(1)) },
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id)) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(2)) },
            ],
        };
        return (new EditHistory(project), bass, keys, band, mu2000);
    }

    private static List<SignalConnection> From(Project project, Track track) => [.. project.ConnectionsFrom(SignalNode.Track(track.Id))];

    private static List<(int Note, int Channel)> Sent(Project project) =>
        [.. SignalGraph.Evaluate(project, Catalog).ExternalParts.SelectMany(p => p.Events).Select(e => e.Event).OfType<NoteEvent>().Select(n => ((int)n.Note.Value, n.Channel.Number)).Order()];

    [Fact]
    public void Joining_ReplacesTheTracksOutputs_WithOneConnectionIntoTheGroup_KeepingItsForcedChannel()
    {
        var (history, bass, _, band, _) = Band();
        var before = history.Current;
        var original = From(before, bass).Single();

        history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions));

        var project = history.Current;
        var route = From(project, bass).Single();
        Assert.Equal(band.Id, project.Sequence.FindTrack(bass.Id)!.Group);
        Assert.Equal(SignalNode.Track(band.Id), route.Destination);
        Assert.Equal(SignalKind.Events, route.Kind);
        Assert.Equal(MidiChannel.FromNumber(1), route.Mapping.Force);
        Assert.Equal<SignalConnection>([original], project.ParkedConnections);
        Assert.Equal("Add To Group", history.UndoLabel);

        history.Undo();
        Assert.Same(before, history.Current);
        history.Redo();
        Assert.Same(project, history.Current);
    }

    [Fact]
    public void Leaving_RemovesTheGroupConnection_AndRestoresTheParkedOutputs()
    {
        var (history, bass, _, band, _) = Band();
        var original = From(history.Current, bass).Single();
        history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions));

        history.Execute(GroupCommands.SetGroup(bass.Id, null, Definitions));

        var project = history.Current;
        Assert.Null(project.Sequence.FindTrack(bass.Id)!.Group);
        Assert.Equal([original], From(project, bass));
        Assert.Empty(project.ParkedConnections);
        Assert.Empty(project.ConnectionsTo(SignalNode.Track(band.Id)));
        Assert.Equal("Remove From Group", history.UndoLabel);
    }

    [Fact]
    public void Leaving_LeavesOutAParkedOutputThatNoLongerWorks()
    {
        var (history, bass, _, band, mu2000) = Band();
        history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions));
        history.Execute(new ProjectCommand("Remove the MU2000", p => p with
        {
            Instruments = [],
            Connections = p.Connections.RemoveAll(c => c.Destination.Kind == SignalNodeKind.ExternalInstrument),
        }));

        history.Execute(GroupCommands.SetGroup(bass.Id, null, Definitions));

        Assert.Empty(From(history.Current, bass));
        Assert.Empty(history.Current.ParkedConnections);
        Assert.DoesNotContain(history.Current.Instruments, i => i.Id == mu2000.Id);
    }

    [Fact]
    public void MovingToAnotherGroup_RoutesThereInstead_AndStillRestoresTheOriginalOutputLater()
    {
        var (history, bass, _, band, _) = Band();
        var original = From(history.Current, bass).Single();
        var rhythm = Track.Create("Rhythm", TrackRole.Group);
        history.Execute(ProjectCommands.AddTrack(rhythm));
        history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions));

        history.Execute(GroupCommands.SetGroup(bass.Id, rhythm.Id, Definitions));

        Assert.Equal(SignalNode.Track(rhythm.Id), From(history.Current, bass).Single().Destination);
        Assert.Equal<SignalConnection>([original], history.Current.ParkedConnections);
        history.Execute(GroupCommands.SetGroup(bass.Id, null, Definitions));
        Assert.Equal([original], From(history.Current, bass));
    }

    [Fact]
    public void Joining_IsRefused_WhenItWouldCloseALoop()
    {
        var (history, bass, _, band, _) = Band();
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(band.Id), SignalNode.Track(bass.Id)), Definitions));
        var before = history.Current;

        var refused = Assert.Throws<CommandRefusedException>(() => history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions)));

        Assert.Contains("feed back", refused.Message, StringComparison.Ordinal);
        Assert.Same(before, history.Current);
    }

    [Fact]
    public void AGroup_CannotGoInsideItself_OrOneOfItsMembers_OrAnInstrumentTrack()
    {
        var (history, bass, keys, band, _) = Band();
        var inner = Track.Create("Inner", TrackRole.Group);
        history.Execute(ProjectCommands.AddTrack(inner));
        history.Execute(GroupCommands.SetGroup(inner.Id, band.Id, Definitions));

        Assert.Throws<CommandRefusedException>(() => history.Execute(GroupCommands.SetGroup(band.Id, inner.Id, Definitions)));
        Assert.Throws<CommandRefusedException>(() => history.Execute(GroupCommands.SetGroup(band.Id, band.Id, Definitions)));
        Assert.Throws<CommandRefusedException>(() => history.Execute(GroupCommands.SetGroup(bass.Id, keys.Id, Definitions)));
        Assert.Null(history.Current.Sequence.FindTrack(band.Id)!.Group);
    }

    [Fact]
    public void CreateGroup_PutsTheGroupAboveTheFirstMember_AndKeepsWhatIsSent()
    {
        var (history, bass, keys, _, mu2000) = Band();
        var sentBefore = Sent(history.Current);
        var group = TrackId.New();

        history.Execute(GroupCommands.CreateGroup(group, "Rhythm", [keys.Id, bass.Id], Definitions));

        var project = history.Current;
        Assert.Equal(["Band", "Rhythm", "Bass", "Keys"], project.Sequence.Tracks.Select(t => t.Name));
        Assert.Equal(TrackRole.Group, project.Sequence.FindTrack(group)!.Role);
        Assert.All([bass, keys], t => Assert.Equal(group, project.Sequence.FindTrack(t.Id)!.Group));
        Assert.Equal(SignalNode.ExternalPart(mu2000.Id), project.ConnectionsFrom(SignalNode.Track(group)).Single().Destination);
        Assert.Equal([(40, 1), (60, 2)], sentBefore);
        Assert.Equal(sentBefore, Sent(project));
        Assert.Equal("Group Tracks", history.UndoLabel);
    }

    [Fact]
    public void CreateGroup_LeavesTheGroupUnrouted_WhenTheMembersWentToDifferentPlaces()
    {
        var (history, bass, keys, _, _) = Band();
        var jv = ExternalInstrument.Create("JV-1080", new EndpointReference("test", "jv", "JV-1080"));
        history.Execute(RoutingCommands.AddInstrument(jv));
        var keysOut = From(history.Current, keys).Single();
        history.Execute(RoutingCommands.UpdateConnection(keysOut with { Destination = SignalNode.ExternalPart(jv.Id) }, Definitions));
        var group = TrackId.New();

        history.Execute(GroupCommands.CreateGroup(group, "Rhythm", [bass.Id, keys.Id], Definitions));

        Assert.Empty(history.Current.ConnectionsFrom(SignalNode.Track(group)));
        Assert.Empty(Sent(history.Current));
    }

    [Fact]
    public void AGroupInAGroup_CreatedFromMembers_StaysInTheirGroup()
    {
        var (history, bass, keys, band, _) = Band();
        history.Execute(GroupCommands.SetGroup([bass.Id, keys.Id], band.Id, Definitions));
        var inner = TrackId.New();

        history.Execute(GroupCommands.CreateGroup(inner, "Low", [bass.Id], Definitions));

        var project = history.Current;
        Assert.Equal(band.Id, project.Sequence.FindTrack(inner)!.Group);
        Assert.Equal(inner, project.Sequence.FindTrack(bass.Id)!.Group);
        Assert.Equal(SignalNode.Track(band.Id), project.ConnectionsFrom(SignalNode.Track(inner)).Single().Destination);
    }

    [Fact]
    public void DeletingAGroup_MovesItsMembersOut_WithTheirOwnOutputsBack()
    {
        var (history, bass, keys, band, _) = Band();
        var before = history.Current;
        history.Execute(GroupCommands.SetGroup([bass.Id, keys.Id], band.Id, Definitions));

        history.Execute(ProjectCommands.RemoveTrack(band.Id));

        var project = history.Current;
        Assert.All([bass, keys], t => Assert.Null(project.Sequence.FindTrack(t.Id)!.Group));
        Assert.Equal<SignalConnection>(before.Connections, project.Connections);
        Assert.Empty(project.ParkedConnections);
    }

    [Fact]
    public void DeletingANestedGroup_MovesItsMembersToTheGroupAroundIt()
    {
        var (history, bass, _, band, _) = Band();
        var inner = TrackId.New();
        history.Execute(GroupCommands.CreateGroup(inner, "Low", [bass.Id], Definitions));
        history.Execute(GroupCommands.SetGroup(inner, band.Id, Definitions));

        history.Execute(ProjectCommands.RemoveTrack(inner));

        Assert.Equal(band.Id, history.Current.Sequence.FindTrack(bass.Id)!.Group);
        Assert.Equal(SignalNode.Track(band.Id), From(history.Current, bass).Single().Destination);
    }

    [Fact]
    public void DeletingAMember_DropsItsParkedOutputs()
    {
        var (history, bass, _, band, _) = Band();
        history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions));

        history.Execute(ProjectCommands.RemoveTrack(bass.Id));

        Assert.Empty(history.Current.ParkedConnections);
    }

    [Fact]
    public void DuplicatingAMember_CopiesItsParkedOutputs_SoTheCopyCanLeaveTheGroupToo()
    {
        var (history, bass, _, band, mu2000) = Band();
        history.Execute(GroupCommands.SetGroup(bass.Id, band.Id, Definitions));
        history.Execute(ProjectCommands.DuplicateTracks([bass.Id]));
        var copy = history.Current.Sequence.Tracks.Single(t => t.Name == "Bass copy");

        history.Execute(GroupCommands.SetGroup(copy.Id, null, Definitions));

        Assert.Equal(band.Id, copy.Group);
        Assert.Equal(SignalNode.ExternalPart(mu2000.Id), From(history.Current, copy).Single().Destination);
        Assert.Single(history.Current.ParkedConnections);
    }

    [Fact]
    public void AnAudioTrack_JoinsWithAnAudioConnection()
    {
        var (history, _, _, band, _) = Band();
        var vocals = Track.Create("Vocals", TrackRole.Audio);
        var bus = MixerChannel.Create("Vocal bus");
        history.Execute(ProjectCommands.AddTrack(vocals));
        history.Execute(RoutingCommands.AddMixerChannel(bus));
        history.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocals.Id), SignalNode.Mixer(bus.Id)), Definitions));

        history.Execute(GroupCommands.SetGroup(vocals.Id, band.Id, Definitions));

        var route = From(history.Current, vocals).Single();
        Assert.Equal(SignalKind.Audio, route.Kind);
        Assert.Equal(SignalNode.Track(band.Id), route.Destination);
    }

    [Fact]
    public void Folding_IsOneUndoStep_AndOnlyForGroupTracks()
    {
        var (history, bass, _, band, _) = Band();

        history.Execute(GroupCommands.SetFolded(band.Id, true));
        var afterBass = history.Execute(GroupCommands.SetFolded(bass.Id, true));

        Assert.True(history.Current.Sequence.FindTrack(band.Id)!.IsFolded);
        Assert.False(afterBass);
        Assert.Equal("Fold Group", history.UndoLabel);
        history.Undo();
        Assert.False(history.Current.Sequence.FindTrack(band.Id)!.IsFolded);
    }
}
