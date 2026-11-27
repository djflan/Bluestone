using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

        // The wheel is a color wheel for screen readers, its value the hue and saturation, settable as typed text; the
        // keys move both.
        wheel.Hue = 290;
        wheel.Saturation = 0.6;
        wheel.Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyPress(Key.Up, RawInputModifiers.Shift, PhysicalKey.None, null);
        Assert.Equal((295.0, 0.61), (wheel.Hue, wheel.Saturation));
        var peer = ControlAutomationPeer.CreatePeerForElement(wheel);
        Assert.Equal(AutomationControlType.Custom, peer.GetAutomationControlType());
        Assert.Equal("color wheel", peer.GetLocalizedControlType());
        var value = Assert.IsAssignableFrom<IValueProvider>(peer);
        Assert.Equal("Hue 295°, saturation 61%", value.Value);
        Assert.False(value.IsReadOnly);
        value.SetValue("Hue 200°, saturation 50%");
        Assert.Equal((200.0, 0.5), (wheel.Hue, wheel.Saturation));
        value.SetValue("120, 30");
        Assert.Equal((120.0, 0.3), (wheel.Hue, wheel.Saturation));
        value.SetValue("saturation 80");
        Assert.Equal((120.0, 0.8), (wheel.Hue, wheel.Saturation));
        value.SetValue("hue 390");
        Assert.Equal((30.0, 0.8), (wheel.Hue, wheel.Saturation));
        Assert.Equal("Hue 30°, saturation 80%", value.Value);
        Assert.Throws<ArgumentException>(() => value.SetValue("teal"));
        Assert.Throws<ArgumentException>(() => value.SetValue("hue 10, saturation 140"));
        Assert.Equal((30.0, 0.8), (wheel.Hue, wheel.Saturation));
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

            // A screen reader sets the wheel's value the same way: the stop follows it live.
            var wheelValue = Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(popup.Picker.FindControl<ColourWheel>("Wheel")!));
            wheelValue.SetValue("Hue 200, saturation 50%");
            app.Settle();
            Assert.Equal(200, ColourHsv.From(popup.Picker.Colour).Hue, 0);
            Assert.Equal(ColourHsv.Hex(popup.Picker.Colour), display.Chain.Gradient.Stops[3].Colour);
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
            var host = app.Find<SettingsView>("SettingsHost");
            var insideSettings = app.InWindow(host, new Point(12, 15));
            Assert.False(popup.PickerBounds.Inflate(ColourPickerPopup.Shadow).Contains(insideSettings));
            app.Window.MouseDown(insideSettings, MouseButton.Left);
            app.Window.MouseUp(insideSettings, MouseButton.Left);
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
    [InlineData(true, "Outside")]
    [InlineData(false, "Outside")]
    [InlineData(true, "Done")]
    [InlineData(false, "Done")]
    [InlineData(true, "Parent")]
    [InlineData(false, "Parent")]
    [InlineData(true, "Gear")]
    [InlineData(false, "Gear")]
    [InlineData(true, "Focus")]
    [InlineData(false, "Focus")]
    [InlineData(true, "Window")]
    [InlineData(false, "Window")]
    public Task ClosingSettings_CancelsMidiAndGradientPickerPreviews(bool midi, string close) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (store, path, display, page) = Open(app, 1440);
        try
        {
            app.Click(StageTile(page, "Device chain"));
            var originalMidi = display.MidiColour;
            var originalGradient = display.Chain.Gradient;
            store.Save(store.Load());
            var saved = File.ReadAllText(path);
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == (midi ? "MidiChip" : "StopChip"));
            app.Click(chip);
            var popup = page.ColourPicker;
            app.Click(PickerSwatch(popup, "#E3C345"));
            Assert.True(midi ? display.IsMidiColourPreviewing : display.Chain.IsPreviewing);
            Assert.Equal(saved, File.ReadAllText(path));

            if (close == "Outside")
            {
                Control target = midi
                    ? app.Find<TextBox>("TempoBox")
                    : AppUnderTest.Within<Button>(app.Window, b => AutomationProperties.GetName(b) == "New track");
                var point = app.InWindow(target, new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
                app.Window.MouseDown(point, MouseButton.Left);
                app.Window.MouseUp(point, MouseButton.Left);
                app.Settle();
                if (midi)
                {
                    Assert.Same(target, app.Window.FocusManager?.GetFocusedElement());
                }
                else
                {
                    Assert.Single(app.ViewModel.Tracks);
                }
            }
            else if (close == "Done")
            {
                var done = app.Find<Button>("CloseSettingsButton");
                app.Click(done);
                Assert.True(app.Window.Settings.IsOpen);
                Assert.False(popup.IsOpen);
                app.Click(done);
            }
            else if (close == "Parent")
            {
                app.Window.Settings.Close();
                app.Settle();
            }
            else if (close == "Gear")
            {
                app.Click(app.Find<Avalonia.Controls.Primitives.ToggleButton>("SettingsButton"));
            }
            else if (close == "Focus")
            {
                var timeline = app.Find<TimelineView>("Timeline");
                Assert.True(timeline.Focus(NavigationMethod.Tab));
                app.Settle();
                Assert.True(timeline.IsFocused);
            }
            else
            {
                app.Window.Close();
                app.Settle();
            }

            Assert.False(app.Window.Settings.IsOpen);
            Assert.False(popup.IsOpen);
            Assert.False(display.IsMidiColourPreviewing);
            Assert.False(display.Chain.IsPreviewing);
            Assert.Equal(originalMidi, display.MidiColour);
            Assert.Equal(originalGradient, display.Chain.Gradient);
            Assert.Equal(saved, File.ReadAllText(path));
        }
        finally
        {
            Reset(display);
        }
    });

    [Fact]
    public Task MenuTextEditing_InThePickersHexField_KeepsSettingsAndThePreviewOpen() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
        var (_, _, display, page) = Open(app, 1440);
        try
        {
            var selectAll = NativeMenu.GetMenu(app.Window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit")
                .Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Select All");
            app.Click(StageTile(page, "Device chain"));
            app.Click(AppUnderTest.Within<Button>(page, b => b.Name == "StopChip"));
            var popup = page.ColourPicker;
            app.Click(PickerSwatch(popup, "#E3C345"));
            var hex = popup.Picker.FindControl<TextBox>("HexBox")!;
            Assert.True(hex.Focus());
            app.Settle();

            selectAll.Command!.Execute(null);
            app.Settle();

            Assert.True(app.Window.Settings.IsOpen);
            Assert.True(popup.IsOpen);
            Assert.True(display.Chain.IsPreviewing);
        }
        finally
        {
            Reset(display);
        }
    });

    [Fact]
    public Task CancelledWindowClose_KeepsThePreviewUntilTheWindowReallyCloses() => HeadlessApp.RunAsync(async () =>
    {
        var dialogs = new ClosePrompt();
        await using var app = await AppUnderTest.OpenAsync(dialogs: dialogs);
        app.ViewModel.AddTrackCommand.Execute(null);
        var (store, path, display, page) = Open(app, 1440);
        try
        {
            app.Click(StageTile(page, "Device chain"));
            var original = display.MidiColour;
            store.Save(store.Load());
            var saved = File.ReadAllText(path);
            app.Click(AppUnderTest.Within<Button>(page, b => b.Name == "MidiChip"));
            var popup = page.ColourPicker;
            app.Click(PickerSwatch(popup, "#E3C345"));

            app.Window.Close();
            app.Settle();
            Assert.Equal(1, dialogs.Asked);
            Assert.True(app.Window.IsVisible);
            Assert.True(app.Window.Settings.IsOpen);
            Assert.True(popup.IsOpen);
            Assert.True(display.IsMidiColourPreviewing);
            Assert.Equal("#E3C345", display.MidiColour);
            Assert.Equal(saved, File.ReadAllText(path));

            dialogs.Answer = UnsavedChangesChoice.Discard;
            app.Window.Close();
            app.Settle();
            Assert.False(app.Window.IsVisible);
            Assert.False(app.Window.Settings.IsOpen);
            Assert.False(popup.IsOpen);
            Assert.False(display.IsMidiColourPreviewing);
            Assert.Equal(original, display.MidiColour);
            Assert.Equal(saved, File.ReadAllText(path));
        }
        finally
        {
            dialogs.Answer = UnsavedChangesChoice.Discard;
            page.ColourPicker.Dismiss();
            Reset(display);
        }
    });

    [Fact]
    public Task ClickingThePickerOutsideSettings_DoesNotCloseTheParent() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, _, display, page) = Open(app, 1040, 640);
        try
        {
            // Constrain the host below the picker's height so a generous portion of the picker must extend beyond it.
            var host = app.Find<SettingsView>("SettingsHost");
            host.Height = 300;
            app.Settle();
            app.Click(StageTile(page, "Device chain"));
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == "MidiChip");
            app.Click(chip);
            var popup = page.ColourPicker;
            var hostBounds = new Rect(app.InWindow(host, default), host.Bounds.Size);
            var picker = popup.Picker;
            var top = new Point(popup.PickerBounds.Center.X, popup.PickerBounds.Top + 4);
            var bottom = new Point(popup.PickerBounds.Center.X, popup.PickerBounds.Bottom - 4);
            var exposed = hostBounds.Contains(top) ? bottom : top;
            Assert.True(popup.PickerBounds.Contains(exposed) && !hostBounds.Contains(exposed),
                $"The picker must extend outside Settings: host {hostBounds}, picker {popup.PickerBounds}.");
            var root = TopLevel.GetTopLevel(picker)!;
            var local = new Point(exposed.X - popup.PickerBounds.X, exposed.Y - popup.PickerBounds.Y);
            var point = picker.TranslatePoint(local, root)!.Value;
            root.MouseDown(point, MouseButton.Left);
            root.MouseUp(point, MouseButton.Left);
            app.Settle();

            Assert.True(app.Window.Settings.IsOpen);
            Assert.True(popup.IsOpen);
        }
        finally
        {
            Reset(display);
        }
    });

    [Fact]
    public Task RapidPickerReopen_IgnoresStaleFocusAndRespectsTheUsersFocus() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, _, display, page) = Open(app, 1440);
        try
        {
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == "StopChip");
            var popup = page.ColourPicker;
            var wheel = popup.Picker.FindControl<ColourWheel>("Wheel")!;
            Assert.True(chip.Focus(NavigationMethod.Tab));
            chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(popup.IsOpen);
            popup.Dismiss();
            bool? staleFocus = null;
            Dispatcher.UIThread.Post(() => staleFocus = wheel.IsFocused, DispatcherPriority.Input);
            chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            app.Settle();
            Assert.False(staleFocus);
            Assert.True(wheel.IsFocused);

            popup.Dismiss();
            chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            popup.Dismiss();
            chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(popup.IsOpen);
            var hex = popup.Picker.FindControl<TextBox>("HexBox")!;
            Assert.True(hex.Focus(NavigationMethod.Tab));
            app.Settle();

            Assert.True(app.Window.Settings.IsOpen);
            Assert.True(popup.IsOpen);
            Assert.True(hex.IsFocused);
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

    [Fact]
    public Task ThePicker_FollowsItsChipThroughAResizeAndAScroll_AndCancelsWhenItsChipGoes() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (store, path, display, page) = Open(app, 1440);
        try
        {
            app.Click(StageTile(page, "Device chain"));
            display.Chain.SelectedGradientStopIndex = 3;
            app.Settle();
            var chain = display.Chain.Gradient;
            store.Save(store.Load());
            var saved = File.ReadAllText(path);
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == "StopChip");
            app.Click(chip);
            var popup = page.ColourPicker;
            app.Click(PickerSwatch(popup, "#E3C345"));
            var wide = popup.PickerBounds;

            // Editing inside the picker (a half-typed hex, its own relayout) neither moves nor closes it.
            var hex = popup.Picker.FindControl<TextBox>("HexBox")!;
            hex.Text = "#E3C3";
            app.Settle();
            hex.Text = "#E3C345";
            app.Settle();
            Assert.True(popup.IsOpen);
            Assert.Equal(wide, popup.PickerBounds);

            // The window shrinks to its smallest: the picker stays open, moves with its chip, and stays inside the window.
            app.Window.Width = 1040;
            app.Window.Height = 640;
            app.Settle();
            app.Settle();
            Assert.True(popup.IsOpen);
            Assert.Equal("#E3C345", display.Chain.Gradient.Stops[3].Colour);
            var bounds = popup.PickerBounds;
            var anchor = new Rect(chip.TranslatePoint(default, app.Window)!.Value, chip.Bounds.Size);
            Assert.NotEqual(wide, bounds);
            Assert.True(bounds.Left >= 0 && bounds.Right <= app.Window.ClientSize.Width, $"{bounds} is outside the window's width.");
            Assert.True(bounds.Top >= 30 && bounds.Bottom <= app.Window.ClientSize.Height, $"{bounds} is under the header or below the window.");
            Assert.False(bounds.Intersects(anchor), "The picker covers its own chip.");

            // Esc as ever; then the MIDI light's picker, and the page scrolled as far as it goes: the picker moves with its
            // chip, still beside it and inside the window, and the light keeps its preview.
            app.Press(Key.Escape);
            Assert.Equal(chain, display.Chain.Gradient);
            var midi = display.MidiColour;
            var midiChip = AppUnderTest.Within<Button>(page, b => b.Name == "MidiChip");
            var scroller = midiChip.FindAncestorOfType<ScrollViewer>()!;
            scroller.Offset = default;
            app.Settle();
            app.Click(midiChip);
            app.Click(PickerSwatch(popup, "#E0A43A"));
            var unscrolled = popup.PickerBounds;
            // As far as the page goes while the chip stays in view (further, the chip is gone and the edit is cancelled).
            var chipTop = midiChip.TranslatePoint(default, (Visual)scroller.Content!)!.Value.Y;
            scroller.Offset = new Vector(0, Math.Min(scroller.Extent.Height - scroller.Viewport.Height, chipTop - 40));
            app.Settle();
            app.Settle();
            Assert.True(scroller.Offset.Y > 100, $"The page did not scroll ({scroller.Offset.Y}).");
            Assert.True(popup.IsOpen);
            Assert.Equal("#E0A43A", display.MidiColour);
            bounds = popup.PickerBounds;
            anchor = new Rect(midiChip.TranslatePoint(default, app.Window)!.Value, midiChip.Bounds.Size);
            Assert.NotEqual(unscrolled, bounds);
            Assert.True(bounds.Top >= 30 && bounds.Bottom <= app.Window.ClientSize.Height, $"{bounds} is under the header or below the window.");
            Assert.False(bounds.Intersects(anchor), "The picker covers its own chip.");

            // The chip goes (the page reflows without it): the edit is cancelled, nothing is saved, Settings stays open.
            midiChip.IsVisible = false;
            app.Settle();
            Assert.False(popup.IsOpen);
            Assert.Equal(midi, display.MidiColour);
            Assert.Equal(Color.Parse(midi), LedColours.Midi);
            Assert.False(display.IsMidiColourPreviewing);
            Assert.Equal(chain, display.Chain.Gradient);
            Assert.False(display.Chain.IsPreviewing);
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.True(app.Window.Settings.IsOpen);
            midiChip.IsVisible = true;
        }
        finally
        {
            Reset(display);
        }
    });

    [Fact]
    public Task ReopeningThePicker_SettlesTheLastEditFirst_ForAnotherStop_AndForTheMidiLight() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (store, path, display, page) = Open(app, 1440);
        try
        {
            app.Click(StageTile(page, "Device chain"));
            display.Chain.SelectedGradientStopIndex = 3;
            app.Settle();
            var chain = display.Chain.Gradient;
            var midi = display.MidiColour;
            store.Save(store.Load());
            var saved = File.ReadAllText(path);
            var chip = AppUnderTest.Within<Button>(page, b => b.Name == "StopChip");
            var midiChip = AppUnderTest.Within<Button>(page, b => b.Name == "MidiChip");
            var popup = page.ColourPicker;

            // Stop 3 previews a preset; then, with that picker still open, stop 1's chip opens a picker of its own.
            app.Click(chip);
            app.Click(PickerSwatch(popup, "#E3C345"));
            display.Chain.SelectedGradientStopIndex = 1;
            chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            app.Settle();

            // Stop 3 is back, stop 1's picker starts from stop 1, and its preview is still running.
            Assert.True(popup.IsOpen);
            Assert.Equal(chain, display.Chain.Gradient);
            Assert.Equal(chain.Stops[1].Colour, ColourHsv.Hex(popup.Picker.Original));
            Assert.True(display.Chain.IsPreviewing, "Settling the last edit must not end this one's preview.");
            Assert.Equal(saved, File.ReadAllText(path));

            // Its own cancel restores the settled gradient, not the one stop 3 was previewing.
            app.Click(PickerSwatch(popup, "#E0A43A"));
            Assert.Equal("#E0A43A", display.Chain.Gradient.Stops[1].Colour);
            app.Press(Key.Escape);
            Assert.Equal(chain, display.Chain.Gradient);
            Assert.False(display.Chain.IsPreviewing);
            Assert.Equal(saved, File.ReadAllText(path));

            // From a stop to the MIDI light: the stop is put back, the light previews, and only the light is applied.
            app.Click(chip);
            app.Click(PickerSwatch(popup, "#E3C345"));
            midiChip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            app.Settle();
            Assert.True(popup.IsOpen);
            Assert.Equal(chain, display.Chain.Gradient);
            Assert.False(display.Chain.IsPreviewing);
            Assert.Equal(midi, ColourHsv.Hex(popup.Picker.Original));
            app.Click(PickerSwatch(popup, "#E0A43A"));
            Assert.True(display.IsMidiColourPreviewing);

            // And from the light to itself: the light's last preview is settled before it starts again from the original.
            midiChip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            app.Settle();
            Assert.True(popup.IsOpen);
            Assert.Equal(midi, display.MidiColour);
            Assert.Equal(midi, ColourHsv.Hex(popup.Picker.Original));
            Assert.True(display.IsMidiColourPreviewing);
            app.Click(PickerSwatch(popup, "#E0A43A"));
            app.Click(PickerButton(popup, "Apply color"));
            var after = store.Load();
            Assert.Equal("#E0A43A", after.MidiColour);
            Assert.Equal(chain, after.ChainGradient);
            Assert.False(display.IsMidiColourPreviewing);
        }
        finally
        {
            Reset(display);
        }
    });

    [Fact]
    public Task TheWheelAndFader_KeepNoColourTheyTried_InTheSharedCaches() => HeadlessApp.RunAsync(() =>
    {
        var wheel = new ColourWheel { Width = 172, Height = 172, Hue = 10, Saturation = 0.8 };
        var fader = new BrightnessFader { Width = 24, Height = 172, Hue = 10, Saturation = 0.8, Value = 80 };
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { wheel, fader } } };
        window.Show();
        Render(window);
        var before = SharedCacheSizes();

        // A drag round the wheel and down the fader: dozens of colours drawn, none of them kept for the life of the app.
        for (var i = 1; i <= 36; i++)
        {
            wheel.Hue = fader.Hue = i * 10;
            wheel.Saturation = fader.Saturation = 0.3 + (i % 7 * 0.1);
            wheel.Brightness = (100 - i) / 100.0;
            fader.Value = 100 - i;
            Render(window);
        }

        Assert.Equal(before, SharedCacheSizes());
        window.Close();
        return Task.CompletedTask;

        static void Render(Window window)
        {
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
        }

        static string SharedCacheSizes() => string.Join(", ", new[]
        {
            (typeof(Desktop.Theme.Glow), "Brushes"),
            (typeof(Desktop.Theme.Glow), "Pens"),
            (typeof(ConsolePaint), "SolidBrushes"),
            (typeof(ConsolePaint), "CachedPens"),
            (typeof(ConsolePaint), "Verticals"),
        }.Select(c => $"{c.Item1.Name}.{c.Item2}={((System.Collections.ICollection)c.Item1.GetField(c.Item2, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!).Count}"));
    });

    private static readonly string[] Chips = ["StopChip", "MidiChip"];

    private sealed class ClosePrompt : IUserInteraction
    {
        public UnsavedChangesChoice Answer { get; set; } = UnsavedChangesChoice.Cancel;

        public int Asked { get; private set; }

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName)
        {
            Asked++;
            return Task.FromResult(Answer);
        }
    }

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
