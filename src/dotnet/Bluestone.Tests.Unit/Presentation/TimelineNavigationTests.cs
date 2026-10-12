using Bluestone.Presentation.Timeline;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>Timeline navigation: anchored zoom, limits, bad input, fitting, and the ruler's click-or-drag gesture.</summary>
public sealed class TimelineNavigationTests
{
    private static readonly TimelineZoomLimits Limits = new(2, 160);

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(700)]
    public void Zooming_KeepsTheMusicalPositionUnderTheAnchor(double anchor)
    {
        var view = new TimelineViewport(40, 1000);
        var before = view.QuartersAt(anchor);

        var zoomed = view.ZoomedAround(anchor, 1.7, Limits);

        Assert.Equal(68, zoomed.PixelsPerQuarter, 6);
        Assert.Equal(before, zoomed.QuartersAt(anchor), 6);
    }

    [Fact]
    public void ZoomStaysWithinItsLimits_AndNeverScrollsBeforeTheStart()
    {
        var view = new TimelineViewport(40, 10);

        Assert.Equal(160, view.ZoomedAround(0, 1000, Limits).PixelsPerQuarter);
        Assert.Equal(2, view.ZoomedAround(0, 0.0001, Limits).PixelsPerQuarter);
        Assert.Equal(0, view.ZoomedAround(500, 0.1, Limits).ScrollX);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void ABadFactor_ChangesNothing(double factor)
    {
        var view = new TimelineViewport(40, 100);

        Assert.Equal(view, view.ZoomedAround(50, factor, Limits));
        Assert.Equal(view, view.ZoomedAround(double.NaN, 2, Limits));
    }

    [Fact]
    public void Fitting_ShowsTheRangeWithALittleRoom_AndIgnoresEmptyRangesAndUnmeasuredViews()
    {
        var view = new TimelineViewport(40, 0);

        var fitted = view.Fitted(16, 48, 1000, Limits);

        Assert.True(fitted.QuartersAt(0) < 16 && fitted.QuartersAt(0) > 14);
        Assert.True(fitted.QuartersAt(1000) > 48 && fitted.QuartersAt(1000) < 50);
        Assert.Equal(view, view.Fitted(16, 16, 1000, Limits));
        Assert.Equal(view, view.Fitted(16, 48, 0, Limits));
        Assert.Equal(2, view.Fitted(0, 100_000, 1000, Limits).PixelsPerQuarter);
    }

    [Fact]
    public void ASmallMovement_IsStillAClick()
    {
        var gesture = new RulerNavigationGesture(100, 10, new TimelineViewport(40, 0), Limits);

        Assert.Null(gesture.Move(102, 12));
        Assert.False(gesture.IsNavigating);
        Assert.NotNull(gesture.Move(100, 10 + RulerNavigationGesture.Threshold));
        Assert.True(gesture.IsNavigating);
    }

    [Fact]
    public void DraggingUp_ZoomsIn_DraggingDown_ZoomsOut_AroundThePressedPosition()
    {
        var view = new TimelineViewport(40, 4000);
        var gesture = new RulerNavigationGesture(300, 10, view, Limits);
        var pressed = view.QuartersAt(300);

        var up = gesture.Move(300, -90)!.Value;
        var down = gesture.Move(300, 110)!.Value;

        Assert.True(up.PixelsPerQuarter > 40);
        Assert.Equal(40 * Math.Exp(96 * RulerNavigationGesture.ZoomPerPixel), up.PixelsPerQuarter, 6);
        Assert.Equal(pressed, up.QuartersAt(300), 6);
        Assert.True(down.PixelsPerQuarter < 40);
        Assert.Equal(pressed, down.QuartersAt(300), 6);
    }

    [Fact]
    public void DraggingSideways_Scrolls_WithThePressedPositionFollowingThePointer_AndNoZoom()
    {
        var view = new TimelineViewport(40, 400);
        var gesture = new RulerNavigationGesture(300, 10, view, Limits);
        var pressed = view.QuartersAt(300);

        var panned = gesture.Move(200, 12)!.Value;

        Assert.Equal(40, panned.PixelsPerQuarter);
        // 100 pixels left, less the 4-pixel dead zone: the pressed position is now 96 pixels further left.
        Assert.Equal(pressed, panned.QuartersAt(300 - 96), 6);
        Assert.Equal(496, panned.ScrollX, 6);
    }

    [Fact]
    public void PanningLeftOfTheStart_StopsAtTheStart()
    {
        var gesture = new RulerNavigationGesture(300, 10, new TimelineViewport(40, 50), Limits);

        Assert.Equal(0, gesture.Move(900, 10)!.Value.ScrollX);
    }
}
