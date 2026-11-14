using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The standalone <see cref="SignalIndicator"/> where its state could go stale or wrong: the lamp and readout across new
/// levels and a trip out of the window, the tooltip while it is being read, which clicks and keys it takes, the cached
/// columns it keeps (and lets go), what a level without audio draws, the readout just over full scale, the focus ring, and
/// the renderer drawing each segment (and its halo) once and whole.
/// </summary>
public sealed class SignalIndicatorReviewTests
{
    private const long Now = 1_000_000_000;

    private static readonly FieldInfo RendererField = typeof(SignalIndicator).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SignalIndicator._renderer was not found.");

    public static TheoryData<string> Shapes => ["Bar", "Round"];

    [Fact]
    public Task Lamp_GoesOutWhenItsLevelsChangeWhileItIsLettingGo() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -40, peak: -30);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Medium, CarriesNotes = true, LampRelease = TimeSpan.FromSeconds(10), Levels = levels };
        var window = Show(indicator);
        try
        {
            // Letting go over ten seconds, as the levels update.
            indicator.NotesHeld = true;
            levels.Update(Reading(rms: -40, peak: -30), Now);
            indicator.NotesHeld = false;
            levels.Update(Reading(rms: -40, peak: -30), Now);
            Assert.True(indicator.DisplayState.Glow is > 0.5 and < 1, $"glow {indicator.DisplayState.Glow}");

            // Without levels there are no more updates to let it go by: it goes out at once, not stays lit.
            indicator.Levels = null;
            Assert.Equal(0, indicator.DisplayState.Glow);

            // The same when it is given other levels mid-release (they may never update).
            indicator.Levels = levels;
            indicator.NotesHeld = true;
            levels.Update(Reading(rms: -40, peak: -30), Now);
            indicator.NotesHeld = false;
            levels.Update(Reading(rms: -40, peak: -30), Now);
            Assert.True(indicator.DisplayState.Glow > 0);
            indicator.Levels = Levels(rms: -40, peak: -30);
            Assert.Equal(0, indicator.DisplayState.Glow);

            // Notes still held across new levels: it stays lit.
            indicator.NotesHeld = true;
            indicator.Levels = levels;
            Assert.Equal(1, indicator.DisplayState.Glow);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Readout_FollowsWhatHappenedWhileItWasOutOfTheWindow() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -20, peak: -12);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = levels };
        var host = new Border { Child = indicator };
        var window = new Window { Width = 200, Height = 400, Content = host };
        window.Show();
        try
        {
            Assert.Equal(-120, indicator.DisplayState.Readout);
            window.Content = null;
            levels.Update(Reading(rms: -20, peak: -3), Now + 1);
            window.Content = host;
            Assert.Equal(SignalIndicatorRenderer.ReadoutValue(levels), indicator.DisplayState.Readout);
            Assert.Equal(-30, indicator.DisplayState.Readout);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Tooltip_OpenedFromTheKeyboard_FollowsThePeak() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -30, peak: -20);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = levels };
        var window = Show(indicator);
        try
        {
            indicator.Focus(NavigationMethod.Tab);
            Assert.True(ToolTip.GetIsOpen(indicator));
            Assert.Contains("peak -20.0 dB", Tip(indicator), StringComparison.Ordinal);

            // Not hovered, only focused: the open tooltip still follows the peak.
            levels.Update(Reading(rms: -30, peak: -12), Now + 1);
            Assert.Contains("peak -12.0 dB", Tip(indicator), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Tooltip_UnderThePointer_FollowsThePeakEvenWhenTheLevelLooksTheSame_AndIsKeptWhenItsWordsAreTheSame() => HeadlessApp.RunAsync(() =>
    {
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Medium };
        var count = 33;
        var levels = Levels(rms: -40, peak: At(count, 20.3));
        indicator.Levels = levels;
        var window = Show(indicator);
        try
        {
            Assert.Equal(count, indicator.LevelMetrics.Count);
            window.MouseMove(Centre(window, indicator));
            var before = indicator.DisplayState;
            var tip = Tip(indicator);

            // A peak a little higher in the same segment: nothing to redraw, but the words change.
            levels.Update(Reading(rms: -40, peak: At(count, 20.6)), Now + 1);
            Assert.Equal(before, indicator.DisplayState);
            Assert.NotEqual(tip, Tip(indicator));

            // The same reading again: the very same words, not rebuilt.
            var same = Tip(indicator);
            levels.Update(Reading(rms: -40, peak: At(count, 20.6)), Now + 2);
            Assert.Same(same, Tip(indicator));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Large_WithoutAudio_DoesNotClearTheClip() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -3, peak: 1);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = levels, CarriesAudio = false };
        var window = Show(indicator);
        try
        {
            // Even given focus by code, Enter, Space, and a click leave the clip for whoever shows the level.
            indicator.Focusable = true;
            indicator.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Click(window, indicator);
            Assert.False(indicator.ClearClip());
            Assert.True(levels.Clipped);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Large_TakesAClickOrKey_OnlyWhenItClearsSomething() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -20, peak: -10);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = levels };
        var window = Show(indicator);
        var keys = 0;
        var presses = 0;
        window.AddHandler(InputElement.KeyDownEvent, (_, _) => keys++, RoutingStrategies.Bubble);
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) => presses++, RoutingStrategies.Bubble);
        try
        {
            // Nothing to clear: the click and the key go on to whatever is behind it.
            indicator.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Click(window, indicator);
            Assert.Equal(1, keys);
            Assert.Equal(1, presses);

            // A clip light to clear: it takes them.
            levels.Update(Reading(rms: -3, peak: 1), Now + 1);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.False(levels.Clipped);
            levels.Update(Reading(rms: -3, peak: 1), Now + 2);
            Click(window, indicator);
            Assert.False(levels.Clipped);
            Assert.Equal(1, keys);
            Assert.Equal(1, presses);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Cursor_IsOneSharedHand() => HeadlessApp.RunAsync(() =>
    {
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large };
        var hand = indicator.Cursor;
        Assert.NotNull(hand);
        indicator.Variant = SignalIndicatorSize.Medium;
        Assert.Null(indicator.Cursor);
        indicator.Variant = SignalIndicatorSize.Large;
        Assert.Same(hand, indicator.Cursor);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CachedColumns_AreLetGoOutOfTheWindow_AndForCompactOrNoAudio_AndRebuiltWhenDrawn() => HeadlessApp.RunAsync(() =>
    {
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = Levels(rms: -9, peak: -3) };
        var renderer = (SignalIndicatorRenderer)RendererField.GetValue(indicator)!;
        var window = Show(indicator);
        var host = window.Content;
        try
        {
            Capture(window).Dispose();
            Assert.True(renderer.HasLevelCache);

            window.Content = null;
            Assert.False(renderer.HasLevelCache);
            window.Content = host;
            Capture(window).Dispose();
            Assert.True(renderer.HasLevelCache);

            indicator.Variant = SignalIndicatorSize.Compact;
            Assert.False(renderer.HasLevelCache);
            Capture(window).Dispose();
            Assert.False(renderer.HasLevelCache);
            indicator.Variant = SignalIndicatorSize.Medium;
            Capture(window).Dispose();
            Assert.True(renderer.HasLevelCache);

            indicator.CarriesAudio = false;
            Assert.False(renderer.HasLevelCache);
            Capture(window).Dispose();
            Assert.False(renderer.HasLevelCache);
            indicator.CarriesAudio = true;
            Capture(window).Dispose();
            Assert.True(renderer.HasLevelCache);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void Readout_JustOverFullScale_ReadsPlusAndIsTinted()
    {
        var tenths = SignalIndicatorRenderer.ReadoutValue(Levels(rms: -3, peak: 0.04));
        Assert.Equal(1, tenths);
        Assert.Equal("+0.1", SignalIndicatorRenderer.ReadoutText(tenths));
        Assert.Equal("+1.2", SignalIndicatorRenderer.ReadoutText(SignalIndicatorRenderer.ReadoutValue(Levels(rms: -3, peak: 1.2))));
        Assert.Equal("-0.1", SignalIndicatorRenderer.ReadoutText(SignalIndicatorRenderer.ReadoutValue(Levels(rms: -3, peak: -0.1))));
    }

    [Fact]
    public Task Readout_JustOverFullScale_IsDrawnInTheClipTint() => HeadlessApp.RunAsync(() =>
    {
        // The readout drawn for +0.04 dB is the "+0.1" in the clip tint, not a plain "0.0".
        var over = DrawReadout(SignalIndicatorRenderer.ReadoutValue(Levels(rms: -3, peak: 0.04)));
        var plus = DrawReadout(1);
        var zero = DrawReadout(0);
        Assert.True(over.AsSpan().SequenceEqual(plus));
        Assert.False(over.AsSpan().SequenceEqual(zero));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Medium")]
    [InlineData("Large")]
    public Task WithoutAudio_DrawsNoScale_AndLargeHasNoReadout(string variantName) => HeadlessApp.RunAsync(() =>
    {
        var indicator = new SignalIndicator { Variant = Enum.Parse<SignalIndicatorSize>(variantName), CarriesAudio = false, Levels = Levels(rms: -9, peak: -3) };
        var window = Show(indicator);
        try
        {
            Assert.Equal(0, indicator.DisplayState.Readout);

            // Without notes or audio there is nothing to draw: not the scale either.
            byte[] shown;
            using (var frame = Capture(window))
            {
                shown = Pixels(frame);
            }

            indicator.IsVisible = false;
            using var hidden = Capture(window);
            Assert.True(Pixels(hidden).AsSpan().SequenceEqual(shown), "It drew something without notes or audio.");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Shapes))]
    public Task KeptColumns_DrawWhatANewIndicatorDraws_AsTheLevelShapeAndSizeChange(string shapeName) => HeadlessApp.RunAsync(() =>
    {
        // One indicator keeps its cached columns warm through each change; each time, a new one in the same state must
        // draw the very same picture (nothing stale kept from before).
        var shape = Enum.Parse<LedShape>(shapeName);
        var steps = new (string Name, Action<SignalIndicator> Apply)[]
        {
            ("level", i => ((MeterLevels)i.Levels!).Update(Reading(rms: -6, peak: -1), Now + 1)),
            ("shape", i => i.LedShape = shape == LedShape.Bar ? LedShape.Round : LedShape.Bar),
            ("height", i => i.LevelHeight = 80),
            ("fractional width", i => i.BarWidth = 5.2),
            ("peak style", i => i.LevelStyle = JunctionLevelStyle.Peak),
        };

        var warm = new SignalIndicator { Variant = SignalIndicatorSize.Large, LedShape = shape, Levels = Levels(rms: -30, peak: -20) };
        var window = Show(warm);
        try
        {
            Capture(window).Dispose();
            for (var step = 0; step < steps.Length; step++)
            {
                steps[step].Apply(warm);
                byte[] kept;
                using (var frame = Capture(window))
                {
                    kept = Pixels(frame);
                }

                var fresh = new SignalIndicator { Variant = SignalIndicatorSize.Large, LedShape = shape, Levels = Levels(rms: -30, peak: -20) };
                for (var i = 0; i <= step; i++)
                {
                    steps[i].Apply(fresh);
                }

                var freshWindow = Show(fresh);
                try
                {
                    Assert.Equal(warm.DisplayState, fresh.DisplayState);
                    using var frame = Capture(freshWindow);
                    Assert.True(Pixels(frame).AsSpan().SequenceEqual(kept), $"After '{steps[step].Name}' the kept columns drew differently from new ones.");
                }
                finally
                {
                    freshWindow.Close();
                }
            }
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Shapes))]
    public Task Level_DrawsTheTopSegmentOnce_WhenItIsBothHeldAndOver(string shapeName) => HeadlessApp.RunAsync(() =>
    {
        var shape = Enum.Parse<LedShape>(shapeName);
        var metrics = new SignalLevelMetrics(98, 6, 1, 2, 1);
        var top = metrics.Count - 1;
        var redundant = new SignalDisplayState(10, 12, top, top, true, true, false, true, 0, false, 0);
        var once = redundant with { OverLeft = false, OverRight = false };
        Assert.True(DrawLevel(metrics, shape, redundant).AsSpan().SequenceEqual(DrawLevel(metrics, shape, once)), "The held top segment was drawn again for the over.");
        return Task.CompletedTask;
    });

    [Fact]
    public Task RoundHalo_ReachesAWholeUnitBelowTheDot() => HeadlessApp.RunAsync(() =>
    {
        // A dot's halo is a unit round it; its slice must not cut it at 0.4 below the dot, as a bar's is.
        var metrics = new SignalLevelMetrics(54, 5, 2, 5, 1);
        var state = new SignalDisplayState(1, 1, -1, -1, false, false, false, false, 0, false, 0);
        var pixels = DrawLevel(metrics, LedShape.Round, state, out var stride);

        // At 2x, the row from 0.5 to 1 unit below the bottom dot, under the middle of the left dot.
        var row = (int)((Origin + metrics.Height + 0.5) * 2);
        var column = (int)((Origin + (metrics.BarWidth / 2)) * 2);
        Assert.True(pixels[(row * stride) + (column * 4) + 3] > 0, "The round halo was cut off below the dot.");
        return Task.CompletedTask;
    });

    [Fact]
    public Task FocusRing_IsCrispAndOutsideTheLevel_AndDoesNotResizeIt() => HeadlessApp.RunAsync(() =>
    {
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = Levels(rms: -9, peak: -3), Margin = new Thickness(8) };
        var window = Show(indicator);
        try
        {
            var size = indicator.Bounds.Size;
            byte[] unfocused;
            int stride;
            using (var frame = Capture(window))
            {
                unfocused = Pixels(frame);
                stride = frame.PixelSize.Width * 4;
            }

            indicator.Focus(NavigationMethod.Tab);
            // The tooltip that opens with keyboard focus is not what is being checked here.
            ToolTip.SetIsOpen(indicator, false);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(indicator.IsFocused);
            Assert.Null(indicator.Effect);
            byte[] focused;
            using (var frame = Capture(window))
            {
                focused = Pixels(frame);
            }

            Assert.Equal(size, indicator.Bounds.Size);

            // Something changed, and only outside what it draws: the level itself is untouched (not blurred).
            var box = new PixelRect((int)indicator.Bounds.X, (int)indicator.Bounds.Y, (int)size.Width, (int)size.Height);
            var changed = 0;
            for (var i = 0; i < focused.Length; i += 4)
            {
                if (!focused.AsSpan(i, 4).SequenceEqual(unfocused.AsSpan(i, 4)))
                {
                    changed++;
                    var (x, y) = ((i % stride) / 4, i / stride);
                    Assert.False(box.Contains(new PixelPoint(x, y)), $"({x}, {y}) inside the indicator changed with focus.");
                }
            }

            Assert.True(changed > 0, "No focus ring was drawn.");

            // The ring is the theme's focus colour at full strength, one pixel wide, 2 px out from its left edge: a crisp line,
            // not a soft glow (the pixels either side of it are as they were). The frame is RGBA.
            var focus = (Color)Avalonia.Application.Current!.FindResource("State.FocusColor")!;
            var ring = ((((int)indicator.Bounds.Y + 50) * stride) + (((int)indicator.Bounds.X - 3) * 4));
            Assert.Equal(new byte[] { focus.R, focus.G, focus.B, 255 }, focused.AsSpan(ring, 4).ToArray());
            Assert.Equal(unfocused.AsSpan(ring - 4, 4).ToArray(), focused.AsSpan(ring - 4, 4).ToArray());
            Assert.Equal(unfocused.AsSpan(ring + 4, 4).ToArray(), focused.AsSpan(ring + 4, 4).ToArray());
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private const double Origin = 4;

    private static byte[] DrawLevel(SignalLevelMetrics metrics, LedShape shape, SignalDisplayState state) => DrawLevel(metrics, shape, state, out _);

    // The level alone, drawn by a new renderer at 2x on a transparent bitmap.
    private static byte[] DrawLevel(SignalLevelMetrics metrics, LedShape shape, SignalDisplayState state, out int stride)
    {
        var renderer = new SignalIndicatorRenderer { Colors = Colors() };
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)((metrics.Width + 8) * 2), (int)((metrics.Height + 8) * 2)), new Vector(192, 192));
        using (var context = bitmap.CreateDrawingContext())
        {
            renderer.DrawLevel(context, new Point(Origin, Origin), metrics, shape, state, 2);
        }

        renderer.ReleaseLevel();
        stride = bitmap.PixelSize.Width * 4;
        return Pixels(bitmap);
    }

    private static byte[] DrawReadout(int tenths)
    {
        var renderer = new SignalIndicatorRenderer { Colors = Colors() };
        var family = (FontFamily)Avalonia.Application.Current!.FindResource("Font.Mono")!;
        renderer.TextHeight(family, 9);
        using var bitmap = new RenderTargetBitmap(new PixelSize(80, 30), new Vector(192, 192));
        using (var context = bitmap.CreateDrawingContext())
        {
            renderer.DrawReadout(context, 20, 2, tenths, family, 9);
        }

        return Pixels(bitmap);
    }

    private static SignalIndicatorColors Colors() => new(
        Bluestone.Desktop.Theme.Palette.MeterLow,
        Bluestone.Desktop.Theme.Palette.MeterMid,
        Bluestone.Desktop.Theme.Palette.MeterHot,
        Bluestone.Desktop.Theme.Palette.MeterClip,
        Bluestone.Desktop.Theme.Palette.JunctionLamp,
        Bluestone.Desktop.Theme.Palette.TextSecondary);

    private static string Tip(SignalIndicator indicator) => Assert.IsType<string>(ToolTip.GetTip(indicator));

    private static Window Show(SignalIndicator indicator)
    {
        indicator.HorizontalAlignment = HorizontalAlignment.Left;
        indicator.VerticalAlignment = VerticalAlignment.Top;
        var host = new Border { Background = (IBrush)Avalonia.Application.Current!.FindResource("Surface.Base")!, Child = indicator };
        var window = new Window { Width = 160, Height = 320, Content = host };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static WriteableBitmap Capture(Window window)
    {
        window.UpdateLayout();
        return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    }

    private static void Click(Window window, Control control)
    {
        var point = Centre(window, control);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static Point Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");

    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    // The level in dB at a point on a scale of count segments: 10.5 is the middle of segment 10.
    private static double At(int count, double segments) => MeterLevels.FloorDecibels + (segments / count * (MeterLevels.CeilingDecibels - MeterLevels.FloorDecibels));

    private static MeterLevels Levels(double rms, double peak)
    {
        var levels = new MeterLevels();
        levels.Update(Reading(rms, peak), Now);
        return levels;
    }

    private static MeterReading Reading(double rms, double peak) => new(Linear(peak), Linear(peak), Linear(rms), Linear(rms));

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);
}
