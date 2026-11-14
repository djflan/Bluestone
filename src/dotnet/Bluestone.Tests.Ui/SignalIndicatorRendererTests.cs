using Avalonia;
using Avalonia.Automation.Peers;
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
/// The signal indicator family (<see cref="SignalIndicatorRenderer"/> and <see cref="SignalIndicator"/>): what makes a view
/// redraw, how the theme sizes Medium and Large, and the words a tooltip and a screen reader get.
/// </summary>
public sealed class SignalIndicatorRendererTests
{
    private const long Now = 1_000_000_000;

    // Segment counts as the theme sizes each variant: Compact, Medium bars and dots, Large bars and dots.
    public static TheoryData<int> Counts => [26, 33, 22, 66, 33];

    // The variant and shape by name (the enums are internal to the app).
    public static TheoryData<string, string, int> ThemedVariants => new()
    {
        { "Compact", "Bar", 26 },
        { "Medium", "Bar", 33 },
        { "Medium", "Round", 22 },
        { "Large", "Bar", 66 },
        { "Large", "Round", 33 },
    };

    [Theory]
    [MemberData(nameof(Counts))]
    public void State_ChangesOnlyWhenWhatIsDrawnChanges(int count)
    {
        // The RMS at a tenth and three tenths into segment 10 lights the same segments; at seven tenths it lights one more.
        var peak = At(count, 15.5);
        var a = State(count, rms: At(count, 10.1), peak);
        Assert.Equal(a, State(count, rms: At(count, 10.3), peak));
        Assert.Equal(10, a.LitLeft);
        Assert.NotEqual(a, State(count, rms: At(count, 10.7), peak));

        // The held peak moving up a segment, the peak going over full scale, and the clip light all redraw.
        Assert.Equal(15, a.HeldLeft);
        Assert.NotEqual(a, State(count, rms: At(count, 10.1), At(count, 16.5)));
        Assert.NotEqual(a, State(count, rms: At(count, 10.1), 0.5));
        Assert.True(State(count, rms: At(count, 10.1), 0.5).OverLeft);
        Assert.NotEqual(a, State(count, rms: At(count, 10.1), peak, clipLatched: true));

        // Silence is one state, whatever the floor reading.
        Assert.Equal(SignalIndicatorRenderer.State(null, count, JunctionLevelStyle.RmsAndPeak, 0, false, 0), SignalIndicatorRenderer.State(new MeterLevels(), count, JunctionLevelStyle.RmsAndPeak, 0, false, 0));
    }

