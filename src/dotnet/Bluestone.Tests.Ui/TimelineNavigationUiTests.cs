using Avalonia;
using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Ui;

/// <summary>Zooming and scrolling time from the rulers: click versus drag, anchoring, independence, the loop strip, and the commands.</summary>
public sealed class TimelineNavigationUiTests
{
    private const long Beat = 960;

    private static ProjectSession Song()
    {
        var session = new ProjectSession();
        NoteEvent Note(int i) => new(new Tick(i * Beat), new TickSpan(Beat), MidiChannel.FromNumber(1), new NoteNumber((byte)(60 + (i % 7))), new Velocity(100));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [.. Enumerable.Range(0, 64).Select(Note)])));
        return session;
    }

    private static async Task<AppUnderTest> OpenAsync()
    {
        var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.Settle();
        return app;
    }

    private static void Near(long expected, long actual, long tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} ± {tolerance}, got {actual}.");

    // The beat ruler's lower part (below the loop strip).
    private const double BeatRulerY = 22;

    [Fact]
    public Task AClickInTheBeatRuler_Seeks_AndADragUpZoomsInAroundThePressedPosition() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var ruler = app.Find<RulerView>("Ruler");
        var scroller = app.Find<ScrollViewer>("TimelineScroller");
        var timeline = app.Find<TimelineView>("Timeline");

        app.ClickAt(ruler, new Point(200, BeatRulerY));
        Near(timeline.XToTick(200), app.ViewModel.PlayheadTick, 30);
        var zoom = app.ViewModel.Zoom;
        var pressed = timeline.XToTick(scroller.Offset.X + 300);

        app.Drag(ruler, new Point(300, BeatRulerY), new Point(300, BeatRulerY - 80));

        Assert.True(app.ViewModel.Zoom > zoom * 1.8, $"Zoom {app.ViewModel.Zoom} after starting at {zoom}");
        Near(pressed, timeline.XToTick(scroller.Offset.X + 300), 40);
        Assert.True(app.ViewModel.PlayheadTick > 0, "The drag must not move the playhead back.");
    });

    [Fact]
    public Task ADragSidewaysScrolls_WithoutZooming_OrMovingThePlayhead() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        app.ViewModel.Zoom = 80;
        app.Settle();
        var ruler = app.Find<RulerView>("Ruler");
        var scroller = app.Find<ScrollViewer>("TimelineScroller");
        var playhead = app.ViewModel.PlayheadTick;

        app.Drag(ruler, new Point(500, BeatRulerY), new Point(200, BeatRulerY + 1));

        Assert.Equal(80, app.ViewModel.Zoom);
        Assert.Equal(296, scroller.Offset.X, 1);
        Assert.Equal(ruler.ScrollX, scroller.Offset.X, 1);
        Assert.Equal(playhead, app.ViewModel.PlayheadTick);
    });

    [Fact]
    public Task TheLoopStrip_StillEditsTheLoop_AndDoesNotNavigate() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var ruler = app.Find<RulerView>("Ruler");
        var zoom = app.ViewModel.Zoom;

        app.Drag(ruler, new Point(100, 5), new Point(260, 5 - 40));

        Assert.Equal(zoom, app.ViewModel.Zoom);
        Assert.NotNull(app.Session.Project.Loop.Active);
    });

    [Fact]
    public Task ThePianoRollsRuler_ZoomsThePianoRoll_AndNotTheArrangement() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var timeline = app.Find<TimelineView>("Timeline");
        app.DoubleClickAt(timeline, new Point(timeline.TickToX(Beat / 2), timeline.LaneHeight * 0.6));
        var roll = app.Find<PianoRollView>("PianoRoll");
        app.Render();
        var arrangerZoom = app.ViewModel.Zoom;
        var editorZoom = app.ViewModel.Editor.Zoom;

        app.Drag(roll, new Point(PianoRollView.KeyboardWidth + 200, 14), new Point(PianoRollView.KeyboardWidth + 200, 14 - 60));

        Assert.True(app.ViewModel.Editor.Zoom > editorZoom * 1.5);
        Assert.Equal(arrangerZoom, app.ViewModel.Zoom);

        var playhead = app.ViewModel.PlayheadTick;
        app.ClickAt(roll, new Point(PianoRollView.KeyboardWidth + 50, 14));
        Assert.NotEqual(playhead, app.ViewModel.PlayheadTick);
        Assert.True(app.ViewModel.Editor.Zoom > editorZoom * 1.5);
    });

    [Fact]
    public Task ZoomToLoop_WithThePianoRollFocused_ShowsTheLoopThere() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var timeline = app.Find<TimelineView>("Timeline");
        app.DoubleClickAt(timeline, new Point(timeline.TickToX(Beat / 2), timeline.LaneHeight * 0.6));
        var roll = app.Find<PianoRollView>("PianoRoll");
        app.Session.Execute(LoopCommands.LoopRegion(new TickRange(new Tick(8 * Beat), new Tick(12 * Beat))));
        roll.Focus();
        app.Settle();
        var arrangerZoom = app.ViewModel.Zoom;

        app.ViewModel.ZoomToLoopCommand.Execute(null);
        app.Settle();

        var view = roll.Viewport;
        var (from, to) = ((8 * view.PixelsPerQuarter) - view.ScrollX, (12 * view.PixelsPerQuarter) - view.ScrollX);
        Assert.True(from >= 0 && to <= roll.GridWidth, $"The loop should be in view: {from}..{to} of {roll.GridWidth}");
        Assert.True(to - from > roll.GridWidth * 0.7, "The loop should fill the piano roll.");
        Assert.Equal(arrangerZoom, app.ViewModel.Zoom);
    });

    [Fact]
    public Task TheZoomCommands_FitTheProject_TheSelection_AndTheLoop_AndTheSliderIsGone() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var scroller = app.Find<ScrollViewer>("TimelineScroller");
        var timeline = app.Find<TimelineView>("Timeline");
        Assert.Throws<InvalidOperationException>(() => app.Find<Slider>("ZoomSlider"));

        app.ViewModel.ZoomToFitCommand.Execute(null);
        app.Settle();
        var end = app.Session.Project.Sequence.EndPosition.Value;
        Assert.True(timeline.TickToX(end) <= scroller.Offset.X + scroller.Viewport.Width, "The project's end should be in view.");
        Assert.True(timeline.TickToX(end) > scroller.Offset.X + (scroller.Viewport.Width * 0.85), "The project should fill the view.");

        app.Session.Execute(LoopCommands.LoopRegion(new TickRange(new Tick(32 * Beat), new Tick(36 * Beat))));
        app.Settle();
        app.ViewModel.ZoomToLoopCommand.Execute(null);
        app.Settle();
        Assert.True(timeline.TickToX(32 * Beat) >= scroller.Offset.X && timeline.TickToX(36 * Beat) <= scroller.Offset.X + scroller.Viewport.Width);
        Assert.True(timeline.TickToX(4 * Beat) > scroller.Viewport.Width * 0.85);
        Assert.True(File.Exists(app.SaveFrame("zoom-close")));

        app.ViewModel.ZoomToFitCommand.Execute(null);
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("zoom-far")));
    });
}
