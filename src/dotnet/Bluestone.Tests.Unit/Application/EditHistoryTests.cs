using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Application;

public sealed class EditHistoryTests
{
    [Fact]
    public void UndoAndRedo_RestoreExactStates()
    {
        var original = Project.CreateNew("A");
        var history = new EditHistory(original);

        history.Execute(ProjectCommands.RenameProject("B"));
        var renamed = history.Current;
        history.Execute(ProjectCommands.RenameProject("C"));

        Assert.Equal("Rename Project", history.UndoLabel);
        history.Undo();
        Assert.Same(renamed, history.Current);
        history.Undo();
        Assert.Same(original, history.Current);
        Assert.False(history.CanUndo);
        history.Redo();
        history.Redo();
        Assert.Equal("C", history.Current.Name);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void NoOpCommands_AreNotRecorded()
    {
        var history = new EditHistory(Project.CreateNew("Same"));
        var changes = 0;
        history.Changed += (_, _) => changes++;

        Assert.False(history.Execute(ProjectCommands.RenameProject("Same")));
        Assert.False(history.Execute(ProjectCommands.SetMuted(TrackId.New(), true)));

        Assert.False(history.CanUndo);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void NewCommand_ClearsRedo()
    {
        var history = new EditHistory(Project.CreateNew("A"));
        history.Execute(ProjectCommands.RenameProject("B"));
        history.Undo();

        history.Execute(ProjectCommands.RenameProject("C"));

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Capacity_DropsTheOldestStates()
    {
        var history = new EditHistory(Project.CreateNew("0"), capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            history.Execute(ProjectCommands.RenameProject(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        while (history.CanUndo)
        {
            history.Undo();
        }

        Assert.Equal("2", history.Current.Name);
    }

    [Fact]
    public void TrackCommands_EditTheRightTrack()
    {
        var track = Track.Create("t");
        var history = new EditHistory(Project.CreateNew());
        var note = new NoteEvent(Tick.Zero, new TickSpan(10), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max);

        history.Execute(ProjectCommands.AddTrack(track));
        history.Execute(ProjectCommands.RenameTrack(track.Id, "Lead"));
        history.Execute(ProjectCommands.SetMuted(track.Id, true));
        history.Execute(ProjectCommands.SetSoloed(track.Id, true));
        var clip = ClipId.New();
        history.Execute(ProjectCommands.AddEvent(track.Id, clip, note));
        history.Execute(ProjectCommands.ReplaceEvent(track.Id, clip, note with { Position = new Tick(5) }));
        history.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Channel = MidiChannel.FromNumber(4), Transpose = 3 }));

        var edited = history.Current.Sequence.FindTrack(track.Id)!;
        Assert.Equal(("Lead", true, true), (edited.Name, edited.IsMuted, edited.IsSoloed));
        Assert.Equal(new Tick(5), Assert.Single(edited.ArrangedEvents).Position);
        Assert.NotNull(TrackOutputs.PrimaryConnection(history.Current, track.Id));
        Assert.NotNull(history.Current.ChainOf(track.Id));

        history.Execute(ProjectCommands.RemoveEvent(track.Id, note.Id));
        Assert.Empty(history.Current.Sequence.FindTrack(track.Id)!.ArrangedEvents);

        history.Execute(ProjectCommands.RemoveTrack(track.Id));
        Assert.Empty(history.Current.Sequence.Tracks);
        Assert.Null(TrackOutputs.PrimaryConnection(history.Current, track.Id));
        Assert.Null(history.Current.ChainOf(track.Id));
        Assert.Empty(history.Current.Connections);
    }

    [Fact]
    public void BatchCommands_UndoAsOneStep()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var history = new EditHistory(Project.CreateNew());
        history.Execute(ProjectCommands.AddTrack(a));
        history.Execute(ProjectCommands.AddTrack(b));

        history.Execute(ProjectCommands.SetMuted([a.Id, b.Id], true));
        Assert.All(history.Current.Sequence.Tracks, t => Assert.True(t.IsMuted));
        Assert.Equal("Mute Tracks", history.UndoLabel);

        history.Undo();
        Assert.All(history.Current.Sequence.Tracks, t => Assert.False(t.IsMuted));
        Assert.False(history.Execute(ProjectCommands.SetSoloed([a.Id, b.Id], false)));
    }

    [Fact]
    public void DuplicateTracks_CopiesBelowWithFreshIdsChainAndRouting()
    {
        var note = new NoteEvent(Tick.Zero, new TickSpan(10), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max);
        var lane = AutomationLane.Create(AutomationTarget.ForPitchBend(MidiChannel.FromIndex(0)));
        var a = Track.FromEvents(TrackId.New(), "Bass", [note]).WithLane(lane);
        var b = Track.Create("Drums");
        var history = new EditHistory(Project.CreateNew());
        history.Execute(ProjectCommands.AddTrack(a));
        history.Execute(ProjectCommands.AddTrack(b));
        history.Execute(ProjectCommands.SetTrackOutput(a.Id, new TrackOutput { Channel = MidiChannel.FromNumber(2), Transpose = -5 }));
        var transpose = TrackOutputs.TransposeDevice(history.Current, a.Id)!;
        var deviceLane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(transpose.Id, BuiltInDevices.TransposeSemitones), [new AutomationPoint(Tick.Zero, ControlValue.Center)]);
        history.Execute(AutomationCommands.AddLane(a.Id, deviceLane));

        history.Execute(ProjectCommands.DuplicateTracks([a.Id]));

        var tracks = history.Current.Sequence.Tracks;
        Assert.Equal(["Bass", "Bass copy", "Drums"], tracks.Select(t => t.Name));
        Assert.NotEqual(a.Id, tracks[1].Id);
        Assert.NotEqual(note.Id, tracks[1].ArrangedEvents.Single().Id);
        Assert.Equal(2, tracks[1].Automation.Length);
        Assert.NotEqual(lane.Id, tracks[1].Automation[0].Id);
        Assert.Equal(lane.Target, tracks[1].Automation[0].Target);
        var copied = TrackOutputs.Read(history.Current, tracks[1].Id);
        Assert.Equal((MidiChannel.FromNumber(2), -5), (copied.Channel!.Value, copied.Transpose));
        var copiedTranspose = TrackOutputs.TransposeDevice(history.Current, tracks[1].Id)!;
        Assert.NotEqual(transpose.Id, copiedTranspose.Id);
        Assert.Equal(copiedTranspose.Id, tracks[1].Automation[1].Target.Device);
        Assert.Equal(transpose.Id, history.Current.Sequence.Tracks[0].Automation[1].Target.Device);
    }

    [Fact]
    public void RemoveTracks_DeletesSeveralInOneStep()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var history = new EditHistory(Project.CreateNew());
        history.Execute(ProjectCommands.AddTrack(a));
        history.Execute(ProjectCommands.AddTrack(b));

        history.Execute(ProjectCommands.RemoveTracks([a.Id, b.Id]));
        Assert.Empty(history.Current.Sequence.Tracks);
        history.Undo();
        Assert.Equal(2, history.Current.Sequence.Tracks.Length);
    }

