using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// What LED motion adds to a frame outside the chain meters' ladder (<see cref="ChainMeterCostTests"/> has that one,
/// whose whole draw allocates nothing): the gain LEDs, the MIDI LED, and the three keys. A frame is the motion clock's
/// tick plus the control's draw into an offscreen bitmap's drawing context. The tick must allocate nothing for every one
/// (ADR 0043). The draws of the keys and the junction allocate in the drawing backend in every state, lit and dark alike
/// (gradient fills, clips, opacity layers, text; nothing of ours, whose brushes are all cached); those bytes are reported,
/// not asserted. The gain LEDs' draw must cost no more animating than at rest. Changing an LED's target (a property set)
/// is not part of a frame and is not measured. Bytes are this thread's managed allocations.
/// </summary>
public sealed class LedMotionCostTests
{
    private const double FrameMs = 2;

    // Every step a fade or warm-up passes through must be prebuilt when the colours are, not on the first frame that needs
    // it. So: one full pass to warm up, then a new gradient and settled draws (which rebuild the caches and give the cost
    // of a draw at rest, the gutter's own band), then one measured pass, not the best of several, through a full fade and a
    // full warm-up of every LED: its ticks allocate nothing and its draws no more than the same number at rest.
    [Fact]
    public Task GainLeds_AfterAGradientChange_AnimateAtTheCostOfADrawAtRest() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var gutter = new GainLedGutter { Value = 0, Width = 14, Height = 220 };
        var window = Show(gutter);
        var previous = MeterGradient.Gain.Gradient;
        try
        {
            using var bitmap = Bitmap(gutter);
            using var context = bitmap.CreateDrawingContext();
            gutter.Render(context);
            Pass(clock, () => gutter.Render(context), () => gutter.Value = double.NegativeInfinity, () => gutter.Value = 0, warms: true);

            (Color, Color)[] changes = [(Colors.Teal, Colors.Orange), (Colors.Lime, Colors.Crimson), (Colors.SkyBlue, Colors.Gold)];
            var bestExcess = long.MaxValue;
            foreach (var (low, high) in changes)
            {
                MeterGradient.Gain.Use([(0.0, low), (1.0, high)]);
                var atRest = ChainMeterCostTests.InSteadyState(() => gutter.Render(context), 1);
                var cached = MeterGradient.Gain.CachedBrushCount;
                var pass = Pass(clock, () => gutter.Render(context), () => gutter.Value = double.NegativeInfinity, () => gutter.Value = 0, warms: true);

                Assert.Equal(LedLevels.Steps, gutter.Leds.Step(0));
                ChainMeterCostTests.Report($"GainLedGutter after a gradient change: {pass.Draws} draws, {pass.DrawBytes} bytes (at rest {atRest} each); ticks {pass.TickBytes} bytes");
                Assert.Equal(cached, MeterGradient.Gain.CachedBrushCount);
                Assert.Equal(0, pass.TickBytes);
                bestExcess = Math.Min(bestExcess, pass.DrawBytes - (pass.Draws * atRest));
            }

            // Rare backend allocations on a loaded machine are tolerated by judging the best of three passes.
            Assert.True(bestExcess <= 0, $"Animated draws allocated {bestExcess} bytes more than at rest.");
        }
        finally
        {
            MeterGradient.Gain.Use(previous);
            window.Close();
        }

