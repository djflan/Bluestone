using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Themes (ADR 0053): Dark Sodalite is read from Tokens.axaml and App.axaml, a theme replaces every color the application
/// draws with (every token brush, every gradient stop and shadow, the stock controls' palette, and every color the code
/// palette reads), and Settings ▸ Display resets the theme only after asking.
/// </summary>
public sealed partial class ThemeApplierTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bluestone-theme", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Every color the code palette reads, by its field, and the token it reads.</summary>
    internal static readonly ImmutableArray<(string Field, string Token)> PaletteTokens =
    [
        ("Base", "Surface.BaseColor"),
        ("Panel", "Surface.PanelColor"),
        ("Sunken", "Surface.SunkenColor"),
        ("Raised", "Surface.RaisedColor"),
        ("Divider", "Line.DividerColor"),
        ("Lane", "Lane.BackgroundColor"),
        ("LaneAlt", "Lane.AlternateColor"),
        ("LaneSelected", "Lane.SelectedColor"),
        ("LaneDivider", "Lane.DividerColor"),
        ("AutomationLane", "Lane.AutomationColor"),
        ("Ruler", "Lane.RulerColor"),
        ("RulerEdge", "Lane.RulerEdgeColor"),
        ("GridBar", "Grid.BarColor"),
        ("GridBeat", "Grid.BeatColor"),
        ("GridStep", "Grid.StepColor"),
        ("WhiteKeyRow", "PianoRoll.WhiteKeyRowColor"),
        ("BlackKeyRow", "PianoRoll.BlackKeyRowColor"),
        ("OctaveLine", "PianoRoll.OctaveLineColor"),
        ("WhiteKey", "PianoRoll.WhiteKeyColor"),
        ("WhiteKeyPressed", "PianoRoll.WhiteKeyPressedColor"),
        ("BlackKey", "PianoRoll.BlackKeyColor"),
        ("KeyLabel", "PianoRoll.KeyLabelColor"),
        ("Text", "Text.PrimaryColor"),
        ("TextSecondary", "Text.SecondaryColor"),
        ("TextMuted", "Text.MutedColor"),
        ("Accent", "Accent.Color"),
        ("Selection", "Lane.SelectionColor"),
        ("Playhead", "Lane.PlayheadColor"),
        ("Cycle", "Transport.CycleColor"),
        ("Record", "State.ErrorColor"),
        ("Focus", "State.FocusColor"),
        ("Field", "Surface.FieldColor"),
        ("Border", "Line.BorderColor"),
        ("BorderStrong", "Line.BorderStrongColor"),
        ("Edge", "Line.EdgeColor"),
        ("Overlay", "Surface.OverlayColor"),
        ("SignalNote", "Signal.NoteColor"),
        ("SignalInstrument", "Signal.InstrumentColor"),
        ("SignalAudio", "Signal.AudioColor"),
        ("SignalOther", "Signal.OtherColor"),
        ("MeterLow", "Meter.LowColor"),
        ("MeterMid", "Meter.MidColor"),
        ("MeterHot", "Meter.HotColor"),
        ("MeterClip", "Meter.ClipColor"),
        ("MeterTrack", "Meter.TrackColor"),
        ("MixerMeterTrack", "MixerMeter.TrackColor"),
        ("MixerMeterFill", "MixerMeter.FillColor"),
        ("MixerMeterHot", "MixerMeter.HotColor"),
        ("MixerMeterPeakHold", "MixerMeter.PeakHoldColor"),
        ("MixerMeterWarming", "MixerMeter.WarmingColor"),
        ("MixerMeterUnavailable", "MixerMeter.UnavailableColor"),
        ("PanelHeader", "Console.PanelHeaderColor"),
        ("Gutter", "Console.GutterColor"),
        ("Well", "Console.WellColor"),
        ("Key", "Console.KeyColor"),
        ("Sheen", "Console.SheenColor"),
        ("Slot", "Console.SlotColor"),
        ("SelectedText", "Console.SelectedTextColor"),
        ("Mute", "Track.MuteColor"),
        ("Solo", "Track.SoloColor"),
        ("SoloInk", "Console.SoloInkColor"),
        ("Clip", "Console.ClipColor"),
    ];

    /// <summary>The code palette's track colors, in order.</summary>
    internal static readonly ImmutableArray<string> TrackTokens =
        [.. Enumerable.Range(1, 8).Select(i => $"Track.Swatch{i}Color")];

    /// <summary>Palette colors computed from another (kept computed, so they follow the color they are made from).</summary>
    private static readonly ImmutableArray<string> ComputedPaletteFields = ["JunctionLamp"];

    [Fact]
    public Task DarkSodalite_IsEveryColorInTokensAndTheFluentPalette_ReadAtStartup() => HeadlessApp.RunAsync(() =>
    {
        var shipped = Desktop.App.ShippedTheme ?? throw new InvalidOperationException("The app records the shipped theme as it starts.");
        Assert.Equal(ThemeDefinition.DarkSodaliteName, shipped.Name);
        Assert.Equal(Presentation.ThemeVariant.Dark, shipped.Variant);
        Assert.Same(shipped, Desktop.App.AppliedTheme);

        // Every Color resource in Tokens.axaml, as the file holds it, and nothing else outside the Fluent palette.
        var file = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(ThemedTokens.Source);
        var tokens = file.Keys.OfType<string>().Where(k => file[k] is Color).ToDictionary(k => k, k => ThemeApplier.Hex((Color)file[k]!), StringComparer.Ordinal);
        Assert.True(tokens.Count > 150, $"Tokens.axaml has {tokens.Count} colors.");
        Assert.Equal(
            tokens.OrderBy(p => p.Key, StringComparer.Ordinal),
            shipped.Colors.Where(p => !p.Key.StartsWith(ThemeApplier.FluentPrefix, StringComparison.Ordinal)));

        // The stock controls' palette: every color App.axaml sets, as it sets it.
        var palette = PaletteAttribute().Matches(File.ReadAllText(Repo.File("src/dotnet/Bluestone.Desktop/App.axaml")))
            .ToDictionary(m => ThemeApplier.FluentPrefix + m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant(), StringComparer.Ordinal);
        Assert.Equal(20, palette.Count);
        Assert.Equal(
            palette.OrderBy(p => p.Key, StringComparer.Ordinal),
            shipped.Colors.Where(p => p.Key.StartsWith(ThemeApplier.FluentPrefix, StringComparison.Ordinal)));

        // Every color a theme names has the form the theme file uses.
        Assert.All(shipped.Colors.Values, c => Assert.True(ThemeDefinition.IsColor(c), c));
        Assert.All(shipped.Colors.Values, c => Assert.Equal(c.ToUpperInvariant(), c));

        // Shadows write their colors out again (a shadow cannot reference a color): each layer matches its color token.
        foreach (var (key, layers) in ThemeApplier.ShadowSources)
        {
            var shadow = Assert.IsType<BoxShadows>(file[key]);
            Assert.Equal(layers.Length, shadow.Count);
            for (var i = 0; i < layers.Length; i++)
            {
                Assert.True(shadow[i].Color == (Color)file[layers[i]]!, $"{key} layer {i + 1} is not {layers[i]}.");
            }
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ATheme_ReplacesEveryColor_EveryBrushFollowsItsColorInPlace_AndTheCodePaletteReadsIt() => HeadlessApp.RunAsync(() =>
    {
        var tokens = ThemeApplier.Tokens ?? throw new InvalidOperationException("The tokens load with the app.");
        var shipped = Desktop.App.ShippedTheme!;
        var app = Avalonia.Application.Current!;
        var keys = tokens.Keys.OfType<string>().ToList();
        var brushes = keys.Where(k => tokens[k] is IBrush).ToDictionary(k => k, k => tokens[k], StringComparer.Ordinal);
        var original = shipped.Colors.Values.Select(Color.Parse).ToHashSet();

        // A sentinel for every color: distinct from each other and from every shipped color, alpha included.
        var sentinels = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = 0x10203;
        foreach (var key in shipped.Colors.Keys)
        {
            string hex;
            do
            {
                next = (next + 0x050B07) & 0xFFFFFF;
                hex = $"#{next:X6}";
            }
            while (original.Contains(Color.Parse(hex)) || sentinels.ContainsValue(hex));

            sentinels[key] = hex;
        }

        var sentinel = shipped with { Colors = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, sentinels) };
        Color Sentinel(string key) => Color.Parse(sentinels[key]);
        try
        {
            ThemeApplier.Apply(sentinel);

            // Every color token is the theme's.
            Assert.All(keys.Where(k => tokens[k] is Color), k => Assert.True(Sentinel(k) == (Color)tokens[k]!, $"{k} kept its shipped color."));

            // Every token brush is the same object as before (styles hold it), now in its color's sentinel.
            foreach (var (key, brush) in brushes)
            {
                Assert.Same(brush, tokens[key]);
                switch (brush)
                {
                    case SolidColorBrush solid:
                        var source = ThemeApplier.SolidSource(key);
                        Assert.True(sentinels.ContainsKey(source), $"The brush {key} follows {source}, which is not a color token.");
                        Assert.True(Sentinel(source) == solid.Color, $"The brush {key} did not follow {source}.");
                        break;
                    case GradientBrush gradient:
                        Assert.True(ThemeApplier.GradientSources.TryGetValue(key, out var stops), $"The gradient {key} has no color tokens for its stops.");
                        Assert.Equal(stops.Length, gradient.GradientStops.Count);
                        for (var i = 0; i < stops.Length; i++)
                        {
                            Assert.True(Sentinel(stops[i]) == gradient.GradientStops[i].Color, $"{key} stop {i + 1} did not follow {stops[i]}.");
                        }

                        break;
                    default:
                        Assert.Fail($"The brush {key} is a {brush!.GetType().Name}, which a theme does not color.");
                        break;
                }
            }

            // Every shadow, rebuilt in its colors.
            foreach (var key in keys.Where(k => tokens[k] is BoxShadows))
            {
                Assert.True(ThemeApplier.ShadowSources.TryGetValue(key, out var layers), $"The shadow {key} has no color tokens for its layers.");
                var shadow = (BoxShadows)tokens[key]!;
                for (var i = 0; i < layers.Length; i++)
                {
                    Assert.True(Sentinel(layers[i]) == shadow[i].Color, $"{key} layer {i + 1} did not follow {layers[i]}.");
                }
            }

            // Every color the code palette reads is the theme's.
            Assert.All(PaletteTokens.Select(p => p.Token).Concat(TrackTokens), k => Assert.True(Sentinel(k) == ThemeApplier.Token(k), $"Palette reads {k} at its shipped color."));

            // The stock controls' palette.
            var palette = app.Styles.OfType<FluentTheme>().Single().Palettes[Avalonia.Styling.ThemeVariant.Dark];
            foreach (var (name, get, _) in ThemeApplier.FluentColors)
            {
                Assert.True(Sentinel(ThemeApplier.FluentPrefix + name) == get(palette), $"The Fluent palette's {name} kept its shipped color.");
            }
        }
        finally
        {
            ThemeApplier.Apply(shipped);
        }

        // And back: Dark Sodalite as it shipped.
        Assert.Equal(shipped.Colors, ThemeApplier.Colors(tokens, app));
        Assert.All(brushes, b => Assert.Same(b.Value, tokens[b.Key]));
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, app.RequestedThemeVariant);
        return Task.CompletedTask;
    });

    [Fact]
    public void AVariant_RunsUnderTheMatchingAvaloniaScheme()
    {
        Assert.Equal(Avalonia.Styling.ThemeVariant.Light, ThemeApplier.Variant(Presentation.ThemeVariant.Light));
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, ThemeApplier.Variant(Presentation.ThemeVariant.Midtoned));
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, ThemeApplier.Variant(Presentation.ThemeVariant.Dark));
    }

    [Fact]
    public Task TheCodePalette_ReadsEveryColorFromItsToken() => HeadlessApp.RunAsync(() =>
    {
        // Every Color field is listed (or computed from one that is), so a new one cannot skip the theme.
        var fields = typeof(Palette).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Color)).Select(f => f.Name).ToHashSet();
        Assert.Equal(fields.Order(StringComparer.Ordinal), PaletteTokens.Select(p => p.Field).Concat(ComputedPaletteFields).Order(StringComparer.Ordinal));
        foreach (var (field, token) in PaletteTokens)
        {
            Assert.True((Color)typeof(Palette).GetField(field)!.GetValue(null)! == ThemeApplier.Token(token), $"Palette.{field} is not {token}.");
        }

        Assert.Equal(TrackTokens.Select(ThemeApplier.Token), Palette.Tracks);
        Assert.Equal(Palette.Lighten(Palette.MeterLow, 0.25), Palette.JunctionLamp);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ResetTheme_AsksFirst_PutsBackEveryMember_AndSaysWhenColorsWaitForARestart() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        var display = app.ViewModel.Display;
        display.Use(new DisplaySettingsStore(Path.Combine(_directory, "display-settings.json")));

        // The theme as the app read it at startup: one color changed in the file by hand.
        var store = new ThemeStore(Path.Combine(_directory, "themes"), TestThemes.Shipped);
        store.Save(store.Defaults with { Colors = store.Defaults.Colors.SetItem("Accent.Color", "#FF8800") });
        var loaded = store.Load();
        Assert.Null(loaded.Problem);
        display.UseTheme(store, loaded.Theme);
        Desktop.App.FollowSignalGradient(display);
        try
        {
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
            var reset = AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetThemeKey");
            var confirm = AppUnderTest.Within<Border>(page, b => b.Name == "ResetThemeConfirm");
            var note = AppUnderTest.Within<TextBlock>(page, t => t.Name == "RestartNote");
            Assert.Equal("Dark Sodalite", AppUnderTest.Within<TextBlock>(page, t => t.Name == "ThemeNameText").Text);
            Assert.Equal("Theme: Dark Sodalite", AutomationProperties.GetName(AppUnderTest.Within<TextBlock>(page, t => t.Name == "ThemeNameText")));
            Assert.True(reset.IsEffectivelyEnabled, "A changed theme can be reset.");
            Assert.False(confirm.IsVisible);
            Assert.False(note.IsVisible, "The colors in use are the file's: nothing waits for a restart.");

            // Reset asks first; Cancel keeps everything and gives focus back to the key.
            app.Click(reset);
            Assert.True(confirm.IsEffectivelyVisible);
            Assert.True(page.IsConfirmingThemeReset);
            Assert.Equal("Reset Dark Sodalite to its shipped colors, gradients, and MIDI color?", AppUnderTest.Within<TextBlock>(page, t => t.Name == "ResetThemeQuestion").Text);
            var cancel = AppUnderTest.Within<Button>(page, b => b.Name == "CancelResetThemeButton");
            Assert.True(cancel.IsFocused, "Cancel is focused, so Enter keeps the theme.");
            app.Click(cancel);
            Assert.False(confirm.IsVisible);
            Assert.True(reset.IsFocused);
            Assert.Equal("#FF8800", store.Load().Theme.Colors["Accent.Color"]);

            // Escape closes Settings, as everywhere in it, and so leaves the question unanswered: nothing is reset.
            app.Click(reset);
            app.Press(Avalonia.Input.Key.Escape);
            Assert.False(app.Window.Settings.IsOpen);
            Assert.False(confirm.IsVisible);
            Assert.Equal("#FF8800", store.Load().Theme.Colors["Accent.Color"]);
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
            reset = AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetThemeKey");
            confirm = AppUnderTest.Within<Border>(page, b => b.Name == "ResetThemeConfirm");
            note = AppUnderTest.Within<TextBlock>(page, t => t.Name == "RestartNote");
            Assert.False(confirm.IsVisible, "A question left open does not come back with the page.");

            // A gradient and the MIDI color changed as well; confirming puts back every member, in the file and on screen.
            display.Mixer.SetGradientStopColour(0, "#4CB873");
            display.MidiColour = "#E0A43A";
            app.Click(reset);
            app.Click(AppUnderTest.Within<Button>(page, b => b.Name == "ConfirmResetThemeButton"));
            Assert.False(confirm.IsVisible);
            Assert.Equal(store.Defaults, store.Load().Theme);
            Assert.Equal(SignalGradient.Default, display.Mixer.Gradient);
            Assert.Equal(ThemeDefinition.DefaultMidiColor, display.MidiColour);
            Assert.Equal(Color.Parse(ThemeDefinition.DefaultMidiColor), LedColours.Midi);
            Assert.False(display.IsThemeChanged);
            Assert.False(reset.IsEffectivelyEnabled, "A theme as shipped has nothing to reset.");

            // The colors in use are still the file's from startup: the page says they change at the next start.
            Assert.True(display.IsRestartNeeded);
            Assert.True(note.IsEffectivelyVisible);
            Assert.Equal("Restart Bluestone to see the theme's colors.", note.Text);
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

    [Fact]
    public Task GradientAndMidiEdits_LandInTheThemeFile_NotTheDisplaySettings_AndTheMidiColorResets() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        var display = app.ViewModel.Display;
        var settings = Path.Combine(_directory, "display-settings.json");
        display.Use(new DisplaySettingsStore(settings));
        var store = TestThemes.Use(display, _directory);
        Desktop.App.FollowSignalGradient(display);
        try
        {
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
            Assert.False(display.IsThemeChanged);
            Assert.False(AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetThemeKey").IsEffectivelyEnabled);

            // A stop's color, by its swatch: in the theme file, and the theme is now changed (no restart: colors are as they were).
            app.Click(ColourPickerTests.StageTile(page, "Device chain"));
            display.Chain.SelectedGradientStopIndex = 1;
            app.Click(AppUnderTest.Within<Button>(page, b => AutomationProperties.GetName(b) == "Color #E0A43A"));
            var saved = store.Load().Theme;
            Assert.Equal("#E0A43A", saved.ChainGradient.Stops[1].Colour);
            Assert.Equal((SignalGradient.Default, SignalGradient.GainDefault), (saved.MixerGradient, saved.GainGradient));
            Assert.True(display.IsThemeChanged);
            Assert.False(display.IsRestartNeeded);
            Assert.True(AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetThemeKey").IsEffectivelyEnabled);

            // The MIDI color, by its swatch, and back by its reset.
            app.Click(AppUnderTest.Within<Button>(page, b => AutomationProperties.GetName(b) == "MIDI light color #B65CFF"));
            Assert.Equal("#B65CFF", store.Load().Theme.MidiColor);
            var resetMidi = AppUnderTest.Within<TextKey>(page, k => k.Name == "ResetMidiKey");
            Assert.Equal("Reset MIDI light color to default", AutomationProperties.GetName(resetMidi));
            app.Click(resetMidi);
            Assert.Equal(ThemeDefinition.DefaultMidiColor, display.MidiColour);
            Assert.Equal(ThemeDefinition.DefaultMidiColor, store.Load().Theme.MidiColor);
            Assert.False(resetMidi.IsChecked == true, "An action key never stays checked.");

            // The display settings keep only what is not a color.
            var json = File.ReadAllText(settings);
            Assert.DoesNotContain("radient", json, StringComparison.Ordinal);
            Assert.DoesNotContain("idi", json, StringComparison.Ordinal);
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

    [GeneratedRegex("(\\w+)=\"(#[0-9A-Fa-f]{6,8})\"")]
    private static partial Regex PaletteAttribute();
}
