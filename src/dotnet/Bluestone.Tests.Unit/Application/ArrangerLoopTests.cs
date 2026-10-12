using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Application.Editing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using Bluestone.Presentation.Timeline;

namespace Bluestone.Tests.Unit.Application;

/// <summary>The arranger loop: a range kept apart from whether it loops, its commands, its file format, and loop-strip drags.</summary>
public sealed class ArrangerLoopTests
{
    private static readonly long Bar = 4L * Project.CreateNew().Sequence.Ppqn.TicksPerQuarterNote;

    private static readonly TickRange TwoBars = new(new Tick(Bar), new Tick(3 * Bar));

    [Fact]
    public void AnEnabledLoop_NeedsARange()
    {
        Assert.Throws<ArgumentException>(() => new ArrangerLoop(null, true));
        Assert.Throws<InvalidOperationException>(() => ArrangerLoop.None.WithEnabled(true));
        Assert.Null(ArrangerLoop.None.Active);
    }

    [Fact]
    public void MovingALoop_StopsAtTickZero_AndAtTheLastTick()
    {
        var atEnd = new ArrangerLoop(new TickRange(new Tick(long.MaxValue - Bar), new Tick(long.MaxValue)), false);
        Assert.Same(atEnd, atEnd.Moved(Bar));

        var nearEnd = new ArrangerLoop(new TickRange(new Tick(long.MaxValue - (2 * Bar)), new Tick(long.MaxValue - 10)), false);
        Assert.Equal(new Tick(long.MaxValue), nearEnd.Moved(long.MaxValue).Range!.End);
        Assert.Equal(Tick.Zero, new ArrangerLoop(TwoBars, false).Moved(-10 * Bar).Range!.Start);
    }

    [Fact]
    public void TurningLoopingOff_KeepsTheRange_AndTurningItOnAgainReusesIt()
    {
        var history = new EditHistory(Project.CreateNew());
        history.Execute(LoopCommands.LoopRegion(TwoBars));

        history.Execute(LoopCommands.SetEnabled(false, Tick.Zero));
        Assert.False(history.Current.Loop.IsEnabled);
        Assert.Null(history.Current.Loop.Active);
        Assert.Equal(TwoBars, history.Current.Loop.Range);

        history.Execute(LoopCommands.SetEnabled(true, new Tick(40 * Bar)));
        Assert.Equal(TwoBars, history.Current.Loop.Active);
    }

    [Fact]
    public void TurningLoopingOn_WithoutARange_LoopsFourBarsFromThePlayheadsBar()
    {
        var history = new EditHistory(Project.CreateNew());

        history.Execute(LoopCommands.Toggle(history.Current, new Tick((2 * Bar) + 700)));

        Assert.Equal(new TickRange(new Tick(2 * Bar), new Tick(6 * Bar)), history.Current.Loop.Active);
    }

    [Fact]
    public void TheDefaultLoop_FollowsAnOddMeter()
    {
        var project = Project.CreateNew();
        var sequence = project.Sequence;
        project = project with { Sequence = sequence.WithMeterMap(MeterMap.Constant(sequence.Ppqn, new TimeSignature(7, 8))) };

        // A 7/8 bar is 3.5 quarter notes.
        var seven = Bar * 7 / 8;
        var range = LoopCommands.DefaultRange(project, new Tick(seven + 20));

        Assert.Equal(new TickRange(new Tick(seven), new Tick(seven * 5)), range);
    }

    [Fact]
    public void NudgingAndMoving_KeepTheLength_AndStopAtTheStart()
    {
        var history = new EditHistory(Project.CreateNew());
        history.Execute(LoopCommands.SetRange(TwoBars));

        history.Execute(LoopCommands.Nudge(480));
        Assert.Equal(new TickRange(new Tick(Bar + 480), new Tick((3 * Bar) + 480)), history.Current.Loop.Range);

        history.Execute(LoopCommands.Nudge(-10 * Bar));
        Assert.Equal(new TickRange(Tick.Zero, new Tick(2 * Bar)), history.Current.Loop.Range);
        Assert.False(history.Current.Loop.IsEnabled);
        Assert.False(history.Execute(LoopCommands.Nudge(-1)));
    }

    [Fact]
    public void ExactStartAndLength_RefuseNonsense()
    {
        var history = new EditHistory(Project.CreateNew());

        Assert.True(history.Execute(LoopCommands.SetStartAndLength(new Tick(Bar), new TickSpan(Bar))));
        Assert.False(history.Execute(LoopCommands.SetStartAndLength(new Tick(Bar), TickSpan.Zero)));
        Assert.Equal(new TickRange(new Tick(Bar), new Tick(2 * Bar)), history.Current.Loop.Range);
    }

