using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The standalone <see cref="SignalIndicator"/> over its life: when it listens to its levels (only while in a window, and
/// only to the levels it has now), when it redraws (whenever what it shows changes, never when it does not, and never
/// leaving a stale picture behind), and which variants take a click or a key to clear the clip light (only Large).
/// </summary>
public sealed class SignalIndicatorLifecycleTests
{
    private const long Now = 1_000_000_000;

    private static readonly FieldInfo UpdatedField = typeof(MeterLevels).GetField(nameof(MeterLevels.Updated), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MeterLevels.Updated's backing field was not found.");

    private static readonly FieldInfo RendererField = typeof(SignalIndicator).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SignalIndicator._renderer was not found.");

    public static TheoryData<string> Variants => ["Compact", "Medium", "Large"];

    public static TheoryData<string> NotLarge => ["Compact", "Medium"];

    // What changes how it looks without changing what it shows: each colour, the LED shape, and the variant.
    public static TheoryData<string, string> Appearance => new()
    {
        { "Medium", nameof(SignalIndicator.LevelLowColor) },
        { "Medium", nameof(SignalIndicator.LevelClipColor) },
        { "Medium", nameof(SignalIndicator.LampColor) },
        { "Medium", nameof(SignalIndicator.TextColor) },
        { "Medium", nameof(SignalIndicator.LedShape) },
        { "Large", nameof(SignalIndicator.LevelHotColor) },
        { "Large", nameof(SignalIndicator.TextColor) },
        { "Large", nameof(SignalIndicator.LedShape) },
        { "Compact", nameof(SignalIndicator.LevelLowColor) },
        { "Compact", nameof(SignalIndicator.LevelMidColor) },
        { "Compact", nameof(SignalIndicator.Variant) },
    };

    [Fact]
    public Task ListensToItsLevels_OnlyWhileInAWindow_AndOnlyToTheOnesItHasNow() => HeadlessApp.RunAsync(() =>
    {
        var first = Levels(rms: -20, peak: -10);
        var second = Levels(rms: -20, peak: -10);

        // Not in a window: it does not listen, so a meter it is given cannot keep it alive or redraw it.
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Medium, Levels = first };
        Assert.Equal(0, Subscribers(first));
        indicator.Levels = second;
        Assert.Equal(0, Subscribers(first));
        Assert.Equal(0, Subscribers(second));
        indicator.Levels = first;

        var window = new Window { Width = 200, Height = 300, Content = indicator };
        window.Show();
        try
        {
            Assert.Equal(1, Subscribers(first));

            // Given other levels, it lets go of the old ones and listens to the new.
            indicator.Levels = second;
            Assert.Equal(0, Subscribers(first));
            Assert.Equal(1, Subscribers(second));

            // Given the same levels again, it does not listen twice.
            indicator.Levels = second;
            Assert.Equal(1, Subscribers(second));

            // Without levels, it listens to nothing.
            indicator.Levels = null;
            Assert.Equal(0, Subscribers(second));

            // Taken out of the window, it lets go; put back, it listens again (once).
            indicator.Levels = first;
            window.Content = null;
            Assert.Equal(0, Subscribers(first));
            first.Update(Reading(rms: -3, peak: 1), Now);
            window.Content = indicator;
            Assert.Equal(1, Subscribers(first));

            // Levels swapped while out of the window: it listens to neither until it is back, then only the new ones.
            window.Content = null;
            indicator.Levels = second;
            Assert.Equal(0, Subscribers(first));
            Assert.Equal(0, Subscribers(second));
            window.Content = indicator;
            Assert.Equal(0, Subscribers(first));
            Assert.Equal(1, Subscribers(second));
        }
        finally
        {
            window.Close();
        }

        // The window closed: nothing still listens.
        Assert.Equal(0, Subscribers(first));
        Assert.Equal(0, Subscribers(second));
        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Redraws_WhenAnUpdateChangesWhatItShows_AndItsPictureIsNeverStale(string variantName) => HeadlessApp.RunAsync(() =>
    {
        var levels = new MeterLevels();
        var indicator = new SignalIndicator { Variant = Enum.Parse<SignalIndicatorSize>(variantName), Levels = levels, CarriesNotes = true };
        var window = Show(indicator);
        try
        {
            var count = indicator.LevelMetrics.Count;
            var peak = At(count, count * 0.8);
            levels.Update(Reading(rms: At(count, count * 0.3), peak), Now);
            Capture(window).Dispose();

            // Sweep the average up through a few segments in small steps: each step redraws only if what it shows changed,
            // and when it did not redraw, drawing it afresh gives the very same picture.
            var redraws = 0;
            var skips = 0;
            for (var step = 0.3; step < 4.3; step += 0.1)
            {
                var before = indicator.DisplayState;
                var drew = Rendered(indicator, window, () => levels.Update(Reading(rms: At(count, (count * 0.3) + step), peak), Now));
                var changed = before != indicator.DisplayState;
                Assert.True(changed == drew, $"step {step:0.0}: what it shows {(changed ? "changed" : "did not change")}, but it {(drew ? "redrew" : "did not redraw")}.");
                if (drew)
                {
                    redraws++;
                    continue;
                }

                skips++;
                using var kept = Capture(window);
                indicator.InvalidateVisual();
                using var fresh = Capture(window);
                Assert.True(SamePixels(kept, fresh), $"step {step:0.0}: the skipped redraw left a stale picture.");
            }

            Assert.InRange(redraws, 3, 6);
            Assert.True(skips > redraws, $"{skips} skipped, {redraws} redrawn: sub-segment updates should not redraw.");

            // Once silent, it stays silent without redrawing.
            var later = Now + (4 * System.Diagnostics.Stopwatch.Frequency);
            Assert.True(Rendered(indicator, window, () => levels.Update(null, later)));
            Assert.False(Rendered(indicator, window, () => levels.Update(null, later + 1)));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task IgnoresLevelsItNoLongerShows() => HeadlessApp.RunAsync(() =>
    {
        var old = Levels(rms: -40, peak: -30);
        var current = Levels(rms: -40, peak: -30);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Medium, Levels = old };
        var window = Show(indicator);
        try
        {
            Capture(window).Dispose();
            indicator.Levels = current;
            Capture(window).Dispose();

            // The old levels going hot draws nothing; the current ones doing the same redraws.
            Assert.False(Rendered(indicator, window, () => old.Update(Reading(rms: -3, peak: 1), Now)));
            Assert.True(Rendered(indicator, window, () => current.Update(Reading(rms: -3, peak: 1), Now)));

            // Out of the window, an update draws nothing and does not throw.
            window.Content = null;
            current.Update(Reading(rms: -40, peak: -30), Now);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Appearance))]
    public Task ChangingHowItLooks_RedrawsItAsIfNew(string variantName, string property) => HeadlessApp.RunAsync(() =>
    {
        // Fixed size, so a change that resizes what it draws does not get a redraw only by moving its bounds.
        var variant = Enum.Parse<SignalIndicatorSize>(variantName);
        SignalIndicator Make() => new()
        {
            Variant = variant,
            Width = 120,
            Height = 280,
            CarriesNotes = true,
            NotesHeld = true,
            Levels = Levels(rms: -9, peak: 1.2),
        };

        var indicator = Make();
        var window = Show(indicator);
        byte[] before;
        byte[] after;
        try
        {
            using (var frame = Capture(window))
            {
                before = Pixels(frame);
            }

            Change(indicator, property, variant);
            using (var frame = Capture(window))
            {
                after = Pixels(frame);
            }
        }
        finally
        {
            window.Close();
        }

        Assert.False(before.AsSpan().SequenceEqual(after), $"Changing {property} did not redraw it.");

        // And what it drew is what a new indicator with that setting draws: no brush or geometry was kept from before.
        var fresh = Make();
        Change(fresh, property, variant);
        var freshWindow = Show(fresh);
        try
        {
            using var frame = Capture(freshWindow);
            Assert.True(Pixels(frame).AsSpan().SequenceEqual(after), $"After changing {property} it draws differently from a new indicator with it.");
        }
        finally
        {
            freshWindow.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(NotLarge))]
    public Task OnlyLarge_ClearsTheClip_SmallerOnesLeaveItForTheMixer(string variantName) => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -3, peak: 1);
        var indicator = new SignalIndicator { Variant = Enum.Parse<SignalIndicatorSize>(variantName), Levels = levels };
        var window = Show(indicator);
        try
        {
            Assert.False(indicator.Focusable);
            Assert.False(indicator.DisplayState.ClipLatched);
            Assert.DoesNotContain("press Enter", indicator.Describe(), StringComparison.Ordinal);

            // Even given focus by code, Enter and Space do nothing, and neither does a click.
            indicator.Focusable = true;
            indicator.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Click(window, indicator);
            Assert.True(levels.Clipped);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Large_ClearsTheClipWithAClickOrSpace_ButNotWithAModifier() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -3, peak: 1);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = levels };
        var window = Show(indicator);
        try
        {
            Capture(window).Dispose();
            Assert.True(Rendered(indicator, window, () => Click(window, indicator)));
            Assert.False(levels.Clipped);
            Assert.False(indicator.DisplayState.ClipLatched);

            // Clipping again lights it again; a right click leaves it; Ctrl+Enter leaves it; Space clears it.
            levels.Update(Reading(rms: -3, peak: 1), Now);
            Assert.True(indicator.DisplayState.ClipLatched);
            window.MouseDown(Centre(window, indicator), MouseButton.Right);
            window.MouseUp(Centre(window, indicator), MouseButton.Right);
            indicator.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            Assert.True(levels.Clipped);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.False(levels.Clipped);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Lamp_LightsAtOnce_AndLetsGoAtOnceWithoutLevels_OrOverItsReleaseWithThem() => HeadlessApp.RunAsync(async () =>
    {
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Medium, CarriesNotes = true, LampRelease = TimeSpan.FromMilliseconds(20) };
        var window = Show(indicator);
        try
        {
            Capture(window).Dispose();
            Assert.True(Rendered(indicator, window, () => indicator.NotesHeld = true));
            Assert.Equal(1, indicator.DisplayState.Glow);
            Assert.True(Rendered(indicator, window, () => indicator.NotesHeld = false));
            Assert.Equal(0, indicator.DisplayState.Glow);

            // With levels updating, the lamp lets go over its release, not at once, and then stops redrawing.
            var levels = Levels(rms: -40, peak: -30);
            indicator.Levels = levels;
            indicator.NotesHeld = true;
            levels.Update(Reading(rms: -40, peak: -30), Now);
            indicator.NotesHeld = false;
            Assert.Equal(1, indicator.DisplayState.Glow);
            for (var i = 0; i < 50 && indicator.DisplayState.Glow > 0; i++)
            {
                await Task.Delay(5);
                levels.Update(Reading(rms: -40, peak: -30), Now);
            }

            Assert.Equal(0, indicator.DisplayState.Glow);
            Capture(window).Dispose();
            Assert.False(Rendered(indicator, window, () => levels.Update(Reading(rms: -40, peak: -30), Now)));
        }
        finally
        {
            window.Close();
        }
    });

    // Changes one thing about how it looks, to something clearly different.
    private static void Change(SignalIndicator indicator, string property, SignalIndicatorSize variant)
    {
        var magenta = Color.FromRgb(255, 0, 255);
        switch (property)
        {
            case nameof(SignalIndicator.LevelLowColor): indicator.LevelLowColor = magenta; break;
            case nameof(SignalIndicator.LevelMidColor): indicator.LevelMidColor = magenta; break;
            case nameof(SignalIndicator.LevelHotColor): indicator.LevelHotColor = magenta; break;
            case nameof(SignalIndicator.LevelClipColor): indicator.LevelClipColor = magenta; break;
            case nameof(SignalIndicator.LampColor): indicator.LampColor = magenta; break;
            case nameof(SignalIndicator.TextColor): indicator.TextColor = magenta; break;
            case nameof(SignalIndicator.LedShape): indicator.LedShape = LedShape.Round; break;
            case nameof(SignalIndicator.Variant): indicator.Variant = variant == SignalIndicatorSize.Compact ? SignalIndicatorSize.Medium : SignalIndicatorSize.Compact; break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, null);
        }
    }

    // Whether it drew in the frame after act: the renderer's colours are cleared first, and only drawing sets them again.
    private static bool Rendered(SignalIndicator indicator, Window window, Action act)
    {
        var renderer = (SignalIndicatorRenderer)RendererField.GetValue(indicator)!;
        renderer.Colors = default;
        act();
        Capture(window).Dispose();
        var drew = renderer.Colors != default;
        if (!drew)
        {
            // Put the colours back without drawing, so the next frame's picture is unaffected.
            renderer.Colors = new SignalIndicatorColors(indicator.LevelLowColor, indicator.LevelMidColor, indicator.LevelHotColor, indicator.LevelClipColor, indicator.LampColor, indicator.TextColor);
        }

        return drew;
    }

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

    private static bool SamePixels(Bitmap a, Bitmap b) => a.PixelSize == b.PixelSize && Pixels(a).AsSpan().SequenceEqual(Pixels(b));

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

    private static int Subscribers(MeterLevels levels) => (UpdatedField.GetValue(levels) as Delegate)?.GetInvocationList().Length ?? 0;

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
