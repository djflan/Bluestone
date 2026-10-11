using Avalonia;
using Avalonia.Input;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The piano roll in the real window, opened by double-clicking a clip: notes drawn with the pencil, selected by
/// clicking and by dragging a marquee, and moved by dragging and with the arrow keys, end up in the project.
/// </summary>
public sealed class PianoRollTests
{
    private const long Beat = 960;

    private static ProjectSession OneClip()
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(Beat), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100))])));
        Assert.Equal(Beat, session.Project.Sequence.Ppqn.TicksPerQuarterNote);
        return session;
    }

    /// <summary>Opens the clip in the piano roll the way a musician does: a double-click on it in the arrangement.</summary>
    private static PianoRollView OpenClip(AppUnderTest app)
    {
        var timeline = app.Find<TimelineView>("Timeline");
        app.DoubleClickAt(timeline, new Point(timeline.TickToX(Beat / 2), timeline.LaneHeight * 0.6));
        var roll = app.Find<PianoRollView>("PianoRoll");
        Assert.Equal(LowerPane.PianoRoll, app.ViewModel.LowerPane);
        Assert.True(roll.IsFocused);
        app.Render();
        return roll;
    }

    /// <summary>The middle of the key row for <paramref name="pitch"/> at <paramref name="tick"/>, in the piano roll.</summary>
    private static Point At(AppUnderTest app, PianoRollView roll, long tick, int pitch)
    {
        var editor = app.ViewModel.Editor;
        var point = new Point(
            PianoRollView.KeyboardWidth + TimeGrid.TickToX(tick, app.Session.Project.Sequence, editor.Zoom) - roll.ScrollX,
            PianoRollView.RulerHeight + ((127 - pitch) * editor.KeyHeight) - roll.ScrollY + (editor.KeyHeight / 2));
        Assert.True(point.Y > PianoRollView.RulerHeight && point.Y < PianoRollView.RulerHeight + roll.GridHeight, $"Pitch {pitch} is not in view.");
        Assert.True(point.X > PianoRollView.KeyboardWidth && point.X < roll.Bounds.Width, $"Tick {tick} is not in view.");
        return point;
    }

    private static List<(long Tick, int Pitch)> Notes(AppUnderTest app) =>
        [.. app.Session.Project.Sequence.Tracks[0].Clips.OfType<NoteClip>().Single().TimelineContent().OfType<NoteEvent>().Select(n => (n.Position.Value, (int)n.Note.Value)).Order()];

    [Fact]
    public Task ThePencil_DrawsANoteOnTheGrid_WhereItIsClicked() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(OneClip());
        var roll = OpenClip(app);

        app.Press(Key.D2);
        Assert.Equal(EditTool.Pencil, app.ViewModel.Editor.Tool);
        app.ClickAt(roll, At(app, roll, (2 * Beat) + 100, 64));

        Assert.Equal([(0L, 60), (2 * Beat, 64)], Notes(app));
        app.SaveFrame("piano-roll-drawn");

        app.Press(Key.D1);
        Assert.Equal(EditTool.Arrow, app.ViewModel.Editor.Tool);
    });

    [Fact]
    public Task Notes_AreSelectedByClicking_ShiftClicking_AndDraggingAMarquee() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(OneClip());
        var roll = OpenClip(app);
        var editor = app.ViewModel.Editor;
        editor.AddNote(2 * Beat, 64, Beat);
        app.Settle();
        editor.SelectNone();
        var (low, high) = (editor.Events.OfType<NoteEvent>().Single(n => n.Note.Value == 60).Id, editor.Events.OfType<NoteEvent>().Single(n => n.Note.Value == 64).Id);

        app.ClickAt(roll, At(app, roll, Beat / 2, 60));
        Assert.Equal([low], editor.SelectedEvents);

        app.ClickAt(roll, At(app, roll, (2 * Beat) + (Beat / 2), 64), RawInputModifiers.Shift);
        Assert.Equal(new HashSet<EventId> { low, high }, editor.SelectedEvents.ToHashSet());

        app.Press(Key.Escape);
        Assert.Empty(editor.SelectedEvents);

        app.Drag(roll, At(app, roll, 4 * Beat, 58), At(app, roll, Beat / 4, 66));
        Assert.Equal(new HashSet<EventId> { low, high }, editor.SelectedEvents.ToHashSet());
        app.SaveFrame("piano-roll-selected");
    });

    [Fact]
    public Task DraggingANote_MovesItInTimeAndPitch_AndTheArrowKeysNudgeAndTransposeIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(OneClip());
        var roll = OpenClip(app);

        app.Drag(roll, At(app, roll, Beat / 2, 60), At(app, roll, Beat + (Beat / 2), 62));

        Assert.Equal([(Beat, 62)], Notes(app));

        app.Press(Key.Right);
        app.Press(Key.Up);

        Assert.Equal([(Beat + (Beat / 4), 63)], Notes(app));

        app.Press(Key.Up, RawInputModifiers.Shift);

        Assert.Equal([(Beat + (Beat / 4), 75)], Notes(app));

        app.Press(Key.Delete);

        Assert.Empty(Notes(app));
        Assert.True(roll.IsFocused);
    });
}
