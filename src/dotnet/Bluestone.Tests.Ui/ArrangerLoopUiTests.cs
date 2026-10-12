using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Ui;

/// <summary>The arranger loop in the ruler's loop strip and the transport: drawn, moved, resized, nudged, typed, and never deleted by a click.</summary>
public sealed class ArrangerLoopUiTests
{
    private const double StripY = 5;

    private static ProjectSession Song()
    {
        var session = new ProjectSession();
        var beat = session.Project.Sequence.Ppqn.TicksPerQuarterNote;
        NoteEvent Note(int i) => new(new Tick(i * beat), new TickSpan(beat), MidiChannel.FromNumber(1), new NoteNumber((byte)(60 + (i % 5))), new Velocity(100));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [.. Enumerable.Range(0, 32).Select(Note)])));
        return session;
    }

    private static long Bar(AppUnderTest app) => 4L * app.Session.Project.Sequence.Ppqn.TicksPerQuarterNote;

    private static double X(AppUnderTest app, RulerView ruler, long tick) =>
        (tick * ruler.PixelsPerQuarter / app.Session.Project.Sequence.Ppqn.TicksPerQuarterNote) - ruler.ScrollX;

    [Fact]
    public Task DrawingInTheStrip_LoopsTheRange_InOneUndoStep() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 20;
        app.Settle();
        var ruler = app.Find<RulerView>("Ruler");
        var bar = Bar(app);

        app.Drag(ruler, new Point(X(app, ruler, bar) + 1, StripY), new Point(X(app, ruler, 3 * bar) - 1, StripY));

        Assert.Equal(new TickRange(new Tick(bar), new Tick(3 * bar)), app.Session.Project.Loop.Active);
        Assert.True(app.ViewModel.Loop.IsEnabled);
        Assert.Equal("Set Loop", app.Session.History.UndoLabel);
        Assert.Equal("2.0.000", app.ViewModel.Loop.LengthText);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(ArrangerLoop.None, app.Session.Project.Loop);
    });

    [Fact]
    public Task AClick_SelectsTheLoopWithoutDeletingIt_AndTheArrowsNudgeIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 20;
        app.Settle();
        var bar = Bar(app);
        var range = new TickRange(new Tick(bar), new Tick(3 * bar));
        app.Session.Execute(LoopCommands.LoopRegion(range));
        app.Settle();
        var ruler = app.Find<RulerView>("Ruler");

        app.ClickAt(ruler, new Point(X(app, ruler, 2 * bar), StripY));

        Assert.Equal(range, app.Session.Project.Loop.Active);
        Assert.True(ruler.IsLoopSelected);

        app.Press(Key.Right, RawInputModifiers.Shift);
        Assert.Equal(new TickRange(new Tick(2 * bar), new Tick(4 * bar)), app.Session.Project.Loop.Active);

        app.Press(Key.Left);
        var nudged = app.Session.Project.Loop.Active!;
        Assert.True(nudged.Start.Value < 2 * bar && nudged.Start.Value >= bar, $"One grid step left of bar 3: {nudged.Start}");
        Assert.Equal(2 * bar, nudged.Length.Value);

        app.Press(Key.Escape);
        Assert.False(ruler.IsLoopSelected);
    });

    [Fact]
    public Task DraggingTheBodyMovesIt_AndAnEdgeResizesIt_WithoutTurningLoopingOn() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 20;
        app.Settle();
        var bar = Bar(app);
        app.Session.Execute(LoopCommands.SetRange(new TickRange(new Tick(bar), new Tick(3 * bar))));
        app.Settle();
        var ruler = app.Find<RulerView>("Ruler");
        var undoBefore = app.Session.History.UndoCount;

        app.Drag(ruler, new Point(X(app, ruler, 2 * bar), StripY), new Point(X(app, ruler, 4 * bar), StripY));
        Assert.Equal(new TickRange(new Tick(3 * bar), new Tick(5 * bar)), app.Session.Project.Loop.Range);
        Assert.False(app.Session.Project.Loop.IsEnabled);
        Assert.Equal(undoBefore + 1, app.Session.History.UndoCount);

        app.Window.MouseMove(app.InWindow(ruler, new Point(X(app, ruler, 5 * bar), StripY)));
        app.Settle();
        Assert.Equal(Bluestone.Presentation.Timeline.LoopStripPart.EndEdge, ruler.HoverPart);
        app.Drag(ruler, new Point(X(app, ruler, 5 * bar), StripY), new Point(X(app, ruler, 7 * bar), StripY));
        Assert.Equal(new TickRange(new Tick(3 * bar), new Tick(7 * bar)), app.Session.Project.Loop.Range);
        Assert.False(app.Session.Project.Loop.IsEnabled);
    });

    [Fact]
    public Task DoubleClickingTheLoop_TurnsLoopingOnAndOff_KeepingTheRange() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 20;
        app.Settle();
        var bar = Bar(app);
        var range = new TickRange(new Tick(bar), new Tick(3 * bar));
        app.Session.Execute(LoopCommands.LoopRegion(range));
        app.Settle();
        var ruler = app.Find<RulerView>("Ruler");

        app.DoubleClickAt(ruler, new Point(X(app, ruler, 2 * bar), StripY));
        Assert.False(app.Session.Project.Loop.IsEnabled);
        Assert.Equal(range, app.Session.Project.Loop.Range);

        app.Click(app.Find<Avalonia.Controls.Primitives.ToggleButton>("LoopButton"));
        Assert.Equal(range, app.Session.Project.Loop.Active);
    });

    [Fact]
    public Task TheTransportFields_SetTheStartAndLengthExactly() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 20;
        app.Settle();
        var bar = Bar(app);
        app.Session.Execute(LoopCommands.LoopRegion(new TickRange(new Tick(bar), new Tick(3 * bar))));
        app.Settle();
        var start = app.Find<Avalonia.Controls.TextBox>("LoopStartBox");
        var length = app.Find<Avalonia.Controls.TextBox>("LoopLengthBox");

        start.Focus();
        start.Text = "5.1.000";
        length.Focus();
        app.Settle();
        Assert.Equal(new TickRange(new Tick(4 * bar), new Tick(6 * bar)), app.Session.Project.Loop.Range);

        length.Text = "1.2.000";
        start.Focus();
        app.Settle();
        Assert.Equal((bar * 3 / 2), app.Session.Project.Loop.Range!.Length.Value);

        length.Focus();
        length.Text = "nonsense";
        start.Focus();
        app.Settle();
        Assert.Equal((bar * 3 / 2), app.Session.Project.Loop.Range!.Length.Value);
        Assert.Equal(app.ViewModel.Loop.LengthText, length.Text);
    });

    [Fact]
    public Task LoopSelectedRegion_LoopsTheSelectedClips() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 20;
        app.Settle();
        var clip = app.Session.Project.Sequence.Tracks[0].Clips[0];
        app.ViewModel.Arrangement.SelectClip(clip.Id);
        app.Settle();

        Assert.True(app.ViewModel.Loop.LoopSelectionCommand.CanExecute(null));
        app.ViewModel.Loop.LoopSelectionCommand.Execute(null);
        app.Settle();

        Assert.Equal(new TickRange(clip.Start, clip.End), app.Session.Project.Loop.Active);
        Assert.Equal("Loop Selected Region", app.Session.History.UndoLabel);
    });

    [Fact]
    public Task ActiveAndInactiveLoops_AndADragInProgress_AreRendered() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        var bar = Bar(app);
        app.Session.Execute(LoopCommands.LoopRegion(new TickRange(new Tick(bar), new Tick(3 * bar))));
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("loop-active")));

        app.Session.Execute(LoopCommands.SetEnabled(false, Tick.Zero));
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("loop-inactive")));

        // Hold a move half way, to see the start and length read out while dragging.
        var ruler = app.Find<RulerView>("Ruler");
        var from = app.InWindow(ruler, new Point(X(app, ruler, 2 * bar), StripY));
        var to = app.InWindow(ruler, new Point(X(app, ruler, (3 * bar) + (bar / 2)), StripY));
        app.Window.MouseMove(from);
        app.Window.MouseDown(from, MouseButton.Left);
        app.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("loop-moving")));
        app.Window.MouseUp(to, MouseButton.Left);
        app.Settle();
    });
}
