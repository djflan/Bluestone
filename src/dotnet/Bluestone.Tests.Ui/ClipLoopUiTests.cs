using Avalonia;
using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Ui;

/// <summary>Clip loops in the arranger and the inspector: the loop handle, the plain edge, the first repeat marker, and several clips at once.</summary>
public sealed class ClipLoopUiTests
{
    private static ProjectSession Song()
    {
        var session = new ProjectSession();
        var beat = session.Project.Sequence.Ppqn.TicksPerQuarterNote;
        var bar = 4 * beat;
        NoteEvent Note(long at, long length, int note) => new(new Tick(at), new TickSpan(length), MidiChannel.FromNumber(1), new NoteNumber((byte)note), new Velocity(100));

        // A one-bar clip: a pickup note, then a two-beat figure to loop.
        var pickup = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(bar), TickSpan.Zero, new EventList([Note(0, beat, 55), Note(2 * beat, beat / 2, 60), Note(3 * beat, beat / 2, 64), Note((3 * beat) + (beat / 2), beat / 2, 67)]), "Riff");
        var lead = new Track(TrackId.New(), "Lead", [pickup], role: TrackRole.Hybrid);
        var drums = new Track(TrackId.New(), "Drums", [new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(bar), TickSpan.Zero, new EventList([Note(0, beat / 4, 36), Note(beat, beat / 4, 38)]), "Beat")], role: TrackRole.Hybrid);
        var loop = new AudioClip(ClipId.New(), Tick.Zero, new TickSpan(4 * bar), TickSpan.Zero, new AudioSource("drum-loop.wav", 48_000, 96_000, 2), "Break")
        {
            Loop = new ClipLoop(Tick.Zero, new TickSpan(bar)),
        };
        var audio = new Track(TrackId.New(), "Break", [loop], role: TrackRole.Audio);
        session.Execute(ProjectCommands.AddTrack(lead));
        session.Execute(ProjectCommands.AddTrack(drums));
        session.Execute(ProjectCommands.AddTrack(audio));
        return session;
    }

    private static async Task<AppUnderTest> OpenAsync()
    {
        var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Zoom = 30;
        app.Settle();
        return app;
    }

    private static long Bar(AppUnderTest app) => 4L * app.Session.Project.Sequence.Ppqn.TicksPerQuarterNote;

    private static Clip ClipOf(AppUnderTest app, int track) => app.Session.Project.Sequence.Tracks[track].Clips[0];

    // The clip body's top, from its name strip (which sits 2 px into the lane).
    private static double BodyTop(TimelineView timeline, Clip clip) => timeline.ClipHeaderBounds(clip.Id)!.Value.Y;

    [Fact]
    public Task DraggingTheLowerRightCorner_LoopsTheClip_InOneUndoStep() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var timeline = app.Find<TimelineView>("Timeline");
        var clip = ClipOf(app, 0);
        var bar = Bar(app);
        var x = timeline.TickToX(clip.End.Value) - 2;
        var y = BodyTop(timeline, clip) + 40;
        app.ViewModel.Arrangement.SelectClip(clip.Id);
        app.Settle();

        app.Drag(timeline, new Point(x, y), new Point(timeline.TickToX(3 * bar) - 2, y));

        var looped = ClipOf(app, 0);
        Assert.True(looped.IsLooping);
        Assert.Equal(new Tick(3 * bar), looped.End);
        Assert.Equal(new ClipLoop(Tick.Zero, new TickSpan(bar)), looped.Loop);
        Assert.Equal("Extend Clip Loop", app.Session.History.UndoLabel);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Null(ClipOf(app, 0).Loop);
        Assert.Equal(new Tick(bar), ClipOf(app, 0).End);
    });

    [Fact]
    public Task DraggingTheUpperRightEdge_OnlyChangesTheLength() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var timeline = app.Find<TimelineView>("Timeline");
        var clip = ClipOf(app, 0);
        var bar = Bar(app);
        var x = timeline.TickToX(clip.End.Value) - 2;
        var y = BodyTop(timeline, clip) + 16;

        app.Drag(timeline, new Point(x, y), new Point(timeline.TickToX(2 * bar) - 2, y));

        Assert.Null(ClipOf(app, 0).Loop);
        Assert.Equal(new Tick(2 * bar), ClipOf(app, 0).End);
    });

    [Fact]
    public Task DraggingTheFirstRepeatMarker_ChangesTheLoopsLength() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var bar = Bar(app);
        var beat = bar / 4;
        var clip = ClipOf(app, 0);
        app.Session.Execute(ClipLoopCommands.ExtendLooping(app.Session.Project.Sequence.Tracks[0].Id, clip.Id, new Tick(4 * bar)));
        app.Settle();
        var timeline = app.Find<TimelineView>("Timeline");
        var y = BodyTop(timeline, clip) + 30;

        app.Drag(timeline, new Point(timeline.TickToX(bar), y), new Point(timeline.TickToX(bar - beat), y));

        Assert.Equal(new TickSpan(bar - beat), ClipOf(app, 0).Loop!.Length);
        Assert.Equal("Change Clip Loop Length", app.Session.History.UndoLabel);
    });

    [Fact]
    public Task TheInspector_LoopsSeveralClipsAtOnce_AndShowsWhenTheyDiffer() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var lead = ClipOf(app, 0);
        var drums = ClipOf(app, 1);
        app.ViewModel.Arrangement.SelectClip(lead.Id);
        app.ViewModel.Arrangement.SelectClip(drums.Id, Presentation.SelectionMode.Add);
        app.ViewModel.IsInspectorVisible = true;
        app.Settle();
        var section = app.Find<Expander>("ClipLoopSection");
        Assert.True(section.IsEffectivelyVisible);
        Assert.Equal("Loop · 2 clips", app.ViewModel.ClipLoop.Header);
        var undo = app.Session.History.UndoCount;

        app.Click(AppUnderTest.Within<CheckBox>(section, c => c.Name is null && Equals(c.Content, "Loop the clip")));

        Assert.True(ClipOf(app, 0).IsLooping);
        Assert.True(ClipOf(app, 1).IsLooping);
        Assert.Equal(undo + 1, app.Session.History.UndoCount);

        app.Session.Execute(ClipLoopCommands.SetEnabled([(app.Session.Project.Sequence.Tracks[1].Id, drums.Id)], false));
        app.Settle();
        Assert.Null(app.ViewModel.ClipLoop.IsLooping);
        Assert.Equal("Some of these clips loop.", app.ViewModel.ClipLoop.Summary);

        var length = app.Find<TextBox>("ClipLoopLengthBox");
        length.Focus();
        length.Text = "0.2.000";
        app.Find<TextBox>("ClipLoopStartBox").Focus();
        app.Settle();
        Assert.Equal(new TickSpan(Bar(app) / 2), ClipOf(app, 0).Loop!.Length);
        Assert.Equal(new TickSpan(Bar(app) / 2), ClipOf(app, 1).Loop!.Length);
    });

    [Fact]
    public Task LoopingClips_AreRenderedWithTheirRepeats_CloseAndFar() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenAsync();
        var bar = Bar(app);
        var beat = bar / 4;
        var lead = app.Session.Project.Sequence.Tracks[0];
        app.Session.Execute(ClipLoopCommands.SetLoop(lead.Id, lead.Clips[0].Id, new ClipLoop(new Tick(2 * beat), new TickSpan(2 * beat))));
        app.Session.Execute(ClipLoopCommands.ExtendLooping(lead.Id, lead.Clips[0].Id, new Tick(4 * bar)));
        app.ViewModel.Arrangement.SelectClip(lead.Clips[0].Id);
        app.ViewModel.IsInspectorVisible = true;
        app.Settle();
        Assert.Equal("Plays a pickup, then 7 passes.", app.ViewModel.ClipLoop.Summary);
        Assert.True(File.Exists(app.SaveFrame("clip-loop-close")));

        app.ViewModel.Zoom = 8;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("clip-loop-far")));
    });
}
