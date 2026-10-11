using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>The piano roll and event list view models, and the transport's recording controls.</summary>
public sealed class EditorViewModelTests : IAsyncLifetime
{
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _endpoints;
    private readonly ProjectSession _session = new();
    private readonly PlaybackController _playback;
    private readonly MainViewModel _vm;
    private readonly LoopbackPort _synth;
    private readonly LoopbackPort _keys;

    public EditorViewModelTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _synth = _provider.CreatePort("Synth", "synth");
        _keys = _provider.CreatePort("Keys", "keys");
        _endpoints = new EndpointDirectory([_provider]);
        _playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        _vm = new MainViewModel(_session, _playback, _endpoints, new NoUi(), new ImmediateDispatcher());
    }

    private EditorViewModel Editor => _vm.Editor;

    private Track Track => _session.Project.Sequence.Tracks[0];

    private static NoteEvent Note(long at, int pitch = 60, long length = 240, int velocity = 100) =>
        new(new Tick(at), new TickSpan(length), MidiChannel.FromIndex(0), new NoteNumber(pitch), new Velocity(velocity));

    public async ValueTask InitializeAsync()
    {
        await _vm.InitializeAsync();
        var track = Track.FromEvents(TrackId.New(), "Piano", [Note(0), Note(960, 64), Note(1920, 67)]);
        _session.Execute(ProjectCommands.AddTrack(track));
        _session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, _synth.OutputId.Value, "Synth") }));
        _vm.LowerPane = LowerPane.EventList;
        await Settle();
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _endpoints.Dispose();
        _provider.Dispose();
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    [Fact]
    public void Editor_FollowsTheSelectedTrack()
    {
        Assert.Equal(Track.Id, Editor.Track?.Id);
        Assert.Equal(3, Editor.Track!.ArrangedEvents.Length);
    }

    [Fact]
    public void AddNote_UsesTheLastLengthAndSelectsIt()
    {
        Editor.NewNoteLength = 120;

        var id = Editor.AddNote(1920, 72);

        var note = Assert.IsType<NoteEvent>(Track.ArrangedEvents.Single(e => e.Id == id));
        Assert.Equal((1920L, 120L, 72), (note.Position.Value, note.Duration.Value, (int)note.Note.Value));
        Assert.Equal([id!.Value], Editor.SelectedEvents);
        Assert.Equal("Add Note", _session.History.UndoLabel);
    }

    [Fact]
    public void MarqueeSelection_FindsNotesInTheRectangle()
    {
        Editor.SelectNotesIn(400, 2000, 62, 70);

        Assert.Equal(2, Editor.SelectionCount);
        Assert.Equal("2 notes", Editor.SelectionSummary);

        Editor.SelectNotesIn(0, 10, 60, 60, SelectionMode.Toggle);
        Assert.Equal(3, Editor.SelectionCount);
    }

    [Fact]
    public void MoveAndCopy_AreSingleUndoSteps()
    {
        Editor.SelectAll();

        Editor.MoveSelection(240, 2);
        Assert.Equal([240L, 1200L, 2160L], Track.ArrangedEvents.Select(e => e.Position.Value));
        Assert.Equal("Move Notes", _session.History.UndoLabel);

        Editor.MoveSelection(1920, 0, copy: true);
        Assert.Equal(6, Track.ArrangedEvents.Length);
        Assert.Equal(3, Editor.SelectionCount);
        Assert.All(Editor.SelectedNotes, n => Assert.True(n.Position.Value >= 1920));

        _vm.UndoCommand.Execute(null);
        _vm.UndoCommand.Execute(null);
        Assert.Equal([0L, 960L, 1920L], Track.ArrangedEvents.Select(e => e.Position.Value));
    }

    [Fact]
    public void Selection_SurvivesEditsAndUndo()
    {
        var first = Track.ArrangedEvents[0].Id;
        Editor.Select([first]);

        Editor.TransposeSelection(12);
        _vm.UndoCommand.Execute(null);

        Assert.Equal([first], Editor.SelectedEvents);
        Assert.Equal("C4", Editor.InfoPitch);
    }

    [Fact]
    public void InfoFields_EditTheSelection()
    {
        Editor.Select([Track.ArrangedEvents[1].Id]);
        Assert.Equal("1.2.000", Editor.InfoPosition);
        Assert.Equal("0.0.240", Editor.InfoLength);

        Editor.InfoVelocity = "64";
        Editor.InfoPitch = "G4";
        Editor.InfoLength = "0.1.000";
        Editor.InfoPosition = "2.1.0";

        var note = Assert.IsType<NoteEvent>(Track.ArrangedEvents.Single(e => e.Id == Editor.SelectedEvents.Single()));
        Assert.Equal((64, 67, 960L, 3840L), ((int)note.Velocity.Value, (int)note.Note.Value, note.Duration.Value, note.Position.Value));
    }

    [Fact]
    public void Quantize_WithoutSelection_QuantizesTheWholeTrack()
    {
        _session.Execute(ProjectCommands.ReplaceEvents(Track.Id, Track.Clips[0].Id, "Humanize", [.. Track.ArrangedEvents.Select(e => e with { Position = new Tick(e.Position.Value + 37) })]));
        Editor.SelectNone();
        Editor.QuantizeGrid = GridOption.For(GridDivision.Eighth);

        Editor.QuantizeCommand.Execute(null);

        Assert.Equal([0L, 960L, 1920L], Track.ArrangedEvents.Select(e => e.Position.Value));
        Assert.Equal("Quantize", _session.History.UndoLabel);
    }

    [Fact]
    public void CopyAndPaste_PastesAtTheSnappedPlayhead()
    {
        Editor.Select([Track.ArrangedEvents[1].Id, Track.ArrangedEvents[2].Id]);
        Editor.CopyCommand.Execute(null);
        _vm.SeekTo(3850);

        Editor.PasteCommand.Execute(null);

        Assert.Equal([3840L, 4800L], Editor.SelectedNotes.Select(n => n.Position.Value));
        Assert.Equal(5, Track.ArrangedEvents.Length);
    }

    [Fact]
    public void Duplicate_PlacesTheCopyAfterTheSelection()
    {
        Editor.Select([Track.ArrangedEvents[0].Id]);
        Editor.Snap = GridOption.For(GridDivision.Quarter);

        Editor.DuplicateCommand.Execute(null);

        // The note ends at tick 240; the copy starts at the next quarter-note line.
        Assert.Equal(960, Editor.SelectedNotes.Single().Position.Value);
    }

    [Fact]
    public void Resize_SetsTheLengthForTheNextNote()
    {
        Editor.Select([Track.ArrangedEvents[0].Id]);

        Editor.ResizeSelection(NoteEdge.End, 720);

        Assert.Equal(960, ((NoteEvent)Track.ArrangedEvents[0]).Duration.Value);
        Assert.Equal(960, Editor.NewNoteLength);
    }

    [Fact]
    public void ControllerLine_ReplacesTheLaneInTheRange()
    {
        Editor.Lane = ControllerLane.Standard.Single(l => l.Name.StartsWith("Modulation", StringComparison.Ordinal));
        Editor.Snap = GridOption.For(GridDivision.Quarter);

        Editor.DrawControllerLine(0, 0, 3840, 120);
        Editor.DrawControllerLine(1920, 10, 1920, 10);

        var values = Editor.LaneEvents().Select(e => (e.Position.Value, ControllerLane.ValueOf(e))).ToList();
        Assert.Equal([(0L, 0), (960L, 30), (1920L, 10), (2880L, 90), (3840L, 120)], values);

        Editor.EraseControllers(0, 2000);
        Assert.Equal(2, Editor.LaneEvents().Count());
    }

    [Fact]
    public void EventList_ShowsAndEditsEvents()
    {
        var rows = _vm.EventList.Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal(("Note", "1.2.000", "E4", "100"), (rows[1].Kind, rows[1].Position, rows[1].Data1, rows[1].Data2));

        rows[1].Data2 = "33";
        rows[1].Data1 = "61";

        var edited = Assert.IsType<NoteEvent>(Track.ArrangedEvents[1]);
        Assert.Equal((33, 61), ((int)edited.Velocity.Value, (int)edited.Note.Value));
        Assert.Equal("C♯4", _vm.EventList.Rows[1].Data1);
    }

    [Fact]
    public void EventList_EditingOneEvent_KeepsTheOtherRows()
    {
        var untouched = _vm.EventList.Rows[0];
        var replaced = 0;
        _vm.EventList.Rows.CollectionChanged += (_, e) => replaced += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace ? 1 : 100;

        _vm.EventList.Rows[1].Data2 = "50";

        Assert.Same(untouched, _vm.EventList.Rows[0]);
        Assert.Equal(1, replaced);

        _vm.EventList.Rows[2].Position = "1.1.100";
        Assert.Equal(["1.1.000", "1.1.100", "1.2.000"], _vm.EventList.Rows.Select(r => r.Position));
    }

    [Fact]
    public void EventList_SharesTheSelection()
    {
        Editor.Select([Track.ArrangedEvents[2].Id]);
        Assert.Equal([Track.ArrangedEvents[2].Id], _vm.EventList.SelectedRows.Select(r => r.Id));

        _vm.EventList.SelectedRows.Clear();
        _vm.EventList.SelectedRows.Add(_vm.EventList.Rows[0]);
        _vm.EventList.OnRowsSelected();

        Assert.Equal([Track.ArrangedEvents[0].Id], Editor.SelectedEvents);
    }

    [Fact]
    public void EventList_FiltersByKind()
    {
        _session.Execute(ProjectCommands.AddEvents(Track.Id, Track.Clips[0].Id, "Add", [new ProgramEvent(Tick.Zero, MidiChannel.FromIndex(0), new ProgramSelection(new ProgramNumber(4)))]));

        _vm.EventList.Filter = EventFilter.All.Single(f => f.Name == "Program Changes");

        var row = Assert.Single(_vm.EventList.Rows);
        Assert.Equal(("Program", "4"), (row.Kind, row.Data1));
    }

    [Fact]
    public async Task Record_PianoKeysPlayAndAreRecorded()
    {
        _vm.IsCountInEnabled = false;
        _vm.ToggleArm(_vm.Tracks[0]);
        await Settle();
        _vm.SeekTo(3840);

        await _vm.RecordCommand.ExecuteAsync(null);
        _playback.Engine.Pump();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        _playback.Engine.Pump();
        Editor.PlayKey(72, 90);
        _playback.Engine.Pump();
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        _playback.Engine.Pump();
        Editor.EndAudition();
        _playback.Engine.Pump();
        _vm.StopCommand.Execute(null);

        Assert.Contains(_synth.Sent, m => m.Bytes is [0x90, 72, 90]);
        var take = Assert.IsType<NoteEvent>(Track.ArrangedEvents[^1]);
        Assert.Equal((4800L, 480L, 72), (take.Position.Value, take.Duration.Value, (int)take.Note.Value));
    }

    [Fact]
    public async Task Record_FromTheTransport_AddsATakeToTheArmedTrack()
    {
        _vm.SelectedInput = _vm.Inputs.Single(i => i.Name == "Keys");
        _vm.IsCountInEnabled = false;
        _vm.ToggleArm(_vm.Tracks[0]);
        await Settle();
        _vm.SeekTo(3840);

        await _vm.RecordCommand.ExecuteAsync(null);
        _playback.Engine.Pump();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        _playback.Engine.Pump();
        _keys.Inject([0x90, 72, 90]);
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        _playback.Engine.Pump();
        _keys.Inject([0x80, 72, 64]);
        _vm.OnFrame();
        Assert.True(_vm.IsRecording);
        Assert.True(_vm.HasInputActivity);
        _vm.StopCommand.Execute(null);

        var take = Assert.IsType<NoteEvent>(Track.ArrangedEvents[^1]);
        Assert.Equal((4800L, 480L, 72), (take.Position.Value, take.Duration.Value, (int)take.Note.Value));
        Assert.False(_vm.IsRecording);
        Assert.Contains(_vm.Messages, m => m.Text == "Recorded 1 note on Piano.");

        // The note was also echoed to the track's output while it was played.
        Assert.Contains(_synth.Sent, m => m.Bytes is [0x90, 72, 90]);
    }

    [Fact]
    public void Editor_EditsOneClipAndFollowsEventsAddedToAnother()
    {
        var first = Track.Clips[0];
        _session.Execute(ClipCommands.CopyClips(Track.Id, [first.Id], 3840));
        var second = Track.Clips[1];

        Assert.Equal(first.Id, Editor.Clip?.Id);
        Assert.Equal(3, Editor.Events.Length);

        var id = Editor.AddNote(4000, 72);

        Assert.Equal(second.Id, Editor.Clip?.Id);
        Assert.Equal(4, Editor.Events.Length);
        Assert.Equal([id!.Value], Editor.SelectedEvents);

        Editor.EditClip(first.Id);
        Assert.Equal(first.Id, Editor.Clip?.Id);
        Assert.Empty(Editor.SelectedEvents);
    }

    [Fact]
    public void AddNote_BetweenClips_GrowsTheEditedClip()
    {
        var first = Track.Clips[0];
        _session.Execute(ClipCommands.CopyClips(Track.Id, [first.Id], 3840));

        Editor.AddNote(2500, 72);

        Assert.Equal((first.Id, 3840L), (Track.Clips[0].Id, Track.Clips[0].End.Value));
        Assert.Equal(2, Track.Clips.Length);
    }

    [Fact]
    public void AddNote_OnATrackWithoutClips_CreatesOne()
    {
        var empty = Track.Create("Empty");
        _session.Execute(ProjectCommands.AddTrack(empty));
        _vm.SelectedTracks.Clear();
        _vm.SelectedTracks.Add(_vm.Tracks[^1]);
        Assert.Null(Editor.Clip);

        Editor.AddNote(100, 60);

        var clip = Assert.Single(_session.Project.Sequence.FindTrack(empty.Id)!.Clips);
        Assert.Equal(clip.Id, Editor.Clip?.Id);
        Assert.Single(Editor.Events);
    }

    [Fact]
    public void Arrangement_MovesAndCopiesTheSelectedClips()
    {
        var clip = Track.Clips[0];
        _vm.Arrangement.SelectClip(clip.Id);

        _vm.Arrangement.MoveSelection(3840, 0, copy: false);
        Assert.Equal([3840L, 4800L, 5760L], Track.ArrangedEvents.Select(e => e.Position.Value));
        Assert.Equal("Move Clip", _session.History.UndoLabel);

        _vm.Arrangement.MoveSelection(-3840, 0, copy: true);
        Assert.Equal(6, Track.ArrangedEvents.Length);
        Assert.Equal(0, Track.ArrangedEvents[0].Position.Value);
        Assert.Equal("Copy Clip", _session.History.UndoLabel);

        // Nothing moves before tick 0; landing there, the clip replaces the copy it covers.
        _vm.Arrangement.MoveSelection(-100_000, 0, copy: false);
        Assert.Equal((clip.Id, 0L), (Assert.Single(Track.Clips).Id, Track.Clips[0].Start.Value));
    }

    [Fact]
    public void Arrangement_MovesClipsToOtherTracks()
    {
        _session.Execute(ProjectCommands.AddTrack(Track.Create("Empty")));
        var clip = Track.Clips[0];
        _vm.Arrangement.SelectClip(clip.Id);

        _vm.Arrangement.MoveSelection(0, 5, copy: false);

        var tracks = _session.Project.Sequence.Tracks;
        Assert.Empty(tracks[0].Clips);
        Assert.Equal(clip.Id, Assert.Single(tracks[1].Clips).Id);
        Assert.Contains(clip.Id, _vm.Arrangement.SelectedClips);
    }

    [Fact]
    public void Arrangement_SplitsResizesDuplicatesAndDeletes()
    {
        var clip = Track.Clips[0];
        _vm.SeekTo(1000);

        _vm.Arrangement.SplitAtPlayhead();
        Assert.Equal([(0L, 1000L), (1000L, 2160L)], Track.Clips.Select(c => (c.Start.Value, c.End.Value)));
        Assert.Equal("Split Clip", _session.History.UndoLabel);

        _vm.Arrangement.ResizeClip(clip.Id, 0, 500);
        Assert.Equal([0L, 1920L], Track.ArrangedEvents.Select(e => e.Position.Value));
        _vm.Arrangement.ResizeClip(clip.Id, 0, 5000);
        Assert.Equal(1000, Track.Clips[0].End.Value);

        _vm.Arrangement.SelectClip(clip.Id);
        _vm.Arrangement.DuplicateSelection();
        Assert.Equal(3, Track.Clips.Length);
        Assert.Equal("Duplicate Clip", _session.History.UndoLabel);

        _vm.Arrangement.DeleteSelection();
        Assert.Equal(2, Track.Clips.Length);
        Assert.False(_vm.Arrangement.HasClipSelection);
    }

    [Fact]
    public void Automation_LanesAreAddedOnTheTrackChannelAndShown()
    {
        var row = _vm.Tracks[0];

        row.AddLaneCommand.Execute(AutomationOption.Volume);
        row.AddLaneCommand.Execute(AutomationOption.Volume);
        row.AddLaneCommand.Execute(AutomationOption.PitchBend);

        Assert.Equal(["Volume (CC 7) · ch 1", "Pitch Bend · ch 1"], row.Lanes.Select(l => l.Name));
        Assert.Equal(2, Track.Automation.Length);
        Assert.True(row.IsAutomationExpanded);
        Assert.Equal(50 + (2 * 36), row.RowHeight);
        Assert.Contains(Track.Id, _vm.ExpandedTracks);
        Assert.Equal("Add Automation Lane", _session.History.UndoLabel);

        row.IsAutomationExpanded = false;
        Assert.Empty(_vm.ExpandedTracks);
        Assert.Equal(50, row.RowHeight);

        row.RemoveLaneCommand.Execute(row.Lanes[0]);
        Assert.Equal(["Pitch Bend · ch 1"], row.Lanes.Select(l => l.Name));
        Assert.Equal("Delete Automation Lane", _session.History.UndoLabel);
    }

    [Fact]
    public void Automation_PointsAreSetAsOneStep()
    {
        _vm.Tracks[0].AddLaneCommand.Execute(AutomationOption.Pan);
        var lane = Track.Automation[0];
        AutomationPoint[] points = [new(Tick.Zero, ControlValue.Min), new(new Tick(960), ControlValue.Max, AutomationCurve.Hold)];

        _vm.SetAutomationPoints(Track.Id, lane.Id, "Add Automation Point", points);

        Assert.Equal(points, Track.Automation[0].Points);
        Assert.Equal("Add Automation Point", _session.History.UndoLabel);
        Assert.False(_session.History.Execute(AutomationCommands.SetPoints(Track.Id, lane.Id, "Again", points)));
    }

    [Fact]
    public void Editor_SaysWhenItsControllerLaneIsOverridden()
    {
        Editor.Lane = ControllerLane.Standard.Single(l => l.Controller == 7 && l.Kind == ControllerLaneKind.Controller);
        Assert.False(Editor.IsLaneOverridden);

        _vm.Tracks[0].AddLaneCommand.Execute(AutomationOption.Volume);
        Assert.False(Editor.IsLaneOverridden);

        _vm.SetAutomationPoints(Track.Id, Track.Automation[0].Id, "Add Automation Point", [new(Tick.Zero, ControlValue.Max)]);
        Assert.True(Editor.IsLaneOverridden);

        Editor.Lane = ControllerLane.Standard.Single(l => l.Kind == ControllerLaneKind.PitchBend);
        Assert.False(Editor.IsLaneOverridden);
    }

    [Fact]
    public void Editor_ComparesOverriddenLanesOnTheRouteChannel()
    {
        // The route sends everything on channel 2, so a lane on channel 2 replaces the clip's channel 1 CC 7.
        _session.Execute(ProjectCommands.SetTrackOutput(Track.Id, TrackOutputs.Read(_session.Project, Track.Id) with { Channel = MidiChannel.FromNumber(2) }));
        _session.Execute(AutomationCommands.AddLane(Track.Id, new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForController(MidiChannel.FromNumber(2), ControllerNumber.ChannelVolume),
            [new AutomationPoint(Tick.Zero, ControlValue.Max)])));

        Editor.Lane = ControllerLane.Standard.Single(l => l.Controller == 7 && l.Kind == ControllerLaneKind.Controller);

        Assert.True(Editor.IsLaneOverridden);
    }

    [Fact]
    public void Editor_OverriddenFollowsTheLaneEventsChannels()
    {
        // The track's first channel is 1, but its CC 7 data is on channel 2, where the lane is.
        _session.Execute(ProjectCommands.AddEvent(Track.Id, Track.Clips[0].Id, new ControllerEvent(new Tick(10), MidiChannel.FromNumber(2), ControllerNumber.ChannelVolume, ControlValue.Max)));
        _session.Execute(AutomationCommands.AddLane(Track.Id, new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForController(MidiChannel.FromNumber(2), ControllerNumber.ChannelVolume),
            [new AutomationPoint(Tick.Zero, ControlValue.Max)])));

        Editor.Lane = ControllerLane.Standard.Single(l => l.Controller == 7 && l.Kind == ControllerLaneKind.Controller);

        Assert.True(Editor.IsLaneOverridden);
    }

    [Fact]
    public void AutomationOptions_ShowTheirControllerInMenus()
    {
        Assert.Equal("Volume (CC 7)", AutomationOption.Volume.ToString());
        Assert.Equal("Pitch Bend", AutomationOption.PitchBend.ToString());
    }

    [Fact]
    public void Arrangement_RenamesClipsAndFallsBackToTheTrackName()
    {
        var clip = Track.Clips[0].Id;
        Assert.Equal("Piano", _vm.Arrangement.DisplayName(clip));

        // Leaving the track's name as it was shown keeps the clip following the track.
        _vm.Arrangement.RenameClip(clip, "Piano");
        Assert.Empty(Track.Clips[0].Name);

        _vm.Arrangement.RenameClip(clip, "Intro");
        Assert.Equal(("Intro", "Intro"), (Track.Clips[0].Name, _vm.Arrangement.DisplayName(clip)));
        Assert.Equal("Rename Clip", _session.History.UndoLabel);

        _vm.Arrangement.RenameClip(clip, "");
        Assert.Equal("Piano", _vm.Arrangement.DisplayName(clip));
        Assert.Null(_vm.Arrangement.DisplayName(ClipId.New()));

        // A track name with stray spaces, confirmed unchanged, still leaves the clip following it.
        _session.Execute(ProjectCommands.RenameTrack(Track.Id, "Piano "));
        _vm.Arrangement.RenameClip(clip, "Piano ");
        Assert.Empty(Track.Clips[0].Name);
    }

    [Fact]
    public void Arrangement_SelectingAnotherTrack_DropsClipsFromTheSelection()
    {
        _session.Execute(ProjectCommands.AddTrack(Track.Create("Other")));
        _vm.Arrangement.SelectClip(Track.Clips[0].Id);

        _vm.Select(_vm.Tracks[1]);

        Assert.False(_vm.Arrangement.HasClipSelection);
    }

    [Fact]
    public void Arrangement_CreatesAnEmptyBarAndUndoDropsItFromTheSelection()
    {
        var id = _vm.Arrangement.CreateClip(Track.Id, 5000);

        var clip = Assert.IsType<NoteClip>(Track.FindClip(id!.Value));
        Assert.Equal((3840L, 7680L), (clip.Start.Value, clip.End.Value));
        Assert.Equal([id.Value], _vm.Arrangement.SelectedClips);
        Assert.Null(_vm.Arrangement.CreateClip(Track.Id, 5000));

        _vm.UndoCommand.Execute(null);
        Assert.Empty(_vm.Arrangement.SelectedClips);
    }

    [Fact]
    public void TempoAndMeter_AreEditableFromTheTransport()
    {
        _vm.CommitTempo("96.5");
        _vm.CommitMeter("3/4");
        _vm.CommitTempo("fast");
        _vm.OnFrame();

        Assert.Equal(96.5, _session.Project.Sequence.TempoMap.TempoAt(Tick.Zero).BeatsPerMinute, 2);
        Assert.Equal(new TimeSignature(3, 4), _session.Project.Sequence.MeterMap.SignatureAt(Tick.Zero));
        Assert.Equal("3/4", _vm.MeterText);
    }

    [Fact]
    public void ShowPane_TogglesTheLowerPane()
    {
        _vm.ShowPaneCommand.Execute(LowerPane.PianoRoll);
        Assert.Equal((LowerPane.PianoRoll, true), (_vm.LowerPane, _vm.IsEditorVisible));
        Assert.False(_vm.EventList.IsActive);

        _vm.ShowPaneCommand.Execute(LowerPane.PianoRoll);
        Assert.False(_vm.IsEditorVisible);

        _vm.ShowPaneCommand.Execute(LowerPane.EventList);
        Assert.True(_vm.EventList.IsActive);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
