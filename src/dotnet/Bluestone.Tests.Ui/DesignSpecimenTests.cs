using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The design language's specimen sheets (docs/design-language.md): Dark Sodalite's color roles, the type ramps, and the
/// main controls in their states, drawn by the real controls and styles. Frames go to the test output; with
/// BLUESTONE_WRITE_SPECIMENS=1 they are written to docs/images/design-language/ for the guide (and so are the Settings ▸
/// Display frames that show the theme section, for review).
/// </summary>
public sealed class DesignSpecimenTests : IDisposable
{
    private const double SheetWidth = 1280;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bluestone-specimen", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public Task TheColorRoles_Specimen() => HeadlessApp.RunAsync(() =>
    {
        var sheet = Sheet("Color roles", "Dark Sodalite: every color is a token, named for its role. Views use the brush; code-drawn controls read the same token through Palette.");
        sheet.Children.Add(Group("SURFACES  ·  DARKEST TO LIGHTEST", Swatches("Surface.Sunken", "Console.Well", "Surface.Field", "Console.Bed", "Surface.Base", "Surface.Panel", "Surface.Chrome", "Surface.Raised", "Surface.Overlay")));
        sheet.Children.Add(Group("LINES", Swatches("Line.Divider", "Line.Edge", "Line.Border", "Line.BorderStrong")));
        sheet.Children.Add(Group("TEXT", TextSwatches()));
        sheet.Children.Add(Group("INTERACTION AND STATE", Swatches("State.Hover", "State.Pressed", "State.SelectedSubtle", "State.Selected", "State.Focus", "State.Success", "State.Warning", "State.Error", "State.Offline")));
        sheet.Children.Add(Group("ACCENT, KEYS, AND SIGNAL", Swatches("Accent", "Accent.Hover", "Track.Mute", "Track.Solo", "Console.Clip", "Signal.Note", "Signal.Instrument", "Signal.Audio", "Signal.Other")));
        sheet.Children.Add(Group("TRACK COLORS", TrackSwatches()));
        sheet.Children.Add(Group("GRADIENTS  ·  LIT KEY FACES, BUTTONS, HEADERS, THE SELECTED DEVICE", Swatches("Key.Accent.Face", "Key.Record.Face", "Key.Cycle.Face", "Key.Solo.Face", "Gradient.Button", "Gradient.Header", "DevicePanel.SelectedFill")));
        Assert.True(File.Exists(Render(sheet, "design-specimen-colors")));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheTypeRamps_Specimen() => HeadlessApp.RunAsync(() =>
    {
        var sheet = Sheet("Type", "Two ramps on one family: the interface ramp for panes and settings, and the console ramp for mixer strips and device panels. Numbers that change use the mono face.");
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = Number("Space.XXL") };
        var ui = new StackPanel { Spacing = Number("Space.M") };
        ui.Children.Add(Overline("INTERFACE  ·  Font.Ui"));
        ui.Children.Add(TypeRow("FontSize.Heading", FontWeight.SemiBold, "Display settings", null));
        ui.Children.Add(TypeRow("FontSize.Title", FontWeight.SemiBold, "Mixer meters", null));
        ui.Children.Add(TypeRow("FontSize.Body", FontWeight.Normal, "Choose an indicator to change its colors.", null));
        ui.Children.Add(TypeRow("FontSize.Small", FontWeight.Normal, "100% is the default.", "Text.Secondary"));
        ui.Children.Add(TypeRow("FontSize.Caption", FontWeight.SemiBold, "INDICATOR COLORS", "Text.Muted"));
        ui.Children.Add(TypeRow("FontSize.Micro", FontWeight.Normal, "Bar 17 · Beat 3", "Text.Muted"));
        var console = new StackPanel { Spacing = Number("Space.M") };
        console.Children.Add(Overline("CONSOLE  ·  Font.Ui AND Font.Mono"));
        console.Children.Add(TypeRow("Console.FontSize.Name", FontWeight.SemiBold, "Wavetable lead", null));
        console.Children.Add(TypeRow("Console.FontSize.Value", FontWeight.Normal, "−7.2   −1.6", null, mono: true));
        console.Children.Add(TypeRow("Console.FontSize.Route", FontWeight.Normal, "MASTER  ·  IN 1/2", "Text.Secondary"));
        console.Children.Add(TypeRow("Console.FontSize.Scale", FontWeight.Normal, "+6  0  −6  −12  −24  −48", "Text.Muted", mono: true));
        console.Children.Add(TypeRow("Console.FontSize.Caption", FontWeight.SemiBold, "GAIN  ·  PAN  ·  SENDS", "Text.Muted"));
        console.Children.Add(TypeRow("FontSize.Body", FontWeight.Normal, "17.3.240   120.00 BPM   4/4", "Lcd.Text", mono: true));
        columns.Children.Add(ui);
        Grid.SetColumn(console, 1);
        columns.Children.Add(console);
        sheet.Children.Add(Panel(columns));
        Assert.True(File.Exists(Render(sheet, "design-specimen-type")));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheControls_Specimen() => HeadlessApp.RunAsync(() =>
    {
        var sheet = Sheet("Controls", "The console's own controls and the restyled stock ones, in their states. Lit things glow; unlit ones keep a trace of their color.");

        var keys = Row(
            Labeled("off", Key("M", Palette.Mute, false)),
            Labeled("mute", Key("M", Palette.Mute, true)),
            Labeled("solo", Key("S", Palette.Solo, true)),
            Labeled("arm off", RecordKey(false)),
            Labeled("armed", RecordKey(true)),
            Labeled("power off", new PowerGlyph { IsChecked = false, Bloom = true }),
            Labeled("power on", new PowerGlyph { IsChecked = true, Bloom = true }));
        var textKeys = Row(26,
            Labeled("off", new TextKey { Text = "Audio", Classes = { "paramSwitch" } }),
            Labeled("lit", new TextKey { Text = "Audio", IsChecked = true, Classes = { "paramSwitch" } }),
            Labeled("action", new TextKey { Text = "Reset to default", IsAction = true, Classes = { "action" } }),
            Labeled("disabled", new TextKey { Text = "Remove stop", IsAction = true, IsEnabled = false, Classes = { "action" } }));
        var knobs = Row(80,
            Labeled("knob", KnobCell("Drive", 0.32, "32%")),
            Labeled("knob, high", KnobCell("Tone", 0.78, "4.2 kHz")),
            Labeled("pan, center", new PanKnob { Minimum = -1, Maximum = 1, Value = 0, Width = 40, Height = 30 }),
            Labeled("pan, left", new PanKnob { Minimum = -1, Maximum = 1, Value = -0.42, Width = 40, Height = 30 }));
        var readouts = Row(
            Labeled("gain", Readout("−7.2", peak: false, clipped: false)),
            Labeled("peak", Readout("−1.6", peak: true, clipped: false)),
            Labeled("clipped", Readout("+1.3", peak: true, clipped: true)));
        var tools = Row(Labeled("tool gutter: on, off, off, action", ToolGutter()));

        var left = new StackPanel { Spacing = Number("Space.L") };
        left.Children.Add(Cell("SQUARE KEYS AND POWER", keys));
        left.Children.Add(Cell("TEXT KEYS", textKeys));
        left.Children.Add(Cell("KNOBS", knobs));
        left.Children.Add(Cell("READOUTS IN RECESSED WELLS", readouts));
        left.Children.Add(Cell("GUTTER PANEL WITH TOOL GLYPHS", tools));

        var strips = Row(
            Labeled("strip, playing", Strip("Lead vocal", gain: -3.8, peak: -0.7, rms: -12, selected: false, clipped: false)),
            Labeled("selected", Strip("Lead synth", gain: -7.2, peak: -1.6, rms: -10, selected: true, clipped: false)),
            Labeled("clipped", Strip("Sub 37", gain: 1.5, peak: 1.3, rms: -6, selected: false, clipped: true)));
        var devices = Row(
            Labeled("device", Device("Stone EQ", selected: false)),
            Labeled("selected", Device("Opto", selected: true)),
            Labeled("unavailable", Device("Missing plugin", selected: false, unavailable: true)));

        var middle = new StackPanel { Spacing = Number("Space.L") };
        middle.Children.Add(Cell("FADER ZONE: GAIN LEDS, FADER, LED LADDER METER", strips));
        middle.Children.Add(Cell("DEVICE PANELS", devices));

        var focused = new Button { Content = "Focused", Classes = { "raised" } };
        var buttons = Row(30,
            Labeled("raised", new Button { Content = "Rescan", Classes = { "raised" } }),
            Labeled("hover", Hover(new Button { Content = "Rescan", Classes = { "raised" } })),
            Labeled("ghost", new Button { Content = "Cancel", Classes = { "ghost" } }),
            Labeled("primary", new Button { Content = "Apply", Classes = { "primary" } }),
            Labeled("danger", new Button { Content = "Reset theme", Classes = { "danger" } }),
            Labeled("disabled", new Button { Content = "Rescan", Classes = { "raised" }, IsEnabled = false }),
            Labeled("focus ring", focused));
        var hexInvalid = new TextBox { Text = "#3CCB", Classes = { "hex", "invalid" }, MaxLength = 7 };
        var fields = Row(30,
            Labeled("text box", new TextBox { Text = "Lead vocal", Width = 120 }),
            Labeled("hex", new TextBox { Text = "#3CCBF4", Classes = { "hex" } }),
            Labeled("hex, invalid", hexInvalid),
            Labeled("check box", new CheckBox { Content = "LED glow", IsChecked = true }),
            Labeled("unchecked", new CheckBox { Content = "LED fade", IsChecked = false }));

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = Number("Space.L") };
        top.Children.Add(left);
        Grid.SetColumn(middle, 1);
        top.Children.Add(middle);
        sheet.Children.Add(top);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = Number("Space.L") };
        bottom.Children.Add(Cell("BUTTONS", buttons));
        var fieldsCell = Cell("FIELDS AND CHOICES", fields);
        Grid.SetColumn(fieldsCell, 1);
        bottom.Children.Add(fieldsCell);
        sheet.Children.Add(bottom);

        var window = Window(sheet, scale: 1);
        focused.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(File.Exists(Save(window, "design-specimen-controls")));
        window.Close();

        var big = Window(Detach(sheet), scale: 2);
        focused.Focus(NavigationMethod.Tab);
        big.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(File.Exists(Save(big, "design-specimen-controls@2x")));
        big.Close();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("compact", 1318, 847)]
    [InlineData("normal", 1620, 1000)]
    public Task TheDisplaySettings_ThemeSection_ForReview(string size, int width, int height) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = width;
        app.Window.Height = height;
        var display = app.ViewModel.Display;
        display.Use(new DisplaySettingsStore(Path.Combine(_directory, "display-settings.json")));
        var store = new ThemeStore(Path.Combine(_directory, size, "themes"), TestThemes.Shipped);
        store.Save(store.Defaults with { Colors = store.Defaults.Colors.SetItem("Surface.BaseColor", "#101418") });
        display.UseTheme(store, store.Load().Theme);
        Desktop.App.FollowSignalGradient(display);
        try
        {
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);

            // The chain's indicator, so the MIDI light row and its reset show. The gradients stay as shipped; the saved theme's
            // changed base color (applied only at the next start) is what makes the theme "changed" and Reset theme available.
            app.Click(ColourPickerTests.StageTile(page, "Device chain"));
            display.Chain.SelectedGradientStopIndex = 2;
            Assert.True(display.IsThemeChanged);
            Assert.Equal(ThemeDefinition.DarkSodalite([]).ChainGradient, display.Chain.Gradient);
            app.Window.MouseMove(new Point(4, height - 4), RawInputModifiers.None);
            await app.SettleTransitionsAsync();
            Assert.True(File.Exists(Copy(app.SaveFrame($"settings-display-{size}"))));

            // The MIDI light's row, with its reset beside the color.
            var scroller = page.GetVisualAncestors().OfType<ScrollViewer>().First();
            var midiPanel = AppUnderTest.Within<Control>(page, c => c.Name == "MidiPanel");
            scroller.Offset = new Vector(0, Math.Max(0, midiPanel.TranslatePoint(default, page)!.Value.Y - Number("Space.XXL")));
            app.Settle();
            await app.SettleTransitionsAsync();
            Assert.True(AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetMidiKey").IsEffectivelyVisible);
            Assert.True(File.Exists(Copy(app.SaveFrame($"settings-display-{size}-midi"))));
            scroller.Offset = default;
            app.Settle();

            app.Click(AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetThemeKey"));
            app.Window.MouseMove(new Point(4, height - 4), RawInputModifiers.None);
            await app.SettleTransitionsAsync();
            Assert.True(page.IsConfirmingThemeReset);
            Assert.True(File.Exists(Copy(app.SaveFrame($"settings-display-{size}-confirm"))));

            app.Click(AppUnderTest.Within<Button>(page, b => b.Name == "ConfirmResetThemeButton"));
            app.Window.MouseMove(new Point(4, height - 4), RawInputModifiers.None);
            await app.SettleTransitionsAsync();
            Assert.True(display.IsRestartNeeded);
            Assert.True(File.Exists(Copy(app.SaveFrame($"settings-display-{size}-restart"))));
        }
        finally
        {
            foreach (var editor in display.Editors)
            {
                editor.ResetGradientCommand.Execute(null);
            }

            display.MidiColour = ThemeDefinition.DefaultMidiColor;
        }
    });

