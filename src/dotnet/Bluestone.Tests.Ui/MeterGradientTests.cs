using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>The meter colour seam: one interpolator with the saved preference, caches that empty on change, and meters that follow it.</summary>
public sealed class MeterGradientTests
{
    private static readonly SignalGradient HardEdge = SignalGradient.Create(
        [new("#000000", 0), new("#000000", 0.5), new("#FF0000", 0.5), new("#FF0000", 1)]);

    [Fact]
    public Task Default_IsTheSavedPreferenceDefault_AndAHardEdgeFallsAsTheEditorDraws() => HeadlessApp.RunAsync(() =>
    {
        try
        {
            Assert.Equal(SignalGradient.Default.Stops.Select(s => (s.Position, Color.Parse(s.Colour))), MeterGradient.Default);

            // Through the colour overload, as the app passes it: exactly on the edge is the upper colour, as Evaluate has it.
            MeterGradient.Use(HardEdge.Stops.Select(s => (s.Position, Color.Parse(s.Colour))));
            Assert.Equal(HardEdge, MeterGradient.Gradient);
            foreach (var fraction in new[] { 0, 0.4999, 0.5, 0.5001, 1 })
            {
                var (r, g, b) = HardEdge.Evaluate(fraction);
                Assert.Equal(Color.FromRgb(r, g, b), MeterGradient.At(fraction));
            }

            Assert.Equal(Colors.Red, MeterGradient.At(0.5));
        }
        finally
        {
            MeterGradient.Use((SignalGradient?)null);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task GradientBrushCaches_EmptyWhenTheGradientChanges() => HeadlessApp.RunAsync(() =>
    {
        try
        {
            MeterGradient.Use(HardEdge);
            _ = MeterGradient.Brush(Colors.Red);
            _ = MeterGradient.GlowBrushes(Colors.Red, 1);
            Assert.Same(MeterGradient.Brush(Colors.Red), MeterGradient.Brush(Colors.Red));
            Assert.True(MeterGradient.CachedBrushCount >= 2);

            MeterGradient.Use((SignalGradient?)null);
            Assert.Equal(0, MeterGradient.CachedBrushCount);
        }
        finally
        {
            MeterGradient.Use((SignalGradient?)null);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AnAttachedLadder_RedrawsInTheNewColours_WhenTheGradientChanges() => HeadlessApp.RunAsync(() =>
    {
        var ladder = new LedLadderMeter { IsReady = true, Value = 0, MeterWidth = 14, Height = 200 };
        var window = new Window
        {
            Width = 80,
            Height = 240,
            Background = Brushes.Black,
            Content = new Border { Padding = new Thickness(16), Child = ladder, HorizontalAlignment = HorizontalAlignment.Left },
        };
        window.Show();
        try
        {
            var before = ReddestPixel(window, ladder);
            Assert.True(ladder.Cells(0, double.NaN, ladder.CellCount).TopLit > ladder.CellCount / 2, "The lower half of the ladder should be lit.");
            Assert.True(before < 100, $"The default gradient has no red in it, but the ladder's reddest pixel was {before}.");

            MeterGradient.Use(SignalGradient.Create([new("#FF0000", 0), new("#FF0000", 1)]));
            var after = ReddestPixel(window, ladder);
            Assert.True(after > 180, $"Expected the lit cells to turn red; the reddest pixel was {after}.");
        }
        finally
        {
            MeterGradient.Use((SignalGradient?)null);
            window.Close();
        }

        return Task.CompletedTask;
    });

    // The most red (red over the brighter of green and blue) in the lower half of the ladder, below the clip lamp.
    private static int ReddestPixel(Window window, Control ladder)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var (red, blue) = frame.Format == Avalonia.Platform.PixelFormats.Bgra8888 ? (2, 0) : (0, 2);
        var origin = ladder.TranslatePoint(default, window) ?? throw new InvalidOperationException("The ladder is not in the window.");
        var reddest = int.MinValue;
        for (var y = (int)(origin.Y + (ladder.Bounds.Height / 2)); y < (int)(origin.Y + ladder.Bounds.Height) - 2; y++)
        {
            for (var x = (int)origin.X; x < (int)(origin.X + ladder.Bounds.Width); x++)
            {
                var offset = (y * stride) + (x * 4);
                reddest = Math.Max(reddest, pixels[offset + red] - Math.Max(pixels[offset + blue], pixels[offset + 1]));
            }
        }

        return reddest;
    }
}