        return Task.CompletedTask;
    });

    public static TheoryData<string> Keys => ["SquareKey", "TextKey", "PowerGlyph"];

    // The ladder's whole draw allocates nothing (ChainMeterCostTests); that must hold through the first fade after a
    // gradient change too, so every glow step of every glowing cell is built when the colors are: the gradient's cache
    // must not grow during the first fade. Draw bytes are judged by the best of three passes, each after its own change.
    [Fact]
    public Task TheLadder_AfterAGradientChange_FadesAndWarmsUpWithoutAllocating() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var ladder = new LedLadderMeter { Mode = MixerMeterMode.PeakRms, IsReady = true, Width = 14, Height = 220 };
        var window = Show(ladder);
        var previous = MeterGradient.Mixer.Gradient;
        try
        {
            using var bitmap = Bitmap(ladder);
            using var context = bitmap.CreateDrawingContext();
            void Loud()
            {
                ladder.Show(0, 0, double.NaN, double.NaN);
                ladder.IsClipped = true;
            }

            void Quiet()
            {
                ladder.Show(-60, -60, double.NaN, double.NaN);
                ladder.IsClipped = false;
            }

            Loud();
            ladder.Render(context);
            Pass(clock, () => ladder.Render(context), Quiet, Loud, warms: true);

            (Color, Color)[] changes = [(Colors.Teal, Colors.Orange), (Colors.Lime, Colors.Crimson), (Colors.SkyBlue, Colors.Gold)];
            var best = long.MaxValue;
            foreach (var (low, high) in changes)
            {
                MeterGradient.Mixer.Use([(0.0, low), (1.0, high)]);
                ladder.Render(context);
                var cached = MeterGradient.Mixer.CachedBrushCount;
                var pass = Pass(clock, () => ladder.Render(context), Quiet, Loud, warms: true);

                ChainMeterCostTests.Report($"LedLadderMeter after a gradient change: {pass.Draws} draws, {pass.DrawBytes} bytes; ticks {pass.TickBytes} bytes");
                Assert.Equal(cached, MeterGradient.Mixer.CachedBrushCount);
                Assert.Equal(0, pass.TickBytes);
                best = Math.Min(best, pass.DrawBytes);
            }

            Assert.Equal(0, best);
        }
        finally
        {
            MeterGradient.Mixer.Use(previous);
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Keys))]
    public Task Keys_FadeOnTicksThatAllocateNothing(string kind) => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        ToggleButton key = kind switch
        {
            "SquareKey" => new SquareKey { Glyph = "M", OnColor = Palette.Mute, IsChecked = true },
            "TextKey" => new TextKey { Text = "Auto", IsChecked = true },
            _ => new PowerGlyph { GlyphSize = 12, Bloom = true, Width = 24, Height = 24, IsChecked = true },
        };
        var window = Show(key);
        try
        {
            using var bitmap = Bitmap(key);
            using var context = bitmap.CreateDrawingContext();
            var pass = InSteadyState(() => Pass(clock, () => key.Render(context), () => key.IsChecked = false, () => key.IsChecked = true, warms: false));
            ChainMeterCostTests.Report($"{kind}, one fade: ticks {pass.TickBytes} bytes; {pass.Draws} draws, {pass.DrawBytes} bytes in the drawing backend");
            Assert.Equal(0, pass.TickBytes);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMidiLed_FadesOnTicksThatAllocateNothing() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var signal = new JunctionSignal();
        (typeof(JunctionSignal).GetProperty(nameof(JunctionSignal.CanCarryNotes)) ?? throw new InvalidOperationException("CanCarryNotes was not found."))
            .SetValue(signal, true);
        var update = (typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Update was not found."))
            .CreateDelegate<Action<bool, MeterReading?, long>>(signal);
        var junction = new ChainJunction { Signal = signal, Width = 48, Height = 220 };
        var window = Show(junction);
        try
        {
            update(true, null, 1_000_000_000L);
            using var bitmap = Bitmap(junction);
            using var context = bitmap.CreateDrawingContext();
            var pass = InSteadyState(() => Pass(clock, () => junction.Render(context), () => update(false, null, 1_000_000_000L), () => update(true, null, 1_000_000_000L), warms: false));
            ChainMeterCostTests.Report($"ChainJunction MIDI LED, one fade: ticks {pass.TickBytes} bytes; {pass.Draws} draws, {pass.DrawBytes} bytes in the drawing backend");
            Assert.True(junction.LampGlow == 1, "The pass ends lit.");
            Assert.Equal(0, pass.TickBytes);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    /// <summary>
    /// Sends the LEDs dark, runs frames every <see cref="FrameMs"/> until the fade is over, lights them, and runs frames
    /// until the warm-up is over (one frame where they light at once); the bytes allocated by the clock's ticks and by the
    /// draws, and how many draws.
    /// </summary>
    private static (long TickBytes, long DrawBytes, int Draws) Pass(ManualLedClock clock, Action draw, Action dark, Action lit, bool warms)
    {
        long ticks = 0;
        long draws = 0;
        var count = 0;
        foreach (var change in (ReadOnlySpan<Action>)[dark, lit])
        {
            clock.Time += 1000;
            change();
            var frames = 0;
            do
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                draw();
                var drawn = GC.GetAllocatedBytesForCurrentThread();
                clock.Frame(clock.Time + FrameMs);
                ticks += GC.GetAllocatedBytesForCurrentThread() - drawn;
                draws += drawn - before;
                count++;
                frames++;
            }
            while (clock.Pending > 0);

            var last = GC.GetAllocatedBytesForCurrentThread();
            draw();
            draws += GC.GetAllocatedBytesForCurrentThread() - last;
            count++;
            Assert.True(frames > 5 || (change == lit && !warms), $"Only {frames} frames moved.");
        }

        return (ticks, draws, count);
    }

    // As ChainMeterCostTests.InSteadyState, on the ticks: the last of up to ten passes, stopping at the first from the third
    // on whose ticks allocate nothing.
    private static (long TickBytes, long DrawBytes, int Draws) InSteadyState(Func<(long TickBytes, long DrawBytes, int Draws)> pass)
    {
        var result = pass();
        for (var round = 1; round < 10 && (round < 3 || result.TickBytes != 0); round++)
        {
            result = pass();
        }

        return result;
    }

    private static RenderTargetBitmap Bitmap(Control control) =>
        new(new PixelSize(Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))));

    private static Window Show(Control control)
    {
        var window = new Window { Width = 200, Height = 300, Content = control };
        window.Show();
        window.UpdateLayout();
        return window;
    }
}
