using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// What signal indicators cost each frame (the run's D6 and ADR 0043's rule that the meter path allocates nothing once warm).
/// The update path (a meter update, the indicator's frame handler, and its redraw check) allocates nothing. Compact draws
/// the seam's way and allocates nothing beyond what Avalonia charges for its calls (the same calls replayed from a recording
/// cost the same). Medium and Large avoid what Avalonia charges for (a gradient fill copies its stops every frame, and
/// formatted text costs about 2.5 KB a call): a frame costs under a fixed budget, the same at every segment count. Bytes are this thread's managed allocations
/// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>); Skia's native memory is not counted. Each test writes what it
/// measured to its output.
/// </summary>
public sealed class SignalIndicatorCostTests
{
    private const long Now = 1_000_000_000;
    private const int Frames = 200;

    // What a Medium or Large frame may cost at most (less than one formatted text, or one stepped gradient at 13 segments),
    // and by how much it may vary between segment counts (none is expected: a few bytes of Avalonia's own noise).
    private const long FrameBudget = 1024;
    private const long GrowthNoise = 32;

    // The segment counts drawn: from a quarter of the seam's 26 to five times it (Medium's 33 and Large's 66 among them).
    private static readonly int[] Counts = [13, 26, 33, 66, 132];

    // The seam's lamp off, then lit.
    private static readonly double[] LampGlows = [0, 1];

