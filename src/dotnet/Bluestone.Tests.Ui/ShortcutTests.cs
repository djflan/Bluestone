using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Playback;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Keyboard shortcuts typed into the real window (Help ▸ Keyboard Shortcuts lists them). The window binds the command-key
/// shortcuts itself unless the macOS menu bar carries them; the last test checks that menu's items.
/// </summary>
public sealed class ShortcutTests
{
    private const long Beat = 960;

    private static ProjectSession LeadAndBass()
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(Beat), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100))])));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Bass", [new NoteEvent(Tick.Zero, new TickSpan(Beat), MidiChannel.FromNumber(2), new NoteNumber(36), new Velocity(100))])));
        Assert.Equal(Beat, session.Project.Sequence.Ppqn.TicksPerQuarterNote);
        return session;
    }

    private static string[] TrackNames(AppUnderTest app) => [.. app.Session.Project.Sequence.Tracks.Select(t => t.Name)];

    [Fact]
    public Task CommandT_AddsATrack_CommandZ_UndoesIt_AndShiftCommandZ_RedoesIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());

        app.Press(Key.T, AppUnderTest.Command);
        Assert.Equal(3, TrackNames(app).Length);

        app.Press(Key.Z, AppUnderTest.Command);
        Assert.Equal(["Lead", "Bass"], TrackNames(app));

        app.Press(Key.Z, AppUnderTest.Command | RawInputModifiers.Shift);
        Assert.Equal(3, TrackNames(app).Length);
    });

    [Fact]
    public Task Space_PlaysAndStops_AndTheTransportKeysMoveThePlayhead() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());

        app.Press(Key.OemPeriod);
        Assert.Equal(4 * Beat, app.ViewModel.PlayheadTick);
        app.Press(Key.Home);
        Assert.Equal(0, app.ViewModel.PlayheadTick);

        // The window under test has no playback thread: the test pumps the engine where that thread would.
        var engine = app.Playback.Engine;
        bool Playing()
        {
            engine.Pump();
            return engine.State == TransportState.Playing;
        }

        app.Press(Key.Space);
        await AppUnderTest.WaitUntilAsync(Playing, "playback to start");
        app.ViewModel.OnFrame();
        Assert.True(app.ViewModel.IsPlaying);

        app.Press(Key.Space);
        engine.Pump();
        Assert.Equal(TransportState.Stopped, engine.State);
    });

    [Fact]
    public Task MAndS_MuteAndSoloTheSelectedTrack_ButTypeIntoAFocusedField() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        TrackInspectorTests.SelectTrack(app, 1);

        app.Press(Key.M);
        app.Press(Key.S);

        var bass = app.Session.Project.Sequence.Tracks[1];
        Assert.True(bass.IsMuted);
        Assert.True(bass.IsSoloed);
        Assert.False(app.Session.Project.Sequence.Tracks[0].IsMuted);

        var tempo = app.Find<TextBox>("TempoBox");
        app.Click(tempo);
        app.Press(Key.M);

        Assert.True(app.Session.Project.Sequence.Tracks[1].IsMuted);
    });

    [Fact]
    public Task Delete_RemovesTheSelectedClip_WhenTheArrangementHasFocus_OtherwiseTheSelectedTracks() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        var timeline = app.Find<TimelineView>("Timeline");
        app.ClickAt(timeline, new Point(timeline.TickToX(Beat / 2), timeline.LaneHeight * 0.6));
        Assert.True(timeline.IsFocused);

        app.Press(Key.Back);

        Assert.Empty(app.Session.Project.Sequence.Tracks[0].Clips);
        Assert.Equal(["Lead", "Bass"], TrackNames(app));

        TrackInspectorTests.SelectTrack(app, 0);
        app.Press(Key.Delete);

        Assert.Equal(["Bass"], TrackNames(app));
    });

    [Fact]
    public Task InThePianoRoll_CopyAndPaste_PutTheSelectedNotesAtThePlayhead() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        var timeline = app.Find<TimelineView>("Timeline");
        app.DoubleClickAt(timeline, new Point(timeline.TickToX(Beat / 2), timeline.LaneHeight * 0.6));
        var roll = app.Find<PianoRollView>("PianoRoll");
        Assert.True(roll.IsFocused);

        app.Press(Key.A, AppUnderTest.Command);
        Assert.Single(app.ViewModel.Editor.SelectedEvents);
        app.Press(Key.C, AppUnderTest.Command);
        app.Press(Key.OemPeriod);
        app.Press(Key.V, AppUnderTest.Command);

        var notes = app.Session.Project.Sequence.Tracks[0].Clips.OfType<NoteClip>().SelectMany(c => c.TimelineContent()).OfType<NoteEvent>().Select(n => n.Position.Value).Order();
        Assert.Equal([0, 4 * Beat], notes);
        Assert.True(roll.IsFocused);
    });

    [Fact]
    public Task TheMacOSMenuBar_CarriesTheCommandKeyShortcuts_AndTheirCommands() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass(), nativeMenuBar: true);
        var menu = NativeMenu.GetMenu(app.Window)!;
        NativeMenuItem Item(string submenu, string header) =>
            menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == submenu).Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);
        var command = (KeyModifiers)AppUnderTest.Command;

        Assert.Equal(new KeyGesture(Key.Z, command), Item("Edit", "Undo").Gesture);
        Assert.Equal(new KeyGesture(Key.Z, command | KeyModifiers.Shift), Item("Edit", "Redo").Gesture);
        Assert.Equal(new KeyGesture(Key.T, command), Item("Track", "New Track").Gesture);
        Assert.Same(app.ViewModel.UndoCommand, Item("Edit", "Undo").Command);

        Item("Track", "New Track").Command!.Execute(null);
        Assert.Equal(3, TrackNames(app).Length);
        Item("Edit", "Undo").Command!.Execute(null);
        Assert.Equal(["Lead", "Bass"], TrackNames(app));
    });
}