    // ---- Sheet and layout ----

    private static StackPanel Sheet(string title, string description)
    {
        var sheet = new StackPanel { Spacing = Number("Space.XXL"), Margin = new Thickness(Number("Space.XXL") + Number("Space.M")), Width = SheetWidth };
        var heading = new StackPanel { Spacing = Number("Space.XS") };
        heading.Children.Add(new TextBlock { Text = "MACHINED & PRECISION MILLED  ·  DARK SODALITE", FontSize = Number("FontSize.Caption"), FontWeight = FontWeight.SemiBold, LetterSpacing = 1.2, Foreground = Brush("Accent") });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brush("Text.Primary") });
        heading.Children.Add(new TextBlock { Text = description, FontSize = Number("FontSize.Body"), Foreground = Brush("Text.Secondary"), TextWrapping = TextWrapping.Wrap, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left });
        sheet.Children.Add(heading);
        sheet.Children.Add(new Border { Height = 1, Background = Brush("Line.Border") });
        return sheet;
    }

    private static StackPanel Group(string title, Control content)
    {
        var group = new StackPanel { Spacing = Number("Space.M") };
        group.Children.Add(Overline(title));
        group.Children.Add(content);
        return group;
    }

    private static TextBlock Overline(string text) => new() { Text = text, Classes = { "overline" } };

    private static Border Panel(Control child) => new()
    {
        Background = Brush("Surface.Panel"),
        BorderBrush = Brush("Line.Divider"),
        BorderThickness = new Thickness(1),
        CornerRadius = Radius("Radius.Panel"),
        Padding = new Thickness(Number("Space.XL")),
        Child = child,
    };

    private static Border Cell(string title, Control content)
    {
        var stack = new StackPanel { Spacing = Number("Space.L") };
        stack.Children.Add(Overline(title));
        stack.Children.Add(content);
        return Panel(stack);
    }

    private static StackPanel Row(params Control[] items) => Row(0, items);

    // Items in a row share one height above their captions, so the captions line up.
    private static StackPanel Row(double height, params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Number("Space.XL") };
        foreach (var item in items)
        {
            if (height > 0 && item is StackPanel { Children: [Panel holder, ..] })
            {
                holder.Height = height;
            }

            row.Children.Add(item);
        }

        return row;
    }

    private static StackPanel Labeled(string caption, Control item)
    {
        var stack = new StackPanel { Spacing = Number("Space.S") };
        var holder = new Panel { MinHeight = 24, Children = { item } };
        item.HorizontalAlignment = HorizontalAlignment.Center;
        item.VerticalAlignment = VerticalAlignment.Center;
        stack.Children.Add(holder);
        stack.Children.Add(new TextBlock { Text = caption, FontSize = Number("FontSize.Caption"), Foreground = Brush("Text.Muted"), HorizontalAlignment = HorizontalAlignment.Center });
        return stack;
    }

    // ---- Colors ----

    private static WrapPanel Swatches(params string[] keys)
    {
        var wrap = new WrapPanel { ItemSpacing = Number("Space.L"), LineSpacing = Number("Space.L") };
        foreach (var key in keys)
        {
            var brush = (IBrush)ThemeResources.Find(key)!;
            wrap.Children.Add(Swatch(key, brush, brush is ISolidColorBrush solid ? ThemeApplier.Hex(solid.Color) : "gradient", null));
        }

        return wrap;
    }

    private static WrapPanel TextSwatches()
    {
        var wrap = new WrapPanel { ItemSpacing = Number("Space.L"), LineSpacing = Number("Space.L") };
        foreach (var key in new[] { "Text.Primary", "Text.Secondary", "Text.Muted", "Text.Disabled" })
        {
            var brush = (ISolidColorBrush)ThemeResources.Find(key)!;
            wrap.Children.Add(Swatch(key, Brush("Surface.Panel"), ThemeApplier.Hex(brush.Color), brush));
        }

        var ink = (ISolidColorBrush)ThemeResources.Find("Text.OnAccent")!;
        wrap.Children.Add(Swatch("Text.OnAccent", Brush("Accent"), ThemeApplier.Hex(ink.Color), ink));
        return wrap;
    }

    private static WrapPanel TrackSwatches()
    {
        var wrap = new WrapPanel { ItemSpacing = Number("Space.L"), LineSpacing = Number("Space.L") };
        for (var i = 0; i < Palette.Tracks.Length; i++)
        {
            wrap.Children.Add(Swatch($"Track.Swatch{i + 1}", new SolidColorBrush(Palette.Tracks[i]), ThemeApplier.Hex(Palette.Tracks[i]), null));
        }

        return wrap;
    }

    private static StackPanel Swatch(string key, IBrush fill, string hex, IBrush? ink)
    {
        var tile = new Border
        {
            Width = 128,
            Height = 56,
            Background = fill,
            BorderBrush = Brush("Line.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = Radius("Radius.Panel"),
            Child = ink is null ? null : new TextBlock { Text = "Aa", FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var stack = new StackPanel { Spacing = Number("Space.XXS"), Width = 128 };
        stack.Children.Add(tile);
        stack.Children.Add(new TextBlock { Text = key, Margin = new Thickness(0, Number("Space.XS"), 0, 0), FontSize = Number("FontSize.Small"), Foreground = Brush("Text.Primary"), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = hex, FontFamily = Font("Font.Mono"), FontSize = Number("FontSize.Caption"), Foreground = Brush("Text.Muted") });
        return stack;
    }

    // ---- Type ----

    private static Grid TypeRow(string sizeKey, FontWeight weight, string sample, string? brushKey, bool mono = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*"), MinHeight = 26 };
        row.Children.Add(new TextBlock
        {
            Text = $"{sizeKey}  {Number(sizeKey).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            FontFamily = Font("Font.Mono"),
            FontSize = Number("FontSize.Caption"),
            Foreground = Brush("Text.Muted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new TextBlock
        {
            Text = sample,
            FontSize = Number(sizeKey),
            FontWeight = weight,
            Foreground = Brush(brushKey ?? "Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
            LetterSpacing = string.Equals(sample.ToUpperInvariant(), sample, StringComparison.Ordinal) && !mono ? 0.6 : 0,
        };
        if (mono)
        {
            text.FontFamily = Font("Font.Mono");
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    // ---- Controls ----

    private static SquareKey Key(string glyph, Color on, bool lit) => new() { Glyph = glyph, OnColor = on, IsChecked = lit, Size = Number("Mixer.KeySize") };

    private static SquareKey RecordKey(bool lit) => new() { Kind = SquareKeyKind.Record, OnColor = Palette.Record, IsChecked = lit, Size = Number("Mixer.KeySize") };

    private static StackPanel KnobCell(string name, double value, string text)
    {
        var cell = new StackPanel { Classes = { "knobCell" } };
        cell.Children.Add(new TextBlock { Text = name, Classes = { "knobLabel" } });
        cell.Children.Add(new Knob { Value = value, Accent = Palette.Accent });
        cell.Children.Add(new TextBlock { Text = text, Classes = { "knobValue" } });
        return cell;
    }

    private static RecessedWell Readout(string value, bool peak, bool clipped)
    {
        var text = new TextBlock { Text = value, Classes = { "mixerValue" } };
        text.Classes.Set("peak", peak);
        text.Classes.Set("clipped", clipped);
        return new RecessedWell { Classes = { "mixerReadout" }, Width = 40, Height = Number("Mixer.ReadoutHeight"), Child = text };
    }

    private static GutterPanel ToolGutter()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new ToggleButton { Classes = { "tool" }, IsChecked = true, Content = Icon("Icon.Inspector") });
        stack.Children.Add(new ToggleButton { Classes = { "tool" }, IsChecked = false, Content = Icon("Icon.Browser") });
        stack.Children.Add(Hover(new ToggleButton { Classes = { "tool" }, IsChecked = false, Content = Icon("Icon.Mixer") }));
        stack.Children.Add(new Button { Classes = { "tool" }, Content = Icon("Icon.Undo") });
        return new GutterPanel { Classes = { "toolGutter" }, Child = stack };
    }

    private static PathIcon Icon(string key) => new() { Classes = { "stroke" }, Data = (Geometry)ThemeResources.Find(key)! };

    private static ConsoleStripPanel Strip(string name, double gain, double peak, double rms, bool selected, bool clipped)
    {
        var rails = new Grid { ColumnDefinitions = new ColumnDefinitions("15,20,4,14,*"), Height = 220 };
        rails.Children.Add(new GainLedGutter { Value = gain, IsHitTestVisible = false });
        var fader = new ConsoleFader { Minimum = Domain.Mixing.AudioStrip.MinGainDecibels, Maximum = Domain.Mixing.AudioStrip.MaxGainDecibels, Value = gain };
        Grid.SetColumn(fader, 1);
        rails.Children.Add(fader);
        var meter = new LedLadderMeter { Mode = MixerMeterMode.PeakRms, Value = rms, PeakHold = peak, IsReady = true, IsClipped = clipped };
        Grid.SetColumn(meter, 3);
        rails.Children.Add(meter);
        var body = new StackPanel { Spacing = Number("Space.S"), Margin = new Thickness(Number("Space.S"), Number("Space.M")) };
        body.Children.Add(new TextBlock { Text = name, FontSize = Number("Console.FontSize.Name"), FontWeight = FontWeight.SemiBold, Foreground = Brush(selected ? "Console.SelectedText" : "Text.Primary"), TextTrimming = TextTrimming.CharacterEllipsis });
        var readouts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,3,*"), Classes = { "mixerReadouts" } };
        readouts.Children.Add(Readout(gain.ToString("+0.0;−0.0", System.Globalization.CultureInfo.InvariantCulture), peak: false, clipped: false));
        var peakReadout = Readout(peak.ToString("+0.0;−0.0", System.Globalization.CultureInfo.InvariantCulture), peak: true, clipped: clipped);
        Grid.SetColumn(peakReadout, 2);
        readouts.Children.Add(peakReadout);
        foreach (var each in readouts.Children)
        {
            each.Width = double.NaN;
        }

        body.Children.Add(readouts);
        body.Children.Add(rails);
        return new ConsoleStripPanel { IsSelected = selected, Width = Number("Mixer.StripWidth"), Child = body };
    }

    private static DeviceCardPanel Device(string name, bool selected, bool unavailable = false)
    {
        var header = new Border { Classes = { "deviceHeader" } };
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        headerRow.Children.Add(new PowerGlyph { Classes = { "devicePower" }, Bloom = true, IsChecked = true });
        var title = new TextBlock { Text = name, Classes = { "deviceName" } };
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(title);
        header.Child = headerRow;
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Number("Space.M")) };
        knobs.Children.Add(KnobCell("Low", 0.42, "−2.5 dB"));
        knobs.Children.Add(KnobCell("High", 0.66, "+3.0 dB"));
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(header);
        Grid.SetRow(knobs, 1);
        layout.Children.Add(knobs);
        return new DeviceCardPanel { IsSelected = selected, IsUnavailable = unavailable, Width = Number("DevicePanel.SmallWidth"), Height = 150, Child = layout };
    }

    private static T Hover<T>(T control)
        where T : Control
    {
        ((IPseudoClasses)control.Classes).Add(":pointerover");
        return control;
    }

    // A control lives in one window: the sheet leaves the closed 1x window before the 2x one takes it.
    private static Control Detach(Control sheet)
    {
        ((LayoutTransformControl)sheet.Parent!).Child = null;
        return sheet;
    }

    // ---- Rendering ----

    private static Window Window(Control sheet, double scale)
    {
        // Sized to the sheet explicitly, once it is styled: a headless window sized to its content stops at the screen's size.
        var host = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = sheet, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 4000, Height = 4000, Background = Brush("Surface.Base"), Content = host };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Width = Math.Ceiling(host.Bounds.Width);
        window.Height = Math.Ceiling(host.Bounds.Height);
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Render(Control sheet, string name)
    {
        var window = Window(sheet, scale: 1);
        try
        {
            return Save(window, name);
        }
        finally
        {
            window.Close();
        }
    }

    private static string Save(Window window, string name)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return Copy(path);
    }

    // With BLUESTONE_WRITE_SPECIMENS=1, the frame is also written to docs/images/design-language/ for the guide.
    private static string Copy(string frame)
    {
        if (Environment.GetEnvironmentVariable("BLUESTONE_WRITE_SPECIMENS") == "1")
        {
            var directory = Repo.File("docs/images/design-language");
            Directory.CreateDirectory(directory);
            File.Copy(frame, Path.Combine(directory, Path.GetFileName(frame)), overwrite: true);
        }

        return frame;
    }

    private static IBrush Brush(string key) => (IBrush)ThemeResources.Find(key)!;

    private static double Number(string key) => (double)ThemeResources.Find(key)!;

    private static CornerRadius Radius(string key) => (CornerRadius)ThemeResources.Find(key)!;

    private static FontFamily Font(string key) => (FontFamily)ThemeResources.Find(key)!;
}