    [Theory]
    [MemberData(nameof(ThemedVariants))]
    public Task Theme_SizesEachVariantSoZeroDecibelsFallsBetweenSegments(string variantName, string shapeName, int count) => HeadlessApp.RunAsync(() =>
    {
        var variant = Enum.Parse<SignalIndicatorSize>(variantName);
        var shape = Enum.Parse<LedShape>(shapeName);
        var indicator = new SignalIndicator { Variant = variant, LedShape = shape, CarriesNotes = true };
        var window = new Window { Width = 200, Height = 400, Content = indicator };
        window.Show();
        try
        {
            window.UpdateLayout();
            var metrics = indicator.LevelMetrics;
            Assert.Equal(count, metrics.Count);
            Assert.True(metrics.Height <= indicator.LevelHeight, $"{metrics.Height} > {indicator.LevelHeight}");
            if (variant != SignalIndicatorSize.Compact)
            {
                Assert.True(metrics.LandsOnEdge(0), "0 dB is inside a segment.");
                Assert.Equal(shape == LedShape.Round, metrics.BarWidth == metrics.Segment);
            }

            if (variant == SignalIndicatorSize.Large)
            {
                Assert.All(SignalIndicatorRenderer.ScaleMarks, mark => Assert.True(metrics.LandsOnEdge(mark), $"{mark} dB is inside a segment."));
            }

            // It takes its natural size: the level, the lamp, and (Large) the readout and clip light over it.
            Assert.True(indicator.DesiredSize.Height >= metrics.Height + indicator.LampHeight + indicator.LampGap);
            Assert.Equal(variant == SignalIndicatorSize.Large, indicator.Focusable);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void Describe_SaysWhatIsThereInTheSeamsWords()
    {
        var levels = Levels(rms: -20, peak: -12.4);
        Assert.Equal("After Polysynth · notes held · peak -12.4 dB", SignalIndicatorRenderer.Describe("After Polysynth", true, true, true, levels, null, false));
        Assert.Equal("Into the chain · no notes · silent · also sent to Strings", SignalIndicatorRenderer.Describe("Into the chain", true, false, true, new MeterLevels(), "Strings", false));
        Assert.Equal("peak 1.0 dB, clipping · clipped — press Enter or click to clear", SignalIndicatorRenderer.Describe(null, false, false, true, Levels(rms: -3, peak: 1), null, true));
        Assert.Equal("silent", SignalIndicatorRenderer.Describe(null, false, false, true, null, null, false));
        Assert.Equal("+1.2", SignalIndicatorRenderer.ReadoutText(12));
        Assert.Equal("-12.4", SignalIndicatorRenderer.ReadoutText(-124));
        Assert.Equal("—", SignalIndicatorRenderer.ReadoutText(SignalIndicatorRenderer.ReadoutValue(new MeterLevels())));
    }

    [Fact]
    public Task Large_ClearsItsClipLightWithEnter_AndSaysSo() => HeadlessApp.RunAsync(() =>
    {
        var levels = Levels(rms: -3, peak: 1);
        var indicator = new SignalIndicator { Variant = SignalIndicatorSize.Large, Levels = levels };
        var window = new Window { Width = 200, Height = 400, Content = indicator };
        window.Show();
        try
        {
            window.UpdateLayout();
            var peer = ControlAutomationPeer.CreatePeerForElement(indicator);
            Assert.Equal("Signal level", peer.GetName());
            Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
            Assert.Contains("clipped — press Enter or click to clear", peer.GetHelpText(), StringComparison.Ordinal);
            Assert.True(indicator.DisplayState.ClipLatched);

            indicator.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.False(levels.Clipped);
            Assert.False(indicator.DisplayState.ClipLatched);
            Assert.DoesNotContain("clipped", peer.GetHelpText(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task EveryVariantAndShape_Draws() => HeadlessApp.RunAsync(() =>
    {
        var frames = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(frames);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(8) };
        foreach (var (variant, shape) in new[] { (SignalIndicatorSize.Compact, LedShape.Bar), (SignalIndicatorSize.Medium, LedShape.Bar), (SignalIndicatorSize.Medium, LedShape.Round), (SignalIndicatorSize.Large, LedShape.Bar), (SignalIndicatorSize.Large, LedShape.Round) })
        {
            row.Children.Add(new SignalIndicator { Variant = variant, LedShape = shape, CarriesNotes = true, NotesHeld = true, Levels = Levels(rms: -9, peak: 1.2), VerticalAlignment = VerticalAlignment.Top });
        }

        var host = new Border { Background = (IBrush)Avalonia.Application.Current!.FindResource("Surface.Base")!, Child = row, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        try
        {
            window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)host.Bounds.Width * 2, (int)host.Bounds.Height * 2), new Vector(192, 192));
            bitmap.Render(host);
            using (var file = File.Create(Path.Combine(frames, "signal-variants.png")))
            {
                bitmap.Save(file, new PngBitmapEncoderOptions());
            }

            Assert.All(row.Children, child => Assert.True(child.Bounds.Width > 0 && child.Bounds.Height > 0));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    // The level in dB at a point on a scale of count segments: 10.5 is the middle of segment 10.
    private static double At(int count, double segments) => MeterLevels.FloorDecibels + (segments / count * (MeterLevels.CeilingDecibels - MeterLevels.FloorDecibels));

    private static SignalDisplayState State(int count, double rms, double peak, bool clipLatched = false) =>
        SignalIndicatorRenderer.State(Levels(rms, peak), count, JunctionLevelStyle.RmsAndPeak, 0, clipLatched, 0);

    private static MeterLevels Levels(double rms, double peak)
    {
        var levels = new MeterLevels();
        levels.Update(new MeterReading(Linear(peak), Linear(peak), Linear(rms), Linear(rms)), Now);
        return levels;
    }

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);
}
