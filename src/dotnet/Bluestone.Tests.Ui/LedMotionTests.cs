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
            clock.Frame(1045);
            Assert.Equal(0.25, cells.Level(loud), 6);
            Assert.Equal(1, cells.Level(quiet));
            clock.Frame(1089);
            Assert.True(cells.Level(loud) > 0, "A meter cell is still fading 89 ms on.");
            clock.Frame(1090);
            Assert.Equal(0, cells.Level(loud));

            // Rising: a dark cell warms up in a straight line.
            clock.Time = 2000;
            ladder.Show(-6, -6, double.NaN, double.NaN);
            Render(window);
            Assert.Equal(0, cells.Level(loud));
            clock.Frame(2010);
            Assert.Equal(0.5, cells.Level(loud), 6);
            clock.Frame(2019);
            Assert.True(cells.Level(loud) < 1, "A meter cell is still warming up 19 ms on.");
            clock.Frame(2020);
            Assert.Equal(1, cells.Level(loud));

            // The held peak lights at once and fades.
            clock.Time = 3000;
            ladder.Show(-30, -30, -6, -6);
            Render(window);
            var peak = ladder.Cells(-30, -6, cells.Count).Held;
            Assert.True(peak > quiet, $"Held cell {peak}.");
            Assert.Equal(LedLevels.Steps, held.Step(peak));
            clock.Frame(3090);
            clock.Time = 4000;
            ladder.Show(-30, -30, double.NaN, double.NaN);
            Render(window);
            clock.Frame(4045);
            Assert.Equal(0.25, held.Level(peak), 6);
            clock.Frame(4090);
            Assert.Equal(0, held.Level(peak));

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
            clock.Frame(6070);
            Assert.Equal(0.25, lamps.Level(0), 6);
            clock.Frame(6139);
            Assert.True(lamps.Level(0) > 0, "A clip lamp is still fading 139 ms on.");
            clock.Frame(6140);
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
            clock.Frame(1070);
            Assert.Equal(0.25, leds.Level(0), 6);
            clock.Frame(1139);
            Assert.True(leds.Level(0) > 0, "A gain LED is still fading 139 ms on.");
            clock.Frame(1140);
            Assert.Equal(0, leds.Level(0));

            clock.Time = 2000;
            gutter.Value = 0;
            Render(window);
            clock.Frame(2010);
            Assert.Equal(0.5, leds.Level(0), 6);
            clock.Frame(2019);
            Assert.True(leds.Level(0) < 1, "A gain LED is still warming up 19 ms on.");
            clock.Frame(2020);
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
    public Task GainLedCountAndCenters_FollowAvailableTravelAtFixedPitch() => HeadlessApp.RunAsync(() =>
    {
        var gutter = new GainLedGutter { Value = double.NegativeInfinity, Width = 14, Height = 220 };
        var window = Show(gutter);
        try
        {
            Render(window);
            var tallTravel = gutter.Bounds.Height - ConsoleRail.TravelTop - ConsoleRail.TravelBottomInset;
            Assert.Equal(11, gutter.VisibleCount);
            Assert.Equal(11, gutter.Leds.Count);
            AssertFixedPitch(11, tallTravel);

            gutter.Height = 138;
            Render(window);
            var shorterTravel = gutter.Bounds.Height - ConsoleRail.TravelTop - ConsoleRail.TravelBottomInset;
            var shorterColors = gutter.Colors.ToArray();
            Assert.Equal(7, gutter.VisibleCount);
            Assert.Equal(7, gutter.Leds.Count);
            AssertFixedPitch(7, shorterTravel);
            Assert.Equal(GainLedGutter.Swatch(1d / 6), shorterColors[1]);

            // A resize within the same count preserves the visible gradient palette.
            gutter.Height = 148;
            Render(window);
            var sameCountTravel = gutter.Bounds.Height - ConsoleRail.TravelTop - ConsoleRail.TravelBottomInset;
            Assert.Equal(7, gutter.VisibleCount);
            Assert.Equal(7, gutter.Leds.Count);
            AssertFixedPitch(7, sameCountTravel);
            Assert.Equal(shorterColors, gutter.Colors);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static void AssertFixedPitch(int count, double travel)
    {
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(index * GainLedGutter.MinimumPitch, GainLedGutter.CentreFraction(index, travel) * travel, 6);
        }
    }

    [Fact]
    public Task GainLeds_FillTheNextLedAsTheFaderReachesItsCenter() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var gutter = new GainLedGutter { Width = 14, Height = 220 };
        var window = Show(gutter);
        try
        {
            var travel = gutter.Bounds.Height - ConsoleRail.TravelTop - ConsoleRail.TravelBottomInset;
            clock.Time = 1000;
            gutter.Value = ConsoleRail.Db((GainLedGutter.MinimumPitch / 2) / travel);
            Render(window);

            var leds = gutter.Leds;
            Assert.Equal(1, leds.Level(0));
            Assert.Equal(0.5, leds.Level(1), 6);

            // At the next LED's centre, the lower one stays full and the next finishes warming up.
            clock.Time = 2000;
            gutter.Value = ConsoleRail.Db(GainLedGutter.MinimumPitch / travel);
            Render(window);
            clock.Frame(2010);
            Assert.Equal(1, leds.Level(0));
            Assert.Equal(1, leds.Level(1));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task GainLeds_BelowFloorAndNonFiniteValuesStayDark() => HeadlessApp.RunAsync(() =>
    {
        var gutter = new GainLedGutter { Value = MixerRail.GainFloor - 1, Width = 14, Height = 220 };
        var window = Show(gutter);
        try
        {
            foreach (var value in new[] { gutter.Value, double.NegativeInfinity, double.NaN, double.PositiveInfinity })
            {
                gutter.Value = value;
                Render(window);
                Assert.All(Enumerable.Range(0, gutter.Leds.Count), i =>
                {
                    Assert.Equal(0, gutter.Leds.Level(i));
                    Assert.Equal(0, gutter.Leds.Step(i));
                });
            }
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
            clock.Time = 1000;
            update.Invoke(signal, [true, null, 1_000_000_000L]);
            Assert.Equal(1, junction.LampGlow);
            Assert.Equal(0, clock.Pending);

            update.Invoke(signal, [false, null, 1_000_000_000L]);
            Assert.Equal(1, junction.LampGlow);
            clock.Frame(1070);
            Assert.Equal(0.25, junction.LampGlow, 6);
            clock.Frame(1139);
            Assert.True(junction.LampGlow > 0, "The MIDI LED is still fading 139 ms on.");
            clock.Frame(1140);
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

            clock.Frame(1060);
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

    [Fact]
    public Task AKeyMovedToAnotherWindowMidFade_SettlesThere_AnimatesOnTheNewClock_AndBothClocksSettle() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var key = new SquareKey { Glyph = "M" };
        var first = new StackPanel { Children = { key } };
        var second = new StackPanel();
        var windowA = Show(first);
        var windowB = Show(second);
        try
        {
            key.IsChecked = true;
            clock.Time = 1000;
            key.IsChecked = false;
            Assert.Equal((1, true), LedMotion.StateOf(windowA));

            // Moved mid-fade: it settles dark on leaving rather than staying part-lit on the first window's clock.
            first.Children.Remove(key);
            second.Children.Add(key);
            Assert.Equal(0, key.Lamp.Level(0));
            Assert.False(key.Lamp.IsRunning);

            // Its next change fades on the second window's frames alone.
            clock.Time = 1100;
            key.IsChecked = true;
            key.IsChecked = false;
            Assert.Equal((1, true), LedMotion.StateOf(windowB));
            Assert.Equal(1, clock.PendingFor(windowB));
            clock.Frame(1130, windowB);
            Assert.Equal(0.25, key.Lamp.Level(0), 6);
            clock.Frame(1160, windowB);
            Assert.Equal(0, key.Lamp.Level(0));
            Assert.Equal((0, false), LedMotion.StateOf(windowB));

            // The first window's next frame drops what it held, and nothing more is asked for anywhere.
            clock.Frame(1200, windowA);
            Assert.Equal((0, false), LedMotion.StateOf(windowA));
            Assert.Equal(0, clock.Pending);

            // Out and back into the same window mid-fade: one registration, not two.
            clock.Time = 2000;
            key.IsChecked = true;
            key.IsChecked = false;
            second.Children.Remove(key);
            second.Children.Add(key);
            key.IsChecked = true;
            key.IsChecked = false;
            Assert.Equal((1, true), LedMotion.StateOf(windowB));
            clock.Frame(2060);
            Assert.Equal(0, key.Lamp.Level(0));
            Assert.Equal((0, false), LedMotion.StateOf(windowB));
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            windowA.Close();
            windowB.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AReversedLed_MovesOnFromTheLevelShown() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var owner = new Border();
        var window = Show(owner);
        try
        {
            // A 90 ms fade and a 20 ms warm-up, as the meter cells.
            var leds = new LedLevels(owner, 90, 20, 1);

            // Warming up, then sent dark half-way: the fade starts from 0.5 and still takes 90 ms.
            clock.Time = 1000;
            leds.Set(0, true, clock.Time);
            clock.Frame(1010);
            Assert.Equal(0.5, leds.Level(0), 6);
            leds.Set(0, false, clock.Time);
            clock.Frame(1055);
            Assert.Equal(0.125, leds.Level(0), 6);
            clock.Frame(1100);
            Assert.Equal(0, leds.Level(0));

            // Fading, then lit again at a quarter: the warm-up ramps from 0.25 at the same rate, so it is lit in 15 ms.
            clock.Time = 2000;
            leds.Set(0, true, clock.Time, instant: true);
            leds.Set(0, false, clock.Time);
            clock.Frame(2045);
            Assert.Equal(0.25, leds.Level(0), 6);
            leds.Set(0, true, clock.Time);
            clock.Frame(2050);
            Assert.Equal(0.5, leds.Level(0), 6);
            clock.Frame(2060);
            Assert.Equal(1, leds.Level(0));
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
    public Task AFractionalTarget_WarmsAndFadesToItsExactLevel() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var owner = new Border();
        var window = Show(owner);
        try
        {
            var leds = new LedLevels(owner, 90, 20, 1);
            clock.Time = 1000;
            leds.Set(0, 0.5, clock.Time);

            clock.Frame(1005);
            Assert.Equal(0.25, leds.Level(0), 6);
            Assert.Equal(2, leds.Step(0));
            clock.Frame(1010);
            Assert.Equal(0.5, leds.Level(0), 6);
            Assert.Equal(4, leds.Step(0));
            Assert.Equal((0, false), LedMotion.StateOf(window));

            clock.Time = 2000;
            leds.Set(0, 0.25, clock.Time);
            clock.Frame(2045);
            Assert.Equal(0.3125, leds.Level(0), 6);
            clock.Frame(2090);
            Assert.Equal(0.25, leds.Level(0), 6);
            Assert.Equal(2, leds.Step(0));
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
    public Task AnInstantSameTargetRequest_SettlesAPartiallyFadedLed_AndStopsTheClock() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var owner = new Border();
        var window = Show(owner);
        try
        {
            var leds = new LedLevels(owner, 90, count: 1);
            clock.Time = 1000;
            leds.Set(0, true, clock.Time, instant: true);
            leds.Set(0, false, clock.Time);

            clock.Frame(1045);
            Assert.Equal(0.25, leds.Level(0), 6);
            Assert.Equal((1, true), LedMotion.StateOf(window));

            // Same target, but instant means an identity change can clear a stale MIDI light now, not after its fade.
            leds.Set(0, false, clock.Time, instant: true);
            Assert.Equal(0, leds.Level(0));
            Assert.Equal(0, leds.Step(0));

            // The already-requested frame removes the now-settled animator from the window's clock.
            clock.Frame(1046);
            Assert.Equal((0, false), LedMotion.StateOf(window));
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    public static TheoryData<string> Switches => ["Fade", "Warmup", "ReduceMotion"];

    [Theory]
    [MemberData(nameof(Switches))]
    public Task ASwitchTurnedOffMidMotion_ShowsTheEndStateOnTheNextFrame(string change) => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var owner = new Border();
        var window = Show(owner);
        try
        {
            // LED 0 fades, LED 1 warms up.
            var leds = new LedLevels(owner, 90, 20, 2);
            clock.Time = 1000;
            leds.Set(0, true, clock.Time, instant: true);
            leds.Set(0, false, clock.Time);
            leds.Set(1, true, clock.Time);
            clock.Frame(1010);
            var fading = leds.Level(0);
            Assert.True(fading is > 0 and < 1, $"Fading at {fading}.");
            Assert.Equal(0.5, leds.Level(1), 6);

            switch (change)
            {
                case "Fade":
                    LedMotion.Fade = false;
                    break;
                case "Warmup":
                    LedMotion.Warmup = false;
                    break;
                default:
                    LedMotion.ReduceMotion = true;
                    break;
            }

            clock.Frame(1011);
            var fadeStops = change != "Warmup";
            var warmupStops = change != "Fade";
            Assert.Equal(fadeStops, leds.Level(0) == 0);
            Assert.Equal(warmupStops, leds.Level(1) == 1);
            Assert.Equal(fadeStops && warmupStops ? (0, false) : (1, true), LedMotion.StateOf(window));

            // Whatever was left still runs out on time, and the clock stops.
            clock.Frame(1100);
            Assert.Equal(0, leds.Level(0));
            Assert.Equal(1, leds.Level(1));
            Assert.Equal((0, false), LedMotion.StateOf(window));
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
            return (square, square.Lamp, 60);
        }

        if (kind == nameof(TextKey))
        {
            var text = new TextKey { Text = "Auto" };
            return (text, text.Lamp, 60);
        }

        var power = new PowerGlyph { GlyphSize = 12, Bloom = true, Width = 24, Height = 24 };
        return (power, power.Lamp, 60);
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