    private static readonly FieldInfo GlowField = typeof(SignalIndicator).GetField("_glow", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SignalIndicator._glow was not found.");

    private static readonly FieldInfo SeamGlowField = typeof(JunctionSeam).GetField("_glow", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("JunctionSeam._glow was not found.");

    private static readonly FieldInfo RendererField = typeof(SignalIndicator).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SignalIndicator._renderer was not found.");

    // What the renderer builds once and keeps: the level's geometry and brushes, Compact's segment brushes, and the text.
    private static readonly FieldInfo[] CachedFields = new[] { "_level", "_segments", "_labels", "_readout" }
        .Select(name => typeof(SignalIndicatorRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"SignalIndicatorRenderer.{name} was not found."))
        .ToArray();

    public static TheoryData<string> Variants => ["Compact", "Medium", "Large"];

    public static TheoryData<string, string> ScaledShapes => new()
    {
        { "Medium", "Bar" },
        { "Medium", "Round" },
        { "Large", "Bar" },
        { "Large", "Round" },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public Task UpdatePath_AllocatesNothing_WhetherWhatItShowsChangesOrNot(string variant) => HeadlessApp.RunAsync(() =>
    {
        var levels = new MeterLevels();
        var indicator = new SignalIndicator { Variant = Enum.Parse<SignalIndicatorSize>(variant), Levels = levels, CarriesNotes = true };
        var window = Show(indicator);
        try
        {
            // Playing steadily: what it shows does not change, so each update only compares.
            var steady = Reading(rms: -12, peak: -6);
            var steadyBytes = InSteadyState(() => levels.Update(steady, Now), 1000);

            // Playing and moving: every update changes what it shows, and asks for a redraw.
            var low = Reading(rms: -30, peak: -20);
            var high = Reading(rms: -6, peak: 1.5);
            var flip = false;
            var movingBytes = InSteadyState(
                () =>
                {
                    flip = !flip;
                    levels.Update(flip ? high : low, Now);
                },
                1000);

            // Idle: audio off.
            var idleBytes = InSteadyState(() => levels.Update(null, Now), 1000);

            // Notes let go: the lamp's glow falls with every update.
            indicator.NotesHeld = true;
            indicator.NotesHeld = false;
            var lampBytes = InSteadyState(() => levels.Update(steady, Now), 1000);

            Report($"{variant} update path, bytes per 1000 updates: steady {steadyBytes}, moving {movingBytes}, idle {idleBytes}, lamp letting go {lampBytes}");
            Assert.Equal(0, steadyBytes);
            Assert.Equal(0, movingBytes);
            Assert.Equal(0, idleBytes);
            Assert.Equal(0, lampBytes);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task DrawCalls_Compact_DrawsEachSegment_AsTheSeamDoes() => HeadlessApp.RunAsync(() =>
    {
        // Compact keeps the seam's drawing, segment by segment. (Medium and Large draw slices of cached bitmaps, which
        // Avalonia cannot record into a drawing group, so their cost is held by bytes below.)
        var calls = new List<(int Count, Recorded Frame)>();
        foreach (var count in Counts)
        {
            var (indicator, window) = Playing(SignalIndicatorSize.Compact, LedShape.Bar, count);
            try
            {
                Assert.Equal(count, indicator.LevelMetrics.Count);
                calls.Add((count, Record(indicator)));
            }
            finally
            {
                window.Close();
            }
        }

        Report($"Compact draw calls by segment count: {string.Join("; ", calls.Select(c => $"{c.Count}: {c.Frame.Draws} draws, {c.Frame.Pushes} pushes"))}");
        Assert.True(calls[^1].Frame.Draws > calls[0].Frame.Draws, "Compact draws each segment.");
        return Task.CompletedTask;
    });

    [Fact]
    public Task Render_Compact_AllocatesNothing_BeyondWhatItsDrawCallsCost() => HeadlessApp.RunAsync(() =>
    {
        // The same draw calls replayed from a recording (the same brushes and geometry) cost what Avalonia charges for them;
        // a frame the indicator draws must cost no more.
        var (indicator, window) = Playing(SignalIndicatorSize.Compact, LedShape.Bar, 33);
        try
        {
            var frame = Record(indicator);
            var replay = new Replay(frame.Group);
            var renderer = RendererField.GetValue(indicator)!;
            var cached = CachedFields.Select(f => Cached(f, renderer)).ToArray();
            var rendered = Median(() => RenderBytes(indicator));
            for (var i = 0; i < CachedFields.Length; i++)
            {
                Assert.Same(cached[i], Cached(CachedFields[i], renderer));
            }

            var replayed = Median(() => RenderBytes(replay));
            Report($"Compact at 33 segments, managed bytes per frame: Render {rendered}, its {frame.Draws} draw calls replayed {replayed}");
            Assert.True(rendered <= replayed, $"Render allocated {rendered} bytes a frame; its draw calls alone, {replayed}.");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(ScaledShapes))]
    public Task Render_CostsAlmostNothing_AndTheSameWhateverTheSegmentCount(string variant, string shape) => HeadlessApp.RunAsync(() =>
    {
        // Medium and Large draw their level as slices of cached bitmaps through rectangle clips, and their text as cached
        // outlines with a solid brush, none of which Avalonia charges for a frame. So a frame costs at most the lamp's glow
        // (a gradient), and not a byte more for more segments. Drawn with stepped gradients (two stops a segment) and
        // formatted text instead, Medium cost 7.6 KB a frame at 13 segments growing by about 208 bytes a segment, and
        // Large about 21 KB at 13 with 2.5 KB a text.
        var size = Enum.Parse<SignalIndicatorSize>(variant);
        var frames = new List<(int Count, long Bytes)>();
        foreach (var count in Counts)
        {
            var (indicator, window) = Playing(size, Enum.Parse<LedShape>(shape), count);
            try
            {
                Assert.Equal(count, indicator.LevelMetrics.Count);

                // Setting the renderer's colours each frame, unchanged, keeps what it has cached: the level's bitmaps
                // (and Compact's brushes), the labels, and the readout.
                var renderer = RendererField.GetValue(indicator)!;
                RenderBytes(indicator);
                var cached = CachedFields.Select(f => Cached(f, renderer)).ToArray();
                frames.Add((count, Median(() => RenderBytes(indicator))));
                for (var i = 0; i < CachedFields.Length; i++)
                {
                    Assert.Same(cached[i], Cached(CachedFields[i], renderer));
                }
            }
            finally
            {
                window.Close();
            }
        }

        var gradient = GradientFillBytes(2);
        Report($"{variant}/{shape} Render, managed bytes per frame by segment count: {string.Join(", ", frames.Select(f => $"{f.Count}: {f.Bytes}"))}; one two-stop gradient fill {gradient}");
        Assert.All(frames, f => Assert.True(f.Bytes <= FrameBudget, $"At {f.Count} segments a frame cost {f.Bytes} bytes; the budget is {FrameBudget}."));
        var growth = frames.Max(f => f.Bytes) - frames.Min(f => f.Bytes);
        Assert.True(growth <= GrowthNoise, $"From {Counts[0]} to {Counts[^1]} segments a frame's cost varied by {growth} bytes.");
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Compact")]
    [InlineData("Medium")]
    public Task Render_WithTheLampLettingGo_CostsOnlyTheLampBrushesRecolour(string variant) => HeadlessApp.RunAsync(() =>
    {
        // As notes let go, the lamp's brush is recoloured in place every frame: Avalonia's change notification for it is the
        // only extra. (Large draws the lamp the same way; its text makes its frames too noisy to compare to the byte.)
        var (indicator, window) = Playing(Enum.Parse<SignalIndicatorSize>(variant), LedShape.Bar, 33);
        try
        {
            var lit = (object)1.0;
            var fades = Enumerable.Range(0, 100).Select(i => (object)(1 - (i * 0.0099))).ToArray();
            var next = 0;
            var steady = RenderBytes(indicator, () => GlowField.SetValue(indicator, lit));
            var fading = RenderBytes(indicator, () => GlowField.SetValue(indicator, fades[next++ % fades.Length]));
            var brush = new SolidColorBrush(Colors.Red);
            var colors = new[] { Colors.Red, Colors.Blue };
            var recolour = InSteadyState(() => brush.Color = colors[next++ % 2], Frames) / Frames;
            Report($"{variant} Render, managed bytes per frame: lamp lit {steady}, letting go {fading}; recolouring a brush {recolour}");
            Assert.True(fading - steady <= recolour, $"Letting go cost {fading - steady} bytes a frame more than lit; recolouring a brush, {recolour}.");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Seam_AllocatesNothing_BeyondWhatItsDrawCallsCost_OrPerUpdate() => HeadlessApp.RunAsync(() =>
    {
        // The junction's seam now draws through the shared renderer. Measured on origin/main at 39d3ad1 with the same
        // setup: 142 draws, 3 pushes, 56 bytes a frame with the lamp off and 432 with it lit, 0 per update.
        var signal = new JunctionSignal();
        SetInternal(signal, nameof(JunctionSignal.CanCarryNotes), true);
        SetInternal(signal, nameof(JunctionSignal.CanCarryAudio), true);
        signal.Levels.Update(Reading(rms: -9, peak: 1.2), Now);
        var seam = new JunctionSeam { Signal = signal, Width = 24, Height = 220 };
        var window = new Window { Width = 100, Height = 300, Content = seam };
        window.Show();
        window.UpdateLayout();
        try
        {
            var updates = InSteadyState(() => signal.Levels.Update(Reading(rms: -9, peak: 1.2), Now), 1000);
            var results = new List<string>();
            foreach (var glow in LampGlows)
            {
                SeamGlowField.SetValue(seam, glow);
                var frame = Record(seam);
                var rendered = Median(() => RenderBytes(seam));
                var replayed = Median(() => RenderBytes(new Replay(frame.Group)));
                results.Add($"lamp {(glow > 0 ? "lit" : "off")}: {frame.Draws} draws, {frame.Pushes} pushes, Render {rendered} bytes a frame, replayed {replayed}");
                Assert.True(rendered <= replayed, $"The seam allocated {rendered} bytes a frame; its draw calls alone, {replayed}.");
            }

            Report($"Seam: {string.Join("; ", results)}; update path {updates} bytes per 1000 updates");
            Assert.Equal(0, updates);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AvaloniasOwnCosts_SolidFillsAreFree() => HeadlessApp.RunAsync(() =>
    {
        // What the design leans on: a cached solid brush fills for nothing, and so does a cached bitmap or outline. What it cannot avoid as drawn: a gradient
        // fill's per-stop copy, and formatted text's per-call cost. Reported for the record.
        using var bitmap = new RenderTargetBitmap(new PixelSize(200, 400));
        var solid = new SolidColorBrush(Colors.Red);
        var open = InSteadyState(() => bitmap.CreateDrawingContext().Dispose(), Frames);
        var solidBytes = InSteadyState(
            () =>
            {
                using var context = bitmap.CreateDrawingContext();
                context.FillRectangle(solid, new Rect(0, 0, 10, 100));
                context.DrawRectangle(solid, null, new Rect(0, 0, 10, 100), 1, 1);
            },
            Frames) - open;
        // Medium and Large draw slices of cached bitmaps through rectangle clips, and text as cached outlines.
        using var column = new RenderTargetBitmap(new PixelSize(20, 200));
        var outline = new FormattedText("-12.4", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, solid).BuildGeometry(new Point(1, 1))!;
        var sliceBytes = InSteadyState(
            () =>
            {
                using var context = bitmap.CreateDrawingContext();
                using (context.PushClip(new Rect(0, 50, 20, 150)))
                {
                    context.DrawImage(column, new Rect(0, 0, 20, 200));
                }

                context.DrawGeometry(solid, null, outline);
            },
            Frames) - open;
        var family = new FontFamily("Inter");
        Report($"Avalonia (Skia, headless): solid fills {solidBytes / Frames} bytes a frame; a bitmap slice and a text outline {sliceBytes / Frames}; gradient fill {GradientFillBytes(26)} bytes at 26 stops, {PerStopGradientBytes():0.0} per stop; DrawText {Median(() => DrawTextBytes(family, 10))} bytes a call");
        Assert.Equal(0, solidBytes);
        Assert.Equal(0, sliceBytes);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RenderTimes_Informational() => HeadlessApp.RunAsync(() =>
    {
        // Informational only, and only meaningful in Release on a quiet machine: the UI thread's time to draw one frame
        // into a CPU (raster) Skia surface, after 5000 frames of warm-up. The app draws on the GPU, where this differs.
        var (large, window) = Playing(SignalIndicatorSize.Large, LedShape.Bar, 66);
        var (medium, mediumWindow) = Playing(SignalIndicatorSize.Medium, LedShape.Bar, 33);
        var signal = new JunctionSignal();
        SetInternal(signal, nameof(JunctionSignal.CanCarryNotes), true);
        SetInternal(signal, nameof(JunctionSignal.CanCarryAudio), true);
        signal.Levels.Update(Reading(rms: -9, peak: 1.2), Now);
        var seam = new JunctionSeam { Signal = signal, Width = 24, Height = 220 };
        var seamWindow = new Window { Width = 100, Height = 300, Content = seam };
        seamWindow.Show();
        seamWindow.UpdateLayout();
        try
        {
            // The seam first, so it has no more warm-up than on its own (as it was measured on origin/main).
            var seamTimes = Times(seam);
            Report($"Render µs ({Configuration()}): seam {seamTimes}; Medium/Bar 33 {Times(medium)}; Large/Bar 66 {Times(large)}");
        }
        finally
        {
            window.Close();
            mediumWindow.Close();
            seamWindow.Close();
        }

        return Task.CompletedTask;

        static string Times(Control control)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(200, 400));
            var samples = new double[2000];
            for (var i = -5000; i < samples.Length; i++)
            {
                using var context = bitmap.CreateDrawingContext();
                var start = Stopwatch.GetTimestamp();
                control.Render(context);
                if (i >= 0)
                {
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
            }

            Array.Sort(samples);
            return string.Create(CultureInfo.InvariantCulture, $"median {samples[samples.Length / 2]:0.0} (p10 {samples[samples.Length / 10]:0.0}, p90 {samples[samples.Length * 9 / 10]:0.0}, n {samples.Length})");
        }

        static string Configuration() =>
            typeof(SignalIndicator).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true ? "Debug" : "Release";
    });

    // A shown indicator at a segment count (1 px segments, 1 px gaps), playing over full scale with a peak held, the clip
    // light on, and the lamp lit, so every part of it draws.
    private static (SignalIndicator Indicator, Window Window) Playing(SignalIndicatorSize size, LedShape shape, int count)
    {
        var levels = new MeterLevels();
        levels.Update(Reading(rms: -3, peak: 1), Now);
        levels.Update(Reading(rms: -12, peak: 1), Now + 1);
        var indicator = new SignalIndicator
        {
            Variant = size,
            LedShape = shape,
            Levels = levels,
            CarriesNotes = true,
            NotesHeld = true,
            SegmentHeight = 1,
            SegmentGap = 1,
            LevelHeight = (2 * count) - 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        return (indicator, Show(indicator));
    }

    private static Window Show(Control control)
    {
        var window = new Window { Width = 200, Height = 400, Content = control };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    // One frame recorded into a drawing group: its draw calls, its pushes (clips, opacities, transforms), its texts, and its
    // fills with a gradient brush.
    private static Recorded Record(Control control)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            control.Render(context);
        }

        var frame = new Recorded(group);
        Count(group);
        return frame;

        void Count(Drawing drawing)
        {
            switch (drawing)
            {
                case DrawingGroup g:
                    frame.Pushes++;
                    foreach (var child in g.Children)
                    {
                        Count(child);
                    }

                    break;
                case GlyphRunDrawing:
                    frame.Draws++;
                    frame.Texts++;
                    break;
                case GeometryDrawing { Brush: IGradientBrush }:
                    frame.Draws++;
                    frame.GradientFills++;
                    break;
                default:
                    frame.Draws++;
                    break;
            }
        }
    }

    // Managed bytes this thread allocates drawing one frame into a Skia surface, beyond opening the surface's context (and
    // beyond before, run ahead of every frame).
    private static long RenderBytes(Control control, Action? before = null)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(200, 400));
        var open = InSteadyState(
            () =>
            {
                before?.Invoke();
                bitmap.CreateDrawingContext().Dispose();
            },
            Frames);
        var total = InSteadyState(
            () =>
            {
                before?.Invoke();
                using var context = bitmap.CreateDrawingContext();
                control.Render(context);
            },
            Frames);
        return (total - open) / Frames;
    }

    private static long GradientFillBytes(int stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 100, RelativeUnit.Absolute), EndPoint = new RelativePoint(0, 0, RelativeUnit.Absolute) };
        for (var i = 0; i < stops; i++)
        {
            brush.GradientStops.Add(new GradientStop(Colors.Red, i / (double)(stops - 1)));
        }

        using var bitmap = new RenderTargetBitmap(new PixelSize(200, 400));
        var open = InSteadyState(() => bitmap.CreateDrawingContext().Dispose(), Frames);
        var fill = InSteadyState(
            () =>
            {
                using var context = bitmap.CreateDrawingContext();
                context.FillRectangle(brush, new Rect(0, 0, 10, 100));
            },
            Frames);
        return (fill - open) / Frames;
    }

    private static double PerStopGradientBytes() => (GradientFillBytes(264) - GradientFillBytes(26)) / (264.0 - 26);

    private static long DrawTextBytes(FontFamily family, double size)
    {
        var text = new FormattedText("-00.0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family), size, Brushes.White);
        using var bitmap = new RenderTargetBitmap(new PixelSize(200, 400));
        var open = InSteadyState(() => bitmap.CreateDrawingContext().Dispose(), Frames);
        var drawn = InSteadyState(
            () =>
            {
                using var context = bitmap.CreateDrawingContext();
                context.DrawText(text, new Point(1, 1));
            },
            Frames);
        return (drawn - open) / Frames;
    }

    // A cached object (for a cached tuple of arrays, its first array), compared by reference.
    private static object? Cached(FieldInfo field, object renderer) =>
        field.GetValue(renderer) is System.Runtime.CompilerServices.ITuple tuple ? tuple[0] : field.GetValue(renderer);

    // The median of five measurements, so one disturbed round does not decide a bound.
    private static long Median(Func<long> measure)
    {
        var samples = Enumerable.Range(0, 5).Select(_ => measure()).Order().ToArray();
        return samples[2];
    }

    // As Bluestone.Tests.Unit's Allocations.InSteadyState: the last of up to ten rounds, stopping at the first from the third
    // on that allocates nothing, so one-time runtime work (a tier-up) cannot fail a path that does not allocate.
    private static long InSteadyState(Action step, int steps)
    {
        long allocated = 0;
        for (var round = 0; round < 10; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < steps; i++)
            {
                step();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round >= 2 && allocated == 0)
            {
                break;
            }
        }

        return allocated;
    }

    // What was measured, in the test's output and on the console.
    private static void Report(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        Console.WriteLine($"[cost] {message}");
    }

    private static void SetInternal(JunctionSignal signal, string property, object value) =>
        (typeof(JunctionSignal).GetProperty(property) ?? throw new InvalidOperationException($"JunctionSignal.{property} was not found.")).SetValue(signal, value);

    private static MeterReading Reading(double rms, double peak) => new(Linear(peak), Linear(peak), Linear(rms), Linear(rms));

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);

    private sealed class Recorded(DrawingGroup group)
    {
        public DrawingGroup Group { get; } = group;

        public int Draws { get; set; }

        public int Pushes { get; set; }

        public int Texts { get; set; }

        public int GradientFills { get; set; }
    }

    // A control that draws a recording: the same calls, made by Avalonia's own replay.
    private sealed class Replay(DrawingGroup group) : Control
    {
        public override void Render(DrawingContext context) => group.Draw(context);
    }
}
