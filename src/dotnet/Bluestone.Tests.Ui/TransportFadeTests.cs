using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Bluestone.Desktop.Controls;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The transport's lit keys (<see cref="LedFade"/>) on a hand-run clock: on lights at once; off fades the light out over a
/// key's 60 ms, as a <see cref="SquareKey"/> does; with LED fade off or under Reduce motion off is instant; and the lit
/// and dark end states draw exactly as the single glyph styled lit or dark did.
/// </summary>
public sealed class TransportFadeTests
{
    public static TheoryData<string> Toggles => ["Play", "Record", "Loop", "Metronome", "Pre (count-in)", "Computer keyboard"];

    [Theory]
    [MemberData(nameof(Toggles))]
    public Task TransportToggle_LightsAtOnce_AndFadesOutOver60Ms(string name) => HeadlessApp.RunAsync(async () =>
    {
        using var clock = new ManualLedClock();
        await using var app = await AppUnderTest.OpenAsync();
        Motion(fade: true, reduce: false);
        var key = Toggle(app, name);
        var fade = FadeIn(key);
        key.IsChecked = false;
        app.Settle();
        clock.Frame(500);

        // On: lit at once, with no frame asked for.
        clock.Time = 1000;
        key.IsChecked = true;
        AssertLit(fade);
        Assert.False(fade.Lamp.IsRunning);
        Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(fade.Children[1]));

        // Off: the dark glyph shows at once, the lit one over it fading, eased: half the time leaves a quarter.
        key.IsChecked = false;
        Assert.True(fade.Children[0].IsVisible);
        Assert.Equal(1, fade.Children[1].Opacity);
        Assert.True(fade.Lamp.IsRunning);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(fade.Children[1]));
        clock.Frame(1030);
        Assert.Equal(0.25, fade.Lamp.Level(0), 6);
        Assert.Equal(0.25, fade.Children[1].Opacity);
        Assert.True(fade.Children[1].IsVisible);
        clock.Frame(1059);
        Assert.True(fade.Lamp.Level(0) > 0, "A transport key is still fading 59 ms on.");
        clock.Frame(1060);
        AssertDark(fade);
        Assert.False(fade.Lamp.IsRunning);
    });

    [Theory]
    [MemberData(nameof(Toggles))]
    public Task TransportToggle_WithLedFadeOff_OrUnderReduceMotion_GoesDarkAtOnce(string name) => HeadlessApp.RunAsync(async () =>
    {
        using var clock = new ManualLedClock();
        await using var app = await AppUnderTest.OpenAsync();
        var key = Toggle(app, name);
        var fade = FadeIn(key);

        foreach (var (fades, reduce) in new[] { (false, false), (true, true), (false, true) })
        {
            Motion(fades, reduce);
            key.IsChecked = true;
            AssertLit(fade);
            key.IsChecked = false;
            AssertDark(fade);
            Assert.False(fade.Lamp.IsRunning);
        }
    });

    [Fact]
    public Task Stop_LitWhileStopped_FadesWhenPlaybackStarts() => HeadlessApp.RunAsync(async () =>
    {
        using var clock = new ManualLedClock();
        await using var app = await AppUnderTest.OpenAsync();
        Motion(fade: true, reduce: false);
        var stop = AppUnderTest.Within<Button>(app.Find<Border>("TransportBar"), b => AutomationProperties.GetName(b) == "Stop");
        var fade = FadeIn(stop);
        AssertLit(fade);

        clock.Time = 1000;
        app.ViewModel.PlayPauseCommand.Execute(null);
        await AppUnderTest.WaitUntilAsync(() =>
        {
            app.Playback.Engine.Pump();
            app.Settle();
            return app.ViewModel.IsPlaying;
        }, "playback to start");
        Assert.True(fade.Children[0].IsVisible);
        Assert.True(fade.Lamp.IsRunning);
        clock.Frame(1030);
        Assert.Equal(0.25, fade.Lamp.Level(0), 6);
        clock.Frame(1060);
        AssertDark(fade);

        app.ViewModel.StopCommand.Execute(null);
        await AppUnderTest.WaitUntilAsync(() =>
        {
            app.Playback.Engine.Pump();
            app.Settle();
            return !app.ViewModel.IsPlaying;
        }, "playback to stop");
        Assert.Equal(TransportState.Stopped, app.Playback.Engine.State);
        AssertLit(fade);
    });

    [Fact]
    public Task InputActivityLamp_FadesOverTheLampRelease() => HeadlessApp.RunAsync(async () =>
    {
        using var clock = new ManualLedClock();
        await using var app = await AppUnderTest.OpenAsync();
        Motion(fade: true, reduce: false);
        var lamp = app.Find<LedFade>("InputActivityLamp");
        Assert.Equal(TimeSpan.FromMilliseconds(140), lamp.FadeDuration);

        clock.Time = 1000;
        lamp.IsLit = true;
        AssertLit(lamp);
        lamp.IsLit = false;
        clock.Frame(1070);
        Assert.Equal(0.25, lamp.Lamp.Level(0), 6);
        clock.Frame(1140);
        AssertDark(lamp);
    });

    private static readonly bool[] OffOnOff = [false, true, false];

    public static TheoryData<string> Faces => ["play", "record", "cycle", "mode text"];

    [Theory]
    [MemberData(nameof(Faces))]
    public Task LayeredKey_DrawsItsEndStatesExactlyAsTheSingleGlyphDid(string classes) => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var layered = Key(classes, layered: true);
        var single = Key(classes, layered: false);
        var window = new Window
        {
            Width = 120,
            Height = 60,
            Background = Brushes.Black,
            Content = new GutterPanel { Classes = { "transport" }, Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { layered, single } } },
        };

        // The glyph as it was styled before the fade: lit by the checked key, with the same glow.
        window.Styles.Add(Legacy());
        window.Show();
        try
        {
            // Dark, lit, then dark again after a full fade.
            foreach (var on in OffOnOff)
            {
                clock.Time += 1000;
                layered.IsChecked = on;
                single.IsChecked = on;
                clock.Frame(clock.Time + 60);
                var pixels = Pixels(window);
                Assert.Equal(Crop(pixels, window, single), Crop(pixels, window, layered));
            }
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static void Motion(bool fade, bool reduce)
    {
        LedMotion.Fade = fade;
        LedMotion.ReduceMotion = reduce;
    }

    private static ToggleButton Toggle(AppUnderTest app, string name)
    {
        var key = AppUnderTest.Within<ToggleButton>(app.Find<Border>("TransportBar"), b => AutomationProperties.GetName(b) == name);
        if (name == "Computer keyboard")
        {
            // The app under test has no computer keyboard; give its transport group one, as the app does.
            var group = (Control)key.FindLogicalAncestorOfType<GutterPanel>()!.Parent!;
            group.DataContext = new ComputerKeyboardViewModel(new ComputerKeyboardProvider(SystemMonotonicClock.Instance));
            app.Settle();
            Assert.True(key.IsEffectivelyVisible);
        }

        return key;
    }

    private static LedFade FadeIn(Control key)
    {
        var fade = key.GetVisualDescendants().OfType<LedFade>().Single();
        Assert.Equal(2, fade.Children.Count);
        Assert.Contains("lit", fade.Children[1].Classes);
        return fade;
    }

    private static void AssertLit(LedFade fade)
    {
        Assert.Equal(LedLevels.Steps, fade.Lamp.Step(0));
        Assert.False(fade.Children[0].IsVisible);
        Assert.True(fade.Children[1].IsVisible);
        Assert.Equal(1, fade.Children[1].Opacity);
    }

    private static void AssertDark(LedFade fade)
    {
        Assert.Equal(0, fade.Lamp.Level(0));
        Assert.True(fade.Children[0].IsVisible);
        Assert.False(fade.Children[1].IsVisible);
    }

    private static ToggleButton Key(string classes, bool layered)
    {
        var key = new ToggleButton();
        key.Classes.AddRange(classes.Split(' '));
        if (!layered)
        {
            key.Classes.Add("legacy");
            key.Content = Glyph(classes, lit: false);
            return key;
        }

        var fade = new LedFade { Children = { Glyph(classes, lit: false), Glyph(classes, lit: true) } };
        fade.Bind(LedFade.IsLitProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = key });
        key.Content = fade;
        return key;
    }

    private static Control Glyph(string classes, bool lit)
    {
        Control glyph = classes switch
        {
            "play" => new PathIcon { Data = Icon("Icon.Play"), Width = 14, Height = 14 },
            "record" => new PathIcon { Data = Icon("Icon.Record"), Width = 13, Height = 13 },
            "cycle" => new PathIcon { Classes = { "stroke" }, Data = Icon("Icon.Cycle"), Width = 17, Height = 17 },
            _ => new TextBlock { Text = "Pre" },
        };
        if (lit)
        {
            glyph.Classes.Add("lit");
        }

        return glyph;
    }

    private static Geometry Icon(string key) =>
        (Geometry)Avalonia.Application.Current!.FindResource(key)!;

    private static Styles Legacy()
    {
        var accent = (Color)Avalonia.Application.Current!.FindResource("Accent.Color")!;
        var red = (Color)Avalonia.Application.Current!.FindResource("Transport.RecordForegroundColor")!;
        Style Glow(Func<Selector?, Selector> selector, Color color, double opacity) => new(selector)
        {
            Setters = { new Setter(Visual.EffectProperty, new DropShadowEffect { Color = color, BlurRadius = 8, OffsetX = 0, OffsetY = 0, Opacity = opacity }) },
        };

        return
        [
            Glow(s => s.OfType<ToggleButton>().Class("legacy").Class(":checked").Descendant().OfType<PathIcon>(), accent, 0.55),
            Glow(s => s.OfType<ToggleButton>().Class("legacy").Class("record").Class(":checked").Descendant().OfType<PathIcon>(), red, 0.6),
            Glow(s => s.OfType<ToggleButton>().Class("legacy").Class("text").Class(":checked").Descendant().OfType<TextBlock>(), accent, 0.55),
        ];
    }

    private static byte[] Pixels(Window window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var size = frame.PixelSize;
        var pixels = new byte[size.Width * 4 * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    private static byte[] Crop(byte[] pixels, Window window, Control control)
    {
        var at = control.TranslatePoint(default, window)!.Value;
        var x = (int)Math.Round(at.X);
        var y = (int)Math.Round(at.Y);
        var width = (int)Math.Round(control.Bounds.Width);
        var height = (int)Math.Round(control.Bounds.Height);
        var stride = (int)window.Width * 4;
        var crop = new byte[width * 4 * height];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(pixels, ((y + row) * stride) + (x * 4), crop, row * width * 4, width * 4);
        }

        return crop;
    }
}
