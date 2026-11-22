using Avalonia;
using Bluestone.Desktop.Controls;

namespace Bluestone.Tests.Ui;

/// <summary>The knob's value arc starts at the range's zero point, wherever it sits.</summary>
public sealed class KnobArcTests
{
    [Fact]
    public Task AsymmetricBipolarRange_DrawsNoArcAtZero_AndAShortArcNearIt() => HeadlessApp.RunAsync(() =>
    {
        // −60 to +12 dB, as on the Audio Receiver's gain: 0 dB sits at 60/72 of the travel.
        var knob = new Knob { Minimum = -60, Maximum = 12, Value = 0 };
        var (fraction, origin) = knob.Positions();
        Assert.Equal(60.0 / 72, origin, 6);

        var cache = new ArcKnobCache();
        var centre = new Point(20, 20);
        Assert.Null(cache.Geometries(centre, 16, fraction, origin).Value);

        var near = cache.Geometries(centre, 16, fraction - 0.05, origin).Value;
        var fromStart = new ArcKnobCache().Geometries(centre, 16, fraction - 0.05, 0).Value;
        Assert.NotNull(near);
        Assert.NotNull(fromStart);
        Assert.True(near!.Bounds.Width * near.Bounds.Height < fromStart!.Bounds.Width * fromStart.Bounds.Height / 4);
        return Task.CompletedTask;
    });
}
