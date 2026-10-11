using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The arrangement in the real window: clips dragged, resized, and renamed with the mouse and keyboard end up as
/// changes in the project, and each is one undo step.
/// </summary>
public sealed class ArrangementTests
{
    private const long Bar = 4 * 960;

    /// <summary>"Lead" with a one-bar clip at the start, and an empty "Bass" below it.</summary>
    private static ProjectSession LeadAndBass()
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(Bar), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100))])));
        session.Execute(ProjectCommands.AddTrack(Track.Create("Bass", TrackRole.Instrument)));
        Assert.Equal(960, session.Project.Sequence.Ppqn.TicksPerQuarterNote);
        return session;
    }

    private static Clip LeadClip(AppUnderTest app) => app.Session.Project.Sequence.Tracks[0].Clips.Single();

    /// <summary>A point in a clip's body (below its name strip) on lane <paramref name="lane"/>, at <paramref name="tick"/>.</summary>
    private static Point Body(TimelineView timeline, long tick, int lane = 0) => new(timeline.TickToX(tick), (lane * timeline.LaneHeight) + (timeline.LaneHeight * 0.6));

    [Fact]
    public Task DraggingAClip_MovesItLater_AndOntoAnotherTrack_AsOneUndoStepEach() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        var timeline = app.Find<TimelineView>("Timeline");
        var clip = LeadClip(app).Id;

        app.Drag(timeline, Body(timeline, Bar / 2), Body(timeline, Bar + (Bar / 2)));

        Assert.Equal(Bar, LeadClip(app).Start.Value);
        Assert.Equal(clip, LeadClip(app).Id);
        Assert.Contains(clip, app.ViewModel.Arrangement.SelectedClips);

        app.Drag(timeline, Body(timeline, Bar + (Bar / 2)), Body(timeline, Bar + (Bar / 2), lane: 1));

        var tracks = app.Session.Project.Sequence.Tracks;
        Assert.Empty(tracks[0].Clips);
        Assert.Equal(Bar, tracks[1].Clips.Single().Start.Value);
        app.SaveFrame("arrangement-dragged");

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();

        Assert.Equal(Bar, LeadClip(app).Start.Value);
    });

    [Fact]
    public Task DraggingAClipsEnd_ResizesIt_AndDraggingItsStart_TrimsIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        var timeline = app.Find<TimelineView>("Timeline");

        app.Drag(timeline, Body(timeline, Bar) - new Point(2, 0), Body(timeline, 2 * Bar));

        Assert.Equal(0, LeadClip(app).Start.Value);
        Assert.Equal(2 * Bar, LeadClip(app).End.Value);

        app.Drag(timeline, Body(timeline, 0) + new Point(3, 0), Body(timeline, Bar / 2));

        Assert.Equal(Bar / 2, LeadClip(app).Start.Value);
        Assert.Equal(2 * Bar, LeadClip(app).End.Value);
    });

    [Fact]
    public Task DoubleClickingAClipsName_RenamesIt_WithTheKeyboard() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        var timeline = app.Find<TimelineView>("Timeline");
        var nameBox = app.Find<TextBox>("ClipNameBox");

        app.DoubleClickAt(timeline, new Point(timeline.TickToX(Bar / 2), 8));

        Assert.True(nameBox.IsVisible);
        Assert.True(nameBox.IsFocused);
        Assert.Equal("Lead", nameBox.Text);
        app.Type("Riff");
        app.SaveFrame("clip-rename");
        app.Press(Key.Enter);

        Assert.False(nameBox.IsVisible);
        Assert.Equal("Riff", LeadClip(app).Name);
        Assert.Equal("Riff", app.ViewModel.Arrangement.DisplayName(LeadClip(app).Id));
        Assert.True(timeline.IsFocused);
    });

    [Fact]
    public Task ReturnOnASelectedClip_RenamesIt_AndEscapeCancels() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(LeadAndBass());
        var timeline = app.Find<TimelineView>("Timeline");
        var nameBox = app.Find<TextBox>("ClipNameBox");
        app.ClickAt(timeline, Body(timeline, Bar / 2));

        app.Press(Key.Enter);
        app.Type("Hook");
        app.Press(Key.Escape);

        Assert.False(nameBox.IsVisible);
        Assert.Equal(string.Empty, LeadClip(app).Name);

        app.Press(Key.Enter);
        app.Type("Hook");
        app.Press(Key.Enter);

        Assert.Equal("Hook", LeadClip(app).Name);
    });
}
