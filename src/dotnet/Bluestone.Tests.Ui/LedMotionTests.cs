using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// LED motion (<see cref="LedMotion"/>, <see cref="LedLevels"/>) on a hand-run clock: each family's fade and warm-up at a
/// known time, no warm-up where there should be none, instant changes with a switch off or under Reduce motion, a clock
/// that stops once everything has settled, and keys whose end states draw exactly as without motion.
/// </summary>
public sealed class LedMotionTests
{
    [Fact]
    public Task MeterCells_FadeEasedAndWarmUpLinearly_TheHeldPeakAndClipLampOnlyFade() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var ladder = new LedLadderMeter { Mode = MixerMeterMode.PeakRms, IsReady = true, Width = 14, Height = 220 };
        var window = Show(ladder);
        try
        {
            var (cells, held, lamps) = ladder.LeftLeds;

            ladder.Show(-6, -6, double.NaN, double.NaN);
            Render(window);
            clock.Frame(500);
            var loud = ladder.Cells(-6, double.NaN, cells.Count).TopLit;
            var quiet = ladder.Cells(-30, double.NaN, cells.Count).TopLit;
            Assert.True(loud > quiet + 2, $"Cells {loud} and {quiet}.");
            Assert.Equal(LedLevels.Steps, cells.Step(loud));
            Assert.Equal(0, clock.Pending);

            // Falling: each cell that goes dark fades, eased: half the time leaves a quarter of the light.
            clock.Time = 1000;
            ladder.Show(-30, -30, double.NaN, double.NaN);
            Render(window);
            Assert.Equal(1, cells.Level(loud));
            Assert.Equal(1, clock.Pending);
            clock.Frame(1000 + (LedLadderMeter.CellFadeMs / 2));
            Assert.Equal(0.25, cells.Level(loud), 6);
            Assert.Equal(1, cells.Level(quiet));
            clock.Frame(1000 + LedLadderMeter.CellFadeMs);
            Assert.Equal(0, cells.Level(loud));

            // Rising: a dark cell warms up in a straight line.
            clock.Time = 2000;
            ladder.Show(-6, -6, double.NaN, double.NaN);
            Render(window);
            Assert.Equal(0, cells.Level(loud));
            clock.Frame(2000 + (LedLadderMeter.CellWarmupMs / 2));
            Assert.Equal(0.5, cells.Level(loud), 6);
            clock.Frame(2000 + LedLadderMeter.CellWarmupMs);
            Assert.Equal(1, cells.Level(loud));

            // The held peak lights at once and fades.
            clock.Time = 3000;
            ladder.Show(-30, -30, -6, -6);
            Render(window);
            var peak = ladder.Cells(-30, -6, cells.Count).Held;
            Assert.True(peak > quiet, $"Held cell {peak}.");
            Assert.Equal(LedLevels.Steps, held.Step(peak));
            clock.Frame(3000 + LedLadderMeter.CellFadeMs);
            clock.Time = 4000;
            ladder.Show(-30, -30, double.NaN, double.NaN);
            Render(window);
            clock.Frame(4000 + (LedLadderMeter.CellFadeMs / 2));
            Assert.Equal(0.25, held.Level(peak), 6);

            // The clip lamp lights at once and fades over its own, longer time.
            clock.Time = 5000;
            ladder.IsClipped = true;
            Render(window);
            Assert.Equal(LedLevels.Steps, lamps.Step(0));
            Assert.Equal(LedLevels.Steps, lamps.Step(1));
            clock.Frame(5100);
            clock.Time = 6000;
            ladder.IsClipped = false;
            Render(window);
            clock.Frame(6000 + (LedLadderMeter.LampFadeMs / 2));
            Assert.Equal(0.25, lamps.Level(0), 6);
            clock.Frame(6000 + LedLadderMeter.LampFadeMs);
            Assert.Equal(0, lamps.Level(0));

            // Settled: the clock has stopped.
            Assert.Equal((0, false), LedMotion.StateOf(window));
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task GainLeds_FadeAndWarmUp() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var gutter = new GainLedGutter { Value = 0, Width = 14, Height = 220 };
        var window = Show(gutter);
        try
        {
            Render(window);
            var leds = gutter.Leds;
            Assert.Equal(LedLevels.Steps, leds.Step(0));

            clock.Time = 1000;
            gutter.Value = double.NegativeInfinity;
            Render(window);
            clock.Frame(1000 + (GainLedGutter.FadeMs / 2));
            Assert.Equal(0.25, leds.Level(0), 6);
            clock.Frame(1000 + GainLedGutter.FadeMs);
            Assert.Equal(0, leds.Level(0));

            clock.Time = 2000;
            gutter.Value = 0;
            Render(window);
            clock.Frame(2000 + (GainLedGutter.WarmupMs / 2));
            Assert.Equal(0.5, leds.Level(0), 6);
            clock.Frame(2000 + GainLedGutter.WarmupMs);
            Assert.Equal(1, leds.Level(0));
            Assert.Equal((0, false), LedMotion.StateOf(window));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMidiLed_LightsAtOnce_AndFadesOverItsRelease() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var signal = new JunctionSignal();
        (typeof(JunctionSignal).GetProperty(nameof(JunctionSignal.CanCarryNotes)) ?? throw new InvalidOperationException("CanCarryNotes was not found."))
            .SetValue(signal, true);
        var update = typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Update was not found.");
        var junction = new ChainJunction { Signal = signal, Width = 48, Height = 220 };
        var window = Show(junction);
        try
        {
            var release = junction.LampRelease.TotalMilliseconds;
            Assert.Equal(140, release);
            clock.Time = 1000;
            update.Invoke(signal, [true, null, 1_000_000_000L]);
            Assert.Equal(1, junction.LampGlow);
            Assert.Equal(0, clock.Pending);

            update.Invoke(signal, [false, null, 1_000_000_000L]);
            Assert.Equal(1, junction.LampGlow);
            clock.Frame(1000 + (release / 2));
            Assert.Equal(0.25, junction.LampGlow, 6);
            clock.Frame(1000 + release);
            Assert.Equal(0, junction.LampGlow);
            Assert.Equal((0, false), LedMotion.StateOf(window));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    public static TheoryData<string> Keys => ["SquareKey", "TextKey", "PowerGlyph"];

    [Theory]
    [MemberData(nameof(Keys))]
    public Task Keys_LightAtOnce_FadeOut_AndSettleOnTheirExactEndStates(string kind) => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var (key, lamp, fadeMs) = Key(kind);
        var window = Show(key);
        try
        {
            // Without motion: the end states as they were drawn before.
            LedMotion.ReduceMotion = true;
            var dark = Pixels(window);
            key.IsChecked = true;
            var lit = Pixels(window);
            key.IsChecked = false;
            Assert.NotEqual(dark, lit);
            Assert.Equal(dark, Pixels(window));

            LedMotion.ReduceMotion = false;
            clock.Time = 1000;
            key.IsChecked = true;
            Assert.Equal(LedLevels.Steps, lamp.Step(0));
            Assert.Equal(0, clock.Pending);
            Assert.Equal(lit, Pixels(window));

            key.IsChecked = false;
            clock.Frame(1000 + (fadeMs / 2));
            Assert.Equal(0.25, lamp.Level(0), 6);
            var fading = Pixels(window);
            Assert.NotEqual(dark, fading);
            Assert.NotEqual(lit, fading);

            clock.Frame(1000 + fadeMs);
            Assert.Equal(0, lamp.Level(0));
            Assert.Equal(dark, Pixels(window));
            Assert.Equal((0, false), LedMotion.StateOf(window));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task WithFadeOrWarmupOff_OrUnderReduceMotion_ChangesAreInstant() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var ladder = new LedLadderMeter { Mode = MixerMeterMode.PeakRms, IsReady = true, Width = 14, Height = 220 };
        var key = new SquareKey { Glyph = "M" };
        var window = Show(new StackPanel { Children = { ladder, key } });
        try
        {
            var cells = ladder.LeftLeds.Cells;
            ladder.Show(-30, -30, double.NaN, double.NaN);
            Render(window);
            var top = ladder.Cells(-6, double.NaN, cells.Count).TopLit;

            void Rise(double at)
            {
                clock.Time = at;
                ladder.Show(-6, -6, double.NaN, double.NaN);
                Render(window);
            }

            void Fall(double at)
            {
                clock.Time = at;
                ladder.Show(-30, -30, double.NaN, double.NaN);
                Render(window);
            }

            // Warm-up off: cells light at once; the fade still runs.
            LedMotion.Warmup = false;
            Rise(1000);
            Assert.Equal(1, cells.Level(top));
            Fall(2000);
            Assert.Equal(1, cells.Level(top));
            clock.Frame(3000);
            LedMotion.Warmup = true;

            // Fade off: cells and keys go dark at once; the warm-up still runs.
            LedMotion.Fade = false;
            Rise(4000);
            Assert.Equal(0, cells.Level(top));
            clock.Frame(5000);
            Fall(6000);
            Assert.Equal(0, cells.Level(top));
            key.IsChecked = true;
            key.IsChecked = false;
            Assert.Equal(0, key.Lamp.Level(0));
            Assert.Equal(0, clock.Pending);
            LedMotion.Fade = true;

            // Reduce motion: both instant, whatever the switches say.
            LedMotion.ReduceMotion = true;
            Rise(7000);
            Assert.Equal(1, cells.Level(top));
            Fall(8000);
            Assert.Equal(0, cells.Level(top));
            key.IsChecked = true;
            key.IsChecked = false;
            Assert.Equal(0, key.Lamp.Level(0));
            Assert.Equal(0, clock.Pending);
            Assert.Equal((0, false), LedMotion.StateOf(window));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task TheClock_RunsOnlyWhileSomethingMoves_AndDropsAControlThatLeaves() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var first = new SquareKey { Glyph = "M" };
        var second = new SquareKey { Glyph = "S" };
        var panel = new StackPanel { Children = { first, second } };
        var window = Show(panel);
        try
        {
            Assert.Equal((0, false), LedMotion.StateOf(window));
            first.IsChecked = true;
            second.IsChecked = true;
            Assert.Equal((0, false), LedMotion.StateOf(window));

            clock.Time = 1000;
            first.IsChecked = false;
            second.IsChecked = false;
            Assert.Equal((2, true), LedMotion.StateOf(window));
            Assert.Equal(1, clock.Pending);

            // A key taken out of the window mid-fade goes dark at once and leaves the clock.
            panel.Children.Remove(second);
            clock.Frame(1010);
            Assert.Equal(0, second.Lamp.Level(0));
            Assert.Equal((1, true), LedMotion.StateOf(window));

            clock.Frame(1000 + SquareKey.FadeMs);
            Assert.Equal((0, false), LedMotion.StateOf(window));
            Assert.Equal(0, clock.Pending);

            // Nothing moving: no frame is asked for again.
            clock.Frame(2000);
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static (ToggleButton Key, LedLevels Lamp, double FadeMs) Key(string kind)
    {
        if (kind == nameof(SquareKey))
        {
            var square = new SquareKey { Glyph = "M", OnColor = Palette.Mute };
            return (square, square.Lamp, SquareKey.FadeMs);
        }

        if (kind == nameof(TextKey))
        {
            var text = new TextKey { Text = "Auto" };
            return (text, text.Lamp, TextKey.FadeMs);
        }

        var power = new PowerGlyph { GlyphSize = 12, Bloom = true, Width = 24, Height = 24 };
        return (power, power.Lamp, PowerGlyph.FadeMs);
    }

    private static Window Show(Control control)
    {
        var window = new Window { Width = 80, Height = 240, Background = Brushes.Black, Content = control };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
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
}