    [Fact]
    public void TempoAndLoopCommands_ChangeTransportSettings()
    {
        var history = new EditHistory(Project.CreateNew());

        history.Execute(ProjectCommands.SetTempo(Tick.Zero, Tempo.FromBeatsPerMinute(90)));
        history.Execute(ProjectCommands.SetLoop(new TickRange(Tick.Zero, new Tick(100))));

        Assert.Equal(Tempo.FromBeatsPerMinute(90), history.Current.Sequence.TempoMap.TempoAt(Tick.Zero));
        Assert.Equal(new Tick(100), history.Current.Loop!.End);
        Assert.False(history.Execute(ProjectCommands.SetTempo(Tick.Zero, Tempo.FromBeatsPerMinute(90))));
    }

    [Fact]
    public void EditsWithTheSameMergeKey_InQuickSuccession_AreOneUndoStep()
    {
        var time = new ManualTime();
        var history = new EditHistory(Project.CreateNew(), time: time);
        IProjectCommand Rename(string name, string? key) => new ProjectCommand("Rename Project", p => p with { Name = name }) { MergeKey = key };

        history.Execute(Rename("a", "drag"));
        time.Advance(TimeSpan.FromMilliseconds(300));
        history.Execute(Rename("b", "drag"));
        time.Advance(TimeSpan.FromMilliseconds(300));
        history.Execute(Rename("c", "drag"));
        history.Undo();

        Assert.Equal("Untitled", history.Current.Name);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Merging_StopsAfterAPause_ADifferentKey_OrAnUndo()
    {
        var time = new ManualTime();
        var history = new EditHistory(Project.CreateNew(), time: time);
        IProjectCommand Rename(string name, string? key) => new ProjectCommand("Rename Project", p => p with { Name = name }) { MergeKey = key };

        history.Execute(Rename("a", "drag"));
        time.Advance(EditHistory.MergeWindow + TimeSpan.FromMilliseconds(1));
        history.Execute(Rename("b", "drag"));
        history.Execute(Rename("c", "other"));
        history.Execute(Rename("d", null));
        history.Execute(Rename("e", null));

        history.Undo();
        Assert.Equal("d", history.Current.Name);
        history.Undo();
        Assert.Equal("c", history.Current.Name);
        history.Undo();
        Assert.Equal("b", history.Current.Name);
        history.Execute(Rename("f", "drag"));
        history.Undo();
        Assert.Equal("b", history.Current.Name);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
    }
}
