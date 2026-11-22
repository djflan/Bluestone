using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;
using LedColours = Bluestone.Desktop.Theme.LedColours;
using MeterGradient = Bluestone.Desktop.Theme.MeterGradient;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The Display page's color picker: its colors are the ones its wheel and fader show; it previews live on the LEDs but
/// saves only on Apply; Cancel, Esc, and a click away put the original back and leave Settings open; and it opens beside
/// its chip, inside the window, at the smallest and a normal window size.
/// </summary>
public sealed class ColourPickerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bluestone-picker", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public Task TheWheelAndFader_GiveTheColoursTheyShow_AndHexRoundTrips() => HeadlessApp.RunAsync(() =>
    {
        var wheel = new ColourWheel { Width = 172, Height = 172 };
        var fader = new BrightnessFader { Width = 24, Height = 172 };
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { wheel, fader } } };
        window.Show();
        window.UpdateLayout();
        var c = new Point(86, 86);

        // The centre is white; the rim at the top, a third and two thirds round (clockwise) is pure red, green, and blue.
        wheel.SetFromPoint(c);
        Assert.Equal("#FFFFFF", Hex(wheel.Hue, wheel.Saturation, 1));
        wheel.SetFromPoint(wheel.PointFor(0, 1));
        Assert.Equal((0, 1.0), (Math.Round(wheel.Hue) % 360, Math.Round(wheel.Saturation, 3)));
        Assert.Equal("#FF0000", Hex(wheel.Hue, wheel.Saturation, 1));
        wheel.SetFromPoint(wheel.PointFor(120, 1));
        Assert.Equal("#00FF00", Hex(wheel.Hue, wheel.Saturation, 1));
        wheel.SetFromPoint(wheel.PointFor(240, 1) + new Point(0, 30));
        Assert.Equal(1, wheel.Saturation);
        Assert.Equal("#0000FF", Hex(240, wheel.Saturation, 1));
        wheel.SetFromPoint(new Point(86 + 40, 86));
        Assert.Equal(90, wheel.Hue, 3);

        // Brightness darkens: the fader's 50% of white is mid grey, its bottom is black, and black keeps its hue.
        Assert.Equal("#808080", Hex(0, 0, 0.5));
        Assert.Equal("#000000", Hex(200, 1, 0));
        Assert.Equal(200, ColourHsv.From(Colors.Black, 200, 0.7).Hue);
        fader.Focus();
        fader.Value = 100;
        fader.Step(-10, fine: false);
        Assert.Equal(90, fader.Value);

        // Hex: six digits with or without '#', any case; anything else is no colour.
        Assert.Equal("#E162F5", ColourHsv.Hex(ColourHsv.ParseHex("e162f5")!.Value));
        Assert.Equal("#E162F5", ColourHsv.Hex(ColourHsv.From(Color.Parse("#E162F5")).ToColor()));
        Assert.Null(ColourHsv.ParseHex("#E162F"));
        Assert.Null(ColourHsv.ParseHex("#E162FZ"));

        // The wheel is a focusable slider for screen readers, its value the hue and saturation; the keys move both.
        wheel.Hue = 290;
        wheel.Saturation = 0.6;
        wheel.Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyPress(Key.Up, RawInputModifiers.Shift, PhysicalKey.None, null);
        Assert.Equal((295.0, 0.61), (wheel.Hue, wheel.Saturation));
        var peer = ControlAutomationPeer.CreatePeerForElement(wheel);
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        Assert.Equal("Hue 295°, saturation 61%", Assert.IsAssignableFrom<IValueProvider>(peer).Value);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheStopPicker_PreviewsLive_AppliesOnlyThatStop_AndCancelsBackToTheOriginal() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (store, path, display, page) = Open(app, 1440);
        try
        {
            app.Click(StageTile(page, "Device chain"));
            Assert.Same(display.Chain, page.Selected!.Editor);
            display.Chain.SelectedGradientStopIndex = 3;
            app.Settle();
            var mixer = display.Mixer.Gradient;
            var gain = display.Gain.Gradient;
            var chain = display.Chain.Gradient;
            store.Save(store.Load());
            var saved = File.ReadAllText(path);
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == "StopChip");

            // Open: the wheel takes focus and the chip is lit while its picker is open.
            app.Click(chip);
            var popup = page.ColourPicker;
            Assert.True(popup.IsOpen);
            Assert.True(popup.Picker.FindControl<ColourWheel>("Wheel")!.IsFocused);
            Assert.Contains("open", chip.Classes);
            Assert.Equal(chain.Stops[3].Colour, ColourHsv.Hex(popup.Picker.Original));

            // Live: the keys turn the hue; the chain's meters show it at once, nothing else changes, nothing is saved.
            app.Press(Key.Right);
            app.Press(Key.Right);
            var live = ColourHsv.Hex(popup.Picker.Colour);
            Assert.NotEqual(chain.Stops[3].Colour, live);
            Assert.Equal(live, display.Chain.Gradient.Stops[3].Colour);
            Assert.Equal(live, Hex(MeterGradient.Chain.Stops[3].Color));
            Assert.Equal(chain.Stops.Where((_, i) => i != 3), display.Chain.Gradient.Stops.Where((_, i) => i != 3));
            Assert.Equal((mixer, gain), (display.Mixer.Gradient, display.Gain.Gradient));
            Assert.Equal(saved, File.ReadAllText(path));

            // Esc: the original is back, nothing saved, the picker closed, Settings still open, focus on the chip.
            app.Press(Key.Escape);
            Assert.False(popup.IsOpen);
            Assert.Equal(chain, display.Chain.Gradient);
            Assert.Equal(chain.Stops[3].Colour, Hex(MeterGradient.Chain.Stops[3].Color));
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.True(app.Window.Settings.IsOpen, "Esc in the picker must not close Settings.");
            Assert.True(chip.IsFocused);
            Assert.DoesNotContain("open", chip.Classes);

            // Cancel does the same.
            app.Click(chip);
            app.Click(PickerSwatch(popup, "#E3C345"));
            Assert.Equal("#E3C345", display.Chain.Gradient.Stops[3].Colour);
            app.Click(PickerButton(popup, "Cancel color change"));
            Assert.Equal(chain, display.Chain.Gradient);
            Assert.Equal(saved, File.ReadAllText(path));

            // A half-typed hex goes nowhere and Apply waits; six digits preview, and Apply keeps that stop only, saved once.
            app.Click(chip);
            var hex = popup.Picker.FindControl<TextBox>("HexBox")!;
            hex.Text = "#E162F";
            app.Settle();
            Assert.Contains("invalid", hex.Classes);
            Assert.False(PickerButton(popup, "Apply color").IsEffectivelyEnabled);
            Assert.Equal(chain, display.Chain.Gradient);
            hex.Text = "#E162F5";
            app.Settle();
            Assert.DoesNotContain("invalid", hex.Classes);
            Assert.Equal("#E162F5", display.Chain.Gradient.Stops[3].Colour);
            app.Click(PickerButton(popup, "Apply color"));
            Assert.False(popup.IsOpen);
            var after = store.Load();
            Assert.Equal("#E162F5", after.ChainGradient.Stops[3].Colour);
            Assert.Equal(chain.Stops.Where((_, i) => i != 3), after.ChainGradient.Stops.Where((_, i) => i != 3));
            Assert.Equal((mixer, gain), (after.MixerGradient, after.GainGradient));
            Assert.False(display.Chain.IsPreviewing);
        }
        finally
        {
            Reset(display);
        }
    });

    [Fact]
    public Task TheMidiPicker_PreviewsOnTheLights_AndAClickAwayCancels() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (store, path, display, page) = Open(app, 1440);
        try
        {
            app.Click(AppUnderTest.Within<RadioButton>(page, r => AutomationProperties.GetName(r) == "Edit device chain colors"));
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == "MidiChip");
            Assert.True(chip.IsEffectivelyVisible);
            var was = display.MidiColour;
            store.Save(store.Load());
            var saved = File.ReadAllText(path);

            app.Click(chip);
            var popup = page.ColourPicker;
            app.Click(PickerSwatch(popup, "#E0A43A"));
            Assert.Equal("#E0A43A", display.MidiColour);
            Assert.Equal(Color.Parse("#E0A43A"), LedColours.Midi);
            Assert.True(display.IsMidiColourPreviewing);
            Assert.Equal(saved, File.ReadAllText(path));

            // A click away (on the page, outside the picker) puts the original back, saves nothing, and ends the preview.
            app.Window.MouseDown(new Point(1300, 860), MouseButton.Left);
            app.Window.MouseUp(new Point(1300, 860), MouseButton.Left);
            app.Settle();
            Assert.False(popup.IsOpen);
            Assert.Equal(was, display.MidiColour);
            Assert.Equal(Color.Parse(was), LedColours.Midi);
            Assert.False(display.IsMidiColourPreviewing);
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.True(app.Window.Settings.IsOpen);

            // Any color, not just a preset: typed in, then Enter applies it.
            app.Click(chip);
            popup.Picker.FindControl<TextBox>("HexBox")!.Text = "#9C82D9";
            app.Settle();
            popup.Picker.FindControl<ColourWheel>("Wheel")!.Focus();
            app.Press(Key.Enter);
            Assert.False(popup.IsOpen);
            Assert.Equal("#9C82D9", store.Load().MidiColour);
            Assert.False(display.IsMidiColourPreviewing);
            Assert.True(chip.IsFocused);
        }
        finally
        {
            Reset(display);
        }
    });

    [Theory]
    [InlineData(1040, 700)]
    [InlineData(1440, 900)]
    public Task ThePicker_OpensBesideItsChip_InsideTheWindow(double width, double height) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, _, display, page) = Open(app, width, height);
        try
        {
            var frames = Path.Combine(AppContext.BaseDirectory, "frames");
            var w = (int)width;
            Assert.True(File.Exists(app.SaveFrame($"picker-page-plain-{w}")));
            app.Click(StageTile(page, "Gain LEDs"));
            Assert.True(File.Exists(app.SaveFrame($"picker-page-gain-{w}")));
            app.Click(StageTile(page, "Device chain"));
            display.Chain.SelectedGradientStopIndex = 3;
            app.Settle();

            // Chain chosen and focused from the keyboard, the pointer resting on the gain tile.
            app.Press(Key.Right);
            app.Press(Key.Left);
            display.Chain.SelectedGradientStopIndex = 3;
            Assert.Contains(":focus-visible", StageTile(page, "Device chain").Classes);
            var gainTile = StageTile(page, "Gain LEDs");
            app.Window.MouseMove(gainTile.TranslatePoint(new Point(40, 40), app.Window)!.Value, RawInputModifiers.None);
            await app.SettleTransitionsAsync();
            Assert.True(File.Exists(app.SaveFrame($"picker-page-chain-hover-focus-{w}")));
            app.Window.MouseMove(new Point(width - 20, height - 120), RawInputModifiers.None);
            app.Settle();
            foreach (var name in Chips)
            {
                var chip = AppUnderTest.Within<Button>(page, b => b.Name == name);
                app.Click(chip);
                var popup = page.ColourPicker;
                Assert.True(popup.IsOpen);
                var bounds = popup.PickerBounds;
                var anchor = new Rect(chip.TranslatePoint(default, app.Window)!.Value, chip.Bounds.Size);
                Assert.True(bounds.Left >= 0 && bounds.Right <= app.Window.ClientSize.Width, $"{name}: {bounds} is outside the window's width.");
                Assert.True(bounds.Top >= 30 && bounds.Bottom <= app.Window.ClientSize.Height, $"{name}: {bounds} is under the header or below the window.");
                Assert.False(bounds.Intersects(anchor), $"{name}: the picker covers its own chip.");
                Assert.Equal(popup.Picker.DesiredSize, bounds.Size);
                SaveComposite(app, popup, Path.Combine(frames, $"picker-open-{name}-{w}.png"));
                if (name == "StopChip")
                {
                    popup.Picker.FindControl<TextBox>("HexBox")!.Text = "#E162F";
                    app.Settle();
                    SaveComposite(app, popup, Path.Combine(frames, $"picker-open-invalid-{w}.png"));
                }

                app.Press(Key.Escape);
                Assert.False(popup.IsOpen);
            }
        }
        finally
        {
            Reset(display);
        }
    });


    private static readonly string[] Chips = ["StopChip", "MidiChip"];

    private (DisplaySettingsStore Store, string Path, DisplayViewModel Display, DisplaySettingsPage Page) Open(AppUnderTest app, double width, double height = 900)
    {
        app.Window.Width = width;
        app.Window.Height = height;
        var path = Path.Combine(_directory, "display-settings.json");
        var store = new DisplaySettingsStore(path);
        var display = app.ViewModel.Display;
        display.Use(store);
        Desktop.App.FollowSignalGradient(display);
        app.Window.ShowSettings(SettingsPage.Display);
        app.Settle();
        var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
        return (store, path, display, page);
    }

    private static void Reset(DisplayViewModel display)
    {
        foreach (var editor in display.Editors)
        {
            editor.ResetGradientCommand.Execute(null);
        }

        display.MidiColour = DisplaySettings.DefaultMidiColour;
    }

    internal static ListBoxItem StageTile(DisplaySettingsPage page, string title) =>
        AppUnderTest.Within<ListBoxItem>(page, i => i.Content is IndicatorStageItem item && item.Title == title);

    private static Button PickerSwatch(ColourPickerPopup popup, string hex) =>
        popup.Picker.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == $"Color {hex}");

    private static Button PickerButton(ColourPickerPopup popup, string name) =>
        popup.Picker.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    private static string Hex(double hue, double saturation, double brightness) => ColourHsv.Hex(new ColourHsv(hue, saturation, brightness).ToColor());

    private static string Hex(Color colour) => ColourHsv.Hex(colour);

    // The window's frame with the picker drawn where it sits (its own top level or overlay), as a musician sees them together.
    internal static void SaveComposite(AppUnderTest app, ColourPickerPopup popup, string path)
    {
        app.Window.UpdateLayout();
        using var window = app.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var child = popup.Child ?? throw new InvalidOperationException("The picker has no content.");
        var scale = window.PixelSize.Width / app.Window.ClientSize.Width;
        using var picker = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(child.Bounds.Width * scale), (int)Math.Ceiling(child.Bounds.Height * scale)), window.Dpi);
        picker.Render(child);
        var at = popup.PickerBounds.Position - new Point(ColourPickerPopup.Shadow, ColourPickerPopup.Shadow);
        using var composite = new RenderTargetBitmap(window.PixelSize, window.Dpi);
        using (var context = composite.CreateDrawingContext())
        {
            context.DrawImage(window, new Rect(window.Size));
            context.DrawImage(picker, new Rect(at, child.Bounds.Size));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        composite.Save(path, new PngBitmapEncoderOptions());
    }
}