    [Fact]
    public void ADisabledLoop_IsSavedWithItsRange_AndReadBack()
    {
        var project = Project.CreateNew() with { Loop = new ArrangerLoop(TwoBars, false) };

        var saved = ProjectSerializer.Default.Serialize(new ProjectDocument(project));
        var json = JsonNode.Parse(saved)!;
        var restored = ProjectSerializer.Default.Deserialize(saved).Project;

        Assert.Equal(ProjectSerializer.CurrentFormatVersion, (int)json["formatVersion"]!);
        Assert.False((bool)json["project"]!["loop"]!["enabled"]!);
        Assert.Equal(project.Loop, restored.Loop);
    }

    [Fact]
    public void AFormat4Loop_IsReadAsAnEnabledLoop()
    {
        // Format 4 had a loop only while looping was on, and no "enabled" flag.
        var project = Project.CreateNew() with { Loop = ArrangerLoop.Enabled(TwoBars) };
        var json = JsonNode.Parse(ProjectSerializer.Default.Serialize(new ProjectDocument(project)))!;
        json["formatVersion"] = 4;
        json["project"]!["loop"]!.AsObject().Remove("enabled");

        var restored = ProjectSerializer.Default.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Project;

        Assert.Equal(TwoBars, restored.Loop.Active);
    }

    [Fact]
    public void ALoopThatEndsBeforeItStarts_IsRefusedWhenRead()
    {
        var project = Project.CreateNew() with { Loop = ArrangerLoop.Enabled(TwoBars) };
        var json = JsonNode.Parse(ProjectSerializer.Default.Serialize(new ProjectDocument(project)))!;
        json["project"]!["loop"]!["end"] = 10;

        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Default.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData(99, LoopStripPart.StartEdge)]
    [InlineData(104, LoopStripPart.StartEdge)]
    [InlineData(150, LoopStripPart.Body)]
    [InlineData(197, LoopStripPart.EndEdge)]
    [InlineData(205, LoopStripPart.EndEdge)]
    [InlineData(250, LoopStripPart.Empty)]
    [InlineData(10, LoopStripPart.Empty)]
    public void TheStrip_IsHitTestedIntoEdgesBodyAndEmptySpace(double x, LoopStripPart part) =>
        Assert.Equal(part, LoopStripDrag.HitTest(x, 100, 200, 5));

    [Fact]
    public void ANarrowLoop_KeepsABodyToGrab()
    {
        Assert.Equal(LoopStripPart.Body, LoopStripDrag.HitTest(106, 100, 112, 5));
        Assert.Equal(LoopStripPart.Empty, LoopStripDrag.HitTest(150, null, null, 5));
    }

    [Fact]
    public void DrawingInEmptySpace_MakesASnappedRangeInEitherDirection_AndTurnsLoopingOn()
    {
        static long Snap(long t) => (long)Math.Round(t / 480.0) * 480;
        var drag = new LoopStripDrag(LoopStripPart.Empty, 1000, null);

        Assert.Null(drag.RangeAt(1100, Snap));
        Assert.Equal(new TickRange(new Tick(960), new Tick(1920)), drag.RangeAt(1900, Snap));
        Assert.Equal(new TickRange(Tick.Zero, new Tick(960)), drag.RangeAt(-500, Snap));
        Assert.True(drag.Enables);
        Assert.Equal("Set Loop", drag.Label);
    }

    [Fact]
    public void MovingTheBody_KeepsTheLength_SnapsTheStart_AndStopsAtZero()
    {
        static long Snap(long t) => (long)Math.Round(t / 480.0) * 480;
        var drag = new LoopStripDrag(LoopStripPart.Body, 2 * Bar, TwoBars);

        Assert.Equal(new TickRange(new Tick(Bar + 480), new Tick((3 * Bar) + 480)), drag.RangeAt((2 * Bar) + 400, Snap));
        Assert.Equal(new TickRange(Tick.Zero, new Tick(2 * Bar)), drag.RangeAt(0, Snap));
        Assert.False(drag.Enables);
        Assert.Equal("Move Loop", drag.Label);
    }

    [Fact]
    public void ResizingAnEdge_StopsBeforeTheOtherEdge_AndIgnoresTheGridWhenAsked()
    {
        var start = new LoopStripDrag(LoopStripPart.StartEdge, Bar, TwoBars);
        var end = new LoopStripDrag(LoopStripPart.EndEdge, 3 * Bar, TwoBars);

        Assert.Equal(new TickRange(new Tick((3 * Bar) - 1), new Tick(3 * Bar)), start.RangeAt(9 * Bar, t => t));
        Assert.Equal(new TickRange(new Tick(Bar), new Tick(Bar + 1)), end.RangeAt(0, t => t));
        Assert.Equal(new TickRange(new Tick(Bar), new Tick((3 * Bar) + 7)), end.RangeAt((3 * Bar) + 7, t => t));
        Assert.Equal("Resize Loop", end.Label);
    }
}
