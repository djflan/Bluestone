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

/// <summary>
/// The indicator colour seams: one interpolator with the saved preference, one gradient per family whose caches empty on its
/// own change only, meters and LEDs that follow the right one, and the MIDI LED's colour.
/// </summary>
public sealed class MeterGradientTests
{
    private static readonly SignalGradient HardEdge = SignalGradient.Create(
        [new("#000000", 0), new("#000000", 0.5), new("#FF0000", 0.5), new("#FF0000", 1)]);

    [Fact]
    public Task Default_IsTheSavedPreferenceDefault_AndAHardEdgeFallsAsTheEditorDraws() => HeadlessApp.RunAsync(() =>
    {
        try
        {
            Assert.Equal(SignalGradient.Default.Stops.Select(s => (s.Position, Color.Parse(s.Colour))), MeterGradient.Mixer.Default);

            // Through the colour overload, as the app passes it: exactly on the edge is the upper colour, as Evaluate has it.
            MeterGradient.Mixer.Use(HardEdge.Stops.Select(s => (s.Position, Color.Parse(s.Colour))));
            Assert.Equal(HardEdge, MeterGradient.Mixer.Gradient);
            foreach (var fraction in new[] { 0, 0.4999, 0.5, 0.5001, 1 })
            {
                var (r, g, b) = HardEdge.Evaluate(fraction);
                Assert.Equal(Color.FromRgb(r, g, b), MeterGradient.Mixer.At(fraction));
            }

            Assert.Equal(Colors.Red, MeterGradient.Mixer.At(0.5));
        }
        finally
        {
            MeterGradient.Mixer.Use((SignalGradient?)null);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task GradientBrushCaches_EmptyWhenTheGradientChanges() => HeadlessApp.RunAsync(() =>
    {
        try
        {
            MeterGradient.Mixer.Use(HardEdge);
            _ = MeterGradient.Mixer.Brush(Colors.Red);
            _ = MeterGradient.Mixer.GlowBrushes(Colors.Red, 1);
            Assert.Same(MeterGradient.Mixer.Brush(Colors.Red), MeterGradient.Mixer.Brush(Colors.Red));
            Assert.True(MeterGradient.Mixer.CachedBrushCount >= 2);

            MeterGradient.Mixer.Use((SignalGradient?)null);
            Assert.Equal(0, MeterGradient.Mixer.CachedBrushCount);
        }
        finally
        {
            MeterGradient.Mixer.Use((SignalGradient?)null);
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

            MeterGradient.Mixer.Use(SignalGradient.Create([new("#FF0000", 0), new("#FF0000", 1)]));
            var after = ReddestPixel(window, ladder);
            Assert.True(after > 180, $"Expected the lit cells to turn red; the reddest pixel was {after}.");
        }
        finally
        {
            MeterGradient.Mixer.Use((SignalGradient?)null);
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static readonly SignalGradient Red = SignalGradient.Create([new("#FF0000", 0), new("#FF0000", 1)]);

    [Fact]
    public Task EditingOneGradient_LeavesTheOthersVersionsAndCachesAlone() => HeadlessApp.RunAsync(() =>
    {
        try
        {
            Assert.Equal(SignalGradient.Default, MeterGradient.Chain.Gradient);
            Assert.Equal(SignalGradient.GainDefault, MeterGradient.Gain.Gradient);
            _ = MeterGradient.Mixer.Brush(Colors.Red);
            _ = MeterGradient.Gain.GlowBrushes(Colors.Red, 1);
            var (mixer, chain, gain) = (MeterGradient.Mixer.Version, MeterGradient.Chain.Version, MeterGradient.Gain.Version);
            var (mixerCached, gainCached) = (MeterGradient.Mixer.CachedBrushCount, MeterGradient.Gain.CachedBrushCount);
            var raised = new List<object?>();
            void Record(object? sender, EventArgs e) => raised.Add(sender);
            MeterGradient.Mixer.Changed += Record;
            MeterGradient.Chain.Changed += Record;
            MeterGradient.Gain.Changed += Record;
            try
            {
                MeterGradient.Chain.Use(HardEdge);
            }
            finally
            {
                MeterGradient.Mixer.Changed -= Record;
                MeterGradient.Chain.Changed -= Record;
                MeterGradient.Gain.Changed -= Record;
            }

            Assert.Equal([MeterGradient.Chain], raised);
            Assert.Equal(chain + 1, MeterGradient.Chain.Version);
            Assert.Equal(mixer, MeterGradient.Mixer.Version);
            Assert.Equal(gain, MeterGradient.Gain.Version);
            Assert.Equal(mixerCached, MeterGradient.Mixer.CachedBrushCount);
            Assert.Equal(gainCached, MeterGradient.Gain.CachedBrushCount);
            Assert.Equal(SignalGradient.Default, MeterGradient.Mixer.Gradient);

            // Null restores each one's own default.
            MeterGradient.Gain.Use(HardEdge);
            MeterGradient.Gain.Use((SignalGradient?)null);
            Assert.Equal(SignalGradient.GainDefault, MeterGradient.Gain.Gradient);
        }
        finally
        {
            MeterGradient.Chain.Use((SignalGradient?)null);
            MeterGradient.Gain.Use((SignalGradient?)null);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AMixerLadder_AndAChainLadder_DrawFromTheirOwnGradients() => HeadlessApp.RunAsync(() =>
    {
        Assert.Equal(MeterGradientTarget.Chain, new ChainJunction().Ladder.GradientTarget);
        Assert.Equal(MeterGradientTarget.Chain, new ChainOutMeter().Ladder.GradientTarget);

        var strip = new LedLadderMeter { IsReady = true, Value = 0, MeterWidth = 14, Height = 200 };
        var chain = new LedLadderMeter { IsReady = true, Value = 0, MeterWidth = 14, Height = 200, GradientTarget = MeterGradientTarget.Chain };
        Assert.Equal(MeterGradientTarget.Mixer, strip.GradientTarget);
        Assert.Null(chain.SubscribedTo);
        var window = new Window
        {
            Width = 120,
            Height = 240,
            Background = Brushes.Black,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16), Spacing = 24, Children = { strip, chain } },
        };
        window.Show();
        try
        {
            Assert.Same(MeterGradient.Mixer, strip.SubscribedTo);
            Assert.Same(MeterGradient.Chain, chain.SubscribedTo);
            Assert.True(ReddestPixel(window, strip) < 100);
            Assert.True(ReddestPixel(window, chain) < 100);

            MeterGradient.Chain.Use(Red);
            Assert.True(ReddestPixel(window, chain) > 180, "The chain ladder follows the chain gradient.");
            Assert.True(ReddestPixel(window, strip) < 100, "The mixer ladder does not.");

            MeterGradient.Mixer.Use(Red);
            MeterGradient.Chain.Use((SignalGradient?)null);
            Assert.True(ReddestPixel(window, strip) > 180);
            Assert.True(ReddestPixel(window, chain) < 100);

            // Changing the target moves the subscription and the colours with it.
            chain.GradientTarget = MeterGradientTarget.Mixer;
            Assert.Same(MeterGradient.Mixer, chain.SubscribedTo);
            Assert.True(ReddestPixel(window, chain) > 180);
        }
        finally
        {
            MeterGradient.Mixer.Use((SignalGradient?)null);
            MeterGradient.Chain.Use((SignalGradient?)null);
            window.Close();
        }

        Assert.Null(strip.SubscribedTo);
        Assert.Null(chain.SubscribedTo);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheGainGutter_SamplesTheGainGradientPerLed_AndRedrawsWhenItChanges() => HeadlessApp.RunAsync(() =>
    {
        var gutter = new GainLedGutter { Value = 6, Height = 260 };
        var window = new Window { Width = 80, Height = 300, Background = Brushes.Black, Content = gutter };
        window.Show();
        try
        {
            Render(window);
            var colors = gutter.Colors;
            Assert.True(colors.Count >= 2);
            Assert.Equal(Color.Parse("#6B4BE0"), colors[0]);
            Assert.Equal(Color.Parse("#FFA3C4"), colors[^1]);
            for (var k = 0; k < colors.Count; k++)
            {
                Assert.Equal(MeterGradient.Gain.At(k / (double)(colors.Count - 1)), colors[k]);
            }

            // The mixer's gradient is not the gain LEDs'.
            MeterGradient.Mixer.Use(HardEdge);
            Render(window);
            Assert.Equal(Color.Parse("#6B4BE0"), gutter.Colors[0]);

            MeterGradient.Gain.Use(Red);
            Render(window);
            Assert.All(gutter.Colors, c => Assert.Equal(Colors.Red, c));
        }
        finally
        {
            MeterGradient.Mixer.Use((SignalGradient?)null);
            MeterGradient.Gain.Use((SignalGradient?)null);
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMidiLed_TakesTheChosenColour_AndTheAppAppliesEverySetting() => HeadlessApp.RunAsync(() =>
    {
        var signal = new JunctionSignal();
        (typeof(JunctionSignal).GetProperty(nameof(JunctionSignal.CanCarryNotes)) ?? throw new InvalidOperationException("CanCarryNotes was not found."))
            .SetValue(signal, true);
        (typeof(JunctionSignal).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new InvalidOperationException("Update was not found."))
            .Invoke(signal, [true, null, 1_000_000_000L]);
        Assert.True(signal.NotesHeld);
        var junction = new ChainJunction { Signal = signal, Width = 48, Height = 220 };
        var window = new Window { Width = 48, Height = 220, Background = Brushes.Black, Content = junction };
        window.Show();
        var display = new DisplayViewModel();
        try
        {
            Assert.Equal(Color.Parse(DisplaySettings.DefaultMidiColour), LedColours.Midi);
            Assert.True(junction.LampGlow > 0.5, "Held notes light the MIDI LED.");
            Assert.True(ReddestPixel(window, junction, 0, ChainJunction.IconY + 20) < 60, "The default MIDI colour is the accent, which has no red in it.");

            var raised = 0;
            void Count(object? sender, EventArgs e) => raised++;
            LedColours.Changed += Count;
            try
            {
                LedColours.UseMidi("#FF0000");
                LedColours.UseMidi("#ff0000");
                LedColours.UseMidi("#FF0000");
            }
            finally
            {
                LedColours.Changed -= Count;
            }

            Assert.Equal(1, raised);
            Assert.True(ReddestPixel(window, junction, 0, ChainJunction.IconY + 20) > 120, "The lit MIDI LED and caption redraw in the new colour.");
            LedColours.UseMidi("red");
            Assert.Equal(LedColours.DefaultMidi, LedColours.Midi);

            // The app applies the display settings: each gradient to its own seam, and the MIDI colour.
            Desktop.App.FollowSignalGradient(display);
            display.MidiColour = "#00FF00";
            Assert.Equal(Color.Parse("#00FF00"), LedColours.Midi);
            var mixer = MeterGradient.Mixer.Version;
            display.Chain.SetGradientStopColour(0, "#FF0000");
            Assert.Equal(display.Chain.Gradient, MeterGradient.Chain.Gradient);
            Assert.Equal(mixer, MeterGradient.Mixer.Version);
            display.Gain.SetGradientStopColour(0, "#00FF00");
            Assert.Equal(display.Gain.Gradient, MeterGradient.Gain.Gradient);
            display.Mixer.SetGradientStopColour(0, "#0000FF");
            Assert.Equal(display.Mixer.Gradient, MeterGradient.Mixer.Gradient);
        }
        finally
        {
            display.Mixer.ResetGradientCommand.Execute(null);
            display.Chain.ResetGradientCommand.Execute(null);
            display.Gain.ResetGradientCommand.Execute(null);
            display.MidiColour = DisplaySettings.DefaultMidiColour;
            LedColours.UseMidi(null);
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static void Render(Window window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
    }

    // The most red (red over the brighter of green and blue) in the lower half of the ladder, below the clip lamp.
    private static int ReddestPixel(Window window, Control ladder) => ReddestPixel(window, ladder, ladder.Bounds.Height / 2, ladder.Bounds.Height - 2);

    // The most red (red over the brighter of green and blue) in rows from top to bottom of the control.
    private static int ReddestPixel(Window window, Control control, double top, double bottom)
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
        var origin = control.TranslatePoint(default, window) ?? throw new InvalidOperationException("The control is not in the window.");
        var reddest = int.MinValue;
        for (var y = (int)(origin.Y + top); y < (int)(origin.Y + bottom); y++)
        {
            for (var x = (int)origin.X; x < (int)(origin.X + control.Bounds.Width); x++)
            {
                var offset = (y * stride) + (x * 4);
                reddest = Math.Max(reddest, pixels[offset + red] - Math.Max(pixels[offset + blue], pixels[offset + 1]));
            }
        }

        return reddest;
    }
}
