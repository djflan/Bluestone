using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>Live MIDI colors must not remain in the drawing caches shared for the life of the app.</summary>
public sealed class MidiColourCacheTests
{
    private const long Timestamp = 1_000_000_000;
    private static readonly double[] FadeFrames = [10, 18, 28, 40, 55, 70, 90, 110, 140];

    [Fact]
    public Task TheChainPreview_ChangingMidiColours_DoesNotGrowTheSharedCaches() => HeadlessApp.RunAsync(() =>
    {
        var original = Hex(LedColours.Midi);
        var reduceMotion = LedMotion.ReduceMotion;
        LedMotion.ReduceMotion = true;
        var preview = new IndicatorPreview { Family = MeterGradientTarget.Chain, Count = 4, Width = 120, Height = 220 };
        var window = Show(preview);
        try
        {
            Assert.Equal(4, preview.GetVisualDescendants().OfType<ChainSeamPreview>().Count());
            LedColours.UseMidi("#122334");
            Render(window);
            var before = SharedCacheSizes();

            for (var i = 0; i < 48; i++)
            {
                LedColours.UseMidi(Colour(i));
                Render(window);
            }

            Assert.Equal(before, SharedCacheSizes());
        }
        finally
        {
            window.Close();
            LedColours.UseMidi(original);
            LedMotion.ReduceMotion = reduceMotion;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AnActiveAndFadingJunction_ChangingMidiColours_DoesNotGrowTheSharedCaches() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var original = Hex(LedColours.Midi);
        var (signal, update) = NoteSignal();
        var junction = new ChainJunction { Signal = signal, Width = 48, Height = 220 };
        var window = Show(junction);
        try
        {
            LedColours.UseMidi("#122334");
            Fade(clock, window, junction, update, null);
            var before = SharedCacheSizes();
            var visited = new HashSet<int>();
            for (var i = 0; i < 32; i++)
            {
                LedColours.UseMidi(Colour(i));
                Fade(clock, window, junction, update, visited);
            }

            Assert.Equal(Enumerable.Range(0, LedLevels.Steps + 1), visited.OrderBy(step => step));
            Assert.Equal(before, SharedCacheSizes());
        }
        finally
        {
            window.Close();
            LedColours.UseMidi(original);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ADetachedJunction_ChangingMidiColour_RebuildsItsAppearanceOnReattach() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var original = Hex(LedColours.Midi);
        var (signal, update) = NoteSignal();
        update(true, null, Timestamp);
        var junction = new ChainJunction { Signal = signal, Width = 48, Height = 220 };
        var window = Show(junction);
        try
        {
            LedColours.UseMidi("#2364D8");
            var blue = Pixels(window);
            LedColours.UseMidi("#E32110");
            Assert.NotEqual(blue, Pixels(window));

            window.Content = null;
            window.UpdateLayout();
            LedColours.UseMidi("#2364D8");
            window.Content = junction;

            Assert.Equal(blue, Pixels(window));
        }
        finally
        {
            window.Close();
            LedColours.UseMidi(original);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task WithReducedMotion_InvalidMidiColourRestoresTheDefault_AndTheLampStillSettles() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var original = Hex(LedColours.Midi);
        var (signal, update) = NoteSignal();
        var junction = new ChainJunction { Signal = signal, Width = 48, Height = 220 };
        var window = Show(junction);
        try
        {
            LedMotion.ReduceMotion = true;
            update(true, null, Timestamp);
            LedColours.UseMidi(null);
            var defaultLit = Pixels(window);
            LedColours.UseMidi("#FF0000");
            var redLit = Pixels(window);
            Assert.NotEqual(defaultLit, redLit);

            update(false, null, Timestamp);
            Assert.Equal(0, junction.LampGlow);
            Assert.Equal(0, clock.Pending);
            Assert.NotEqual(redLit, Pixels(window));

            LedColours.UseMidi("not a color");
            Assert.Equal(LedColours.DefaultMidi, LedColours.Midi);
            update(true, null, Timestamp);
            Assert.Equal(1, junction.LampGlow);
            Assert.Equal(defaultLit, Pixels(window));
        }
        finally
        {
            window.Close();
            LedColours.UseMidi(original);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task MidiPaint_ReusesWarmSteps_AndForgetsThemWhenTheColourChanges() => HeadlessApp.RunAsync(() =>
    {
        var original = Hex(LedColours.Midi);
        try
        {
            LedColours.UseMidi("#123456");
            var pens = new IPen[LedLevels.Steps];
            for (var step = 1; step <= LedLevels.Steps; step++)
            {
                var pen = LedColours.MidiPen(step);
                var ink = ConsolePaint.Mix(ConsolePaint.Alpha(Palette.TextMuted, 0.75), ConsolePaint.Lighter(LedColours.Midi, 0.1), step / (double)LedLevels.Steps);
                Assert.Equal(ink, Assert.IsAssignableFrom<ISolidColorBrush>(pen.Brush).Color);
                Assert.Equal(1.3, pen.Thickness);
                Assert.Equal(PenLineCap.Round, pen.LineCap);
                pens[step - 1] = pen;
            }

            var caption = LedColours.MidiCaptionBrush;
            Assert.Equal(ConsolePaint.Alpha(LedColours.Midi, 0.85), caption.Color);
            LedColours.UseMidi("#123456");
            Assert.Same(pens[0], LedColours.MidiPen(1));
            Assert.Same(caption, LedColours.MidiCaptionBrush);

            var allocated = ChainMeterCostTests.InSteadyState(() =>
            {
                for (var step = 1; step <= LedLevels.Steps; step++)
                {
                    _ = LedColours.MidiPen(step);
                }

                _ = LedColours.MidiCaptionBrush;
            }, 1000);
            Assert.Equal(0, allocated);

            LedColours.UseMidi("#ABCDEF");
            LedColours.UseMidi("#123456");
            Assert.NotSame(pens[0], LedColours.MidiPen(1));
            Assert.NotSame(caption, LedColours.MidiCaptionBrush);
        }
        finally
        {
            LedColours.UseMidi(original);
        }

        return Task.CompletedTask;
    });

    private static void Fade(ManualLedClock clock, Window window, ChainJunction junction, Action<bool, MeterReading?, long> update, HashSet<int>? visited)
    {
        clock.Time += 1000;
        var start = clock.Time;
        update(true, null, Timestamp);
        Assert.Equal(1, junction.LampGlow);
        visited?.Add(LedLevels.Steps);
        Render(window);
        update(false, null, Timestamp);
        foreach (var offset in FadeFrames)
        {
            clock.Frame(start + offset);
            visited?.Add((int)Math.Round(junction.LampGlow * LedLevels.Steps, MidpointRounding.AwayFromZero));
            Render(window);
        }

        Assert.Equal(0, junction.LampGlow);
        Assert.Equal(0, clock.Pending);
    }

    private static (JunctionSignal Signal, Action<bool, MeterReading?, long> Update) NoteSignal()
    {
        var signal = new JunctionSignal();
        (typeof(JunctionSignal).GetProperty(nameof(JunctionSignal.CanCarryNotes)) ?? throw new InvalidOperationException("CanCarryNotes was not found."))
            .SetValue(signal, true);
        var update = (typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Update was not found."))
            .CreateDelegate<Action<bool, MeterReading?, long>>(signal);
        return (signal, update);
    }

    private static Window Show(Control control)
    {
        var window = new Window { Width = 160, Height = 240, Background = Brushes.Black, Content = control };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    }

    private static byte[] Pixels(Window window)
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

        return pixels;
    }

    private static string Colour(int i) => $"#{((i * 47) + 17) % 256:X2}{((i * 79) + 37) % 256:X2}{((i * 127) + 11) % 256:X2}";

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string SharedCacheSizes() => string.Join(", ", new[]
    {
        (typeof(Glow), "Brushes"),
        (typeof(Glow), "Pens"),
        (typeof(ConsolePaint), "SolidBrushes"),
        (typeof(ConsolePaint), "CachedPens"),
        (typeof(ConsolePaint), "Verticals"),
    }.Select(cache =>
    {
        var field = cache.Item1.GetField(cache.Item2, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{cache.Item1.Name}.{cache.Item2} was not found.");
        return $"{cache.Item1.Name}.{cache.Item2}={((ICollection)field.GetValue(null)!).Count}";
    }));
}
