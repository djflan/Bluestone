using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>A signal indicator's level colours: two to eight sorted stops, evaluated without allocating.</summary>
public sealed class SignalGradientTests
{
    private static SignalGradient Gradient(params (string Colour, double Position)[] stops) =>
        SignalGradient.Create(stops.Select(s => new SignalGradientStop(s.Colour, s.Position)));

    [Fact]
    public void Default_HasTheR4CStops()
    {
        Assert.Equal<SignalGradientStop>(
            [new("#34C6F4", 0), new("#34C6F4", 0.40), new("#3FE0D8", 0.62), new("#7C6BFF", 0.78), new("#B65CFF", 0.86), new("#B65CFF", 1.0)],
            SignalGradient.Default.Stops);
    }

    [Fact]
    public void Evaluate_AtStopsAndMidSegment_InterpolatesInSrgbAndClamps()
    {
        var gradient = Gradient(("#000000", 0), ("#FF8040", 0.5), ("#FFFFFF", 1));

        Assert.Equal(((byte)0, (byte)0, (byte)0), gradient.Evaluate(0));
        Assert.Equal(((byte)0xFF, (byte)0x80, (byte)0x40), gradient.Evaluate(0.5));
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF), gradient.Evaluate(1));
        Assert.Equal(((byte)0x80, (byte)0x40, (byte)0x20), gradient.Evaluate(0.25));
        Assert.Equal(((byte)0xFF, (byte)0xC0, (byte)0xA0), gradient.Evaluate(0.75));
        Assert.Equal(((byte)0, (byte)0, (byte)0), gradient.Evaluate(-3));
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF), gradient.Evaluate(7));
        Assert.Equal(((byte)0, (byte)0, (byte)0), gradient.Evaluate(double.NaN));
    }

    [Fact]
    public void Evaluate_AtAHardEdge_JumpsToTheUpperColour()
    {
        var gradient = Gradient(("#00FF00", 0), ("#00FF00", 0.5), ("#FF0000", 0.5), ("#FF0000", 1));

        Assert.Equal(((byte)0, (byte)0xFF, (byte)0), gradient.Evaluate(0.4999));
        Assert.Equal(((byte)0xFF, (byte)0, (byte)0), gradient.Evaluate(0.5));
        Assert.Equal(((byte)0xFF, (byte)0, (byte)0), gradient.Evaluate(0.75));
    }

    [Fact]
    public void Evaluate_DoesNotAllocate()
    {
        var gradient = SignalGradient.Default;
        gradient.Evaluate(0.5);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i <= 100; i++)
        {
            gradient.Evaluate(i / 100.0);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Create_ClampsAndSortsPositions_AndUpperCasesColours()
    {
        var gradient = Gradient(("#ffffff", 1.5), ("#abcdef", 0.3), ("#000000", -0.2));

        Assert.Equal<SignalGradientStop>([new("#000000", 0), new("#ABCDEF", 0.3), new("#FFFFFF", 1)], gradient.Stops);
    }

    [Fact]
    public void Create_TooFewOrTooManyStopsOrABadColour_Throws()
    {
        Assert.Throws<ArgumentException>(() => Gradient(("#000000", 0)));
        Assert.Throws<ArgumentException>(() => Gradient([.. Enumerable.Range(0, 9).Select(i => ("#000000", i / 8.0))]));
        Assert.Throws<ArgumentException>(() => Gradient(("#000000", 0), ("red", 1)));
        Assert.Throws<ArgumentException>(() => Gradient(("#000000", 0), ("#12345G", 1)));
        Assert.Throws<ArgumentException>(() => Gradient(("#000000", 0), ("#FFF", 1)));
        Assert.Throws<ArgumentException>(() => Gradient(("#000000", 0), ("#FFFFFF", double.NaN)));
        Assert.False(SignalGradient.TryCreate(null, out _));
        Assert.Equal(8, Gradient([.. Enumerable.Range(0, 8).Select(i => ("#000000", i / 7.0))]).Stops.Length);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(Gradient(("#000000", 0), ("#FFFFFF", 1)), Gradient(("#000000", 0), ("#ffffff", 1)));
        Assert.Equal(Gradient(("#000000", 0), ("#FFFFFF", 1)).GetHashCode(), Gradient(("#000000", 0), ("#ffffff", 1)).GetHashCode());
        Assert.True(Gradient(("#000000", 0), ("#FFFFFF", 1)) == Gradient(("#000000", 0), ("#FFFFFF", 1)));
        Assert.NotEqual(Gradient(("#000000", 0), ("#FFFFFF", 1)), Gradient(("#000000", 0), ("#FFFFFF", 0.9)));
    }
}
