using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>Themes (ADR 0053): a JSON file of every color, the meter gradients, and the MIDI color, over its shipped defaults.</summary>
public sealed class ThemeStoreTests : IDisposable
{
    private static readonly ThemeDefinition Shipped = ThemeDefinition.DarkSodalite(
    [
        new("Surface.BaseColor", "#12151a"),
        new("Accent.Color", "#3CCBF4"),
        new("Overlay.ScrimColor", "#CC0C0F13"),
    ]);

    private static readonly SignalGradient Red = SignalGradient.Create([new("#000000", 0), new("#FF0000", 1)]);

    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-theme-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ThemeFile => Path.Combine(_directory, "dark-sodalite.json");

    private ThemeStore Store() => new(_directory, Shipped);

    [Fact]
    public void DarkSodalite_IsTheDarkDefault_WithTheShippedGradientsAndMidiColor()
    {
        Assert.Equal("Dark Sodalite", Shipped.Name);
        Assert.Equal(ThemeVariant.Dark, Shipped.Variant);
        Assert.Equal("#12151A", Shipped.Colors["Surface.BaseColor"]);
        Assert.Equal(["#B2E0F4", "#3FE0D8", "#34C6F4", "#7C6BFF", "#B65CFF", "#FF2E93"], Shipped.MixerGradient.Stops.Select(s => s.Colour));
        Assert.Equal([0, 0.136, 0.323, 0.571, 0.719, 1], Shipped.MixerGradient.Stops.Select(s => s.Position));
        Assert.Equal(Shipped.MixerGradient, Shipped.ChainGradient);
        Assert.Equal(["#6B4BE0", "#A45CF2", "#E070C8", "#FFA3C4"], Shipped.GainGradient.Stops.Select(s => s.Colour));
        Assert.Equal("#3CCBF4", Shipped.MidiColor);
        Assert.Equal("dark-sodalite.json", ThemeDefinition.FileName(Shipped.Name));
        Assert.Throws<ArgumentException>(() => ThemeDefinition.DarkSodalite([new("Surface.BaseColor", "slate")]));
    }

    [Fact]
    public void AMissingFile_GivesTheDefaults_AndIsWrittenSoItCanBeEditedByHand()
    {
        var loaded = Store().Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(Shipped, loaded.Theme);
        var text = File.ReadAllText(ThemeFile);
        Assert.Contains("\"name\": \"Dark Sodalite\"", text, StringComparison.Ordinal);
        Assert.Contains("\"variant\": \"dark\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Surface.BaseColor\": \"#12151A\"", text, StringComparison.Ordinal);
        Assert.Contains("\"color\": \"#B2E0F4\"", text, StringComparison.Ordinal);
        Assert.Equal(Shipped, Store().Load().Theme);
    }

    [Fact]
    public void EveryMember_RoundTrips()
    {
        var theme = Shipped with
        {
            Variant = ThemeVariant.Midtoned,
            Colors = Shipped.Colors.SetItem("Accent.Color", "#FF8800"),
            MixerGradient = Red,
            ChainGradient = Red,
            GainGradient = Red,
            MidiColor = "#7C6BFF",
        };

        Store().Save(theme);

        Assert.Equal(theme, Store().Load().Theme);
        Assert.NotEqual(Shipped, theme);
    }

    [Fact]
    public void EachInvalidOrUnknownValue_TakesItsDefault_WithoutResettingTheOthers()
    {
        File.WriteAllText(ThemeFile, """
            {
              "name": "Something else",
              "variant": "sepia",
              "version": 99,
              "gradients": { "mixer": "rainbow", "chain": [{ "color": "#000000", "position": 0 }, { "color": "#ff0000", "position": 1 }], "gain": [{ "colour": "#000000", "position": 0 }] },
              "midiColor": "red",
              "colors": { "Surface.BaseColor": "#010203", "Accent.Color": "aqua", "Made.UpColor": "#FFFFFF", "Overlay.ScrimColor": "#80ffffff" }
            }
            """);

        var loaded = Store().Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(Shipped with
        {
            Colors = Shipped.Colors.SetItem("Surface.BaseColor", "#010203").SetItem("Overlay.ScrimColor", "#80FFFFFF"),
            ChainGradient = Red,
        }, loaded.Theme);
        Assert.False(loaded.Theme.Colors.ContainsKey("Made.UpColor"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void AnUnreadableFile_IsKeptAside_NeverOverwritten_AndTheDefaultsAreUsed(string text)
    {
        File.WriteAllText(ThemeFile, text);

        var loaded = Store().Load();

        Assert.Equal(Shipped, loaded.Theme);
        Assert.Contains("couldn't be read", loaded.Problem, StringComparison.Ordinal);
        var aside = Assert.Single(Directory.GetFiles(_directory, "dark-sodalite.unreadable-*.json"));
        Assert.Equal(text, File.ReadAllText(aside));
        Assert.Contains(Path.GetFileName(aside), loaded.Problem, StringComparison.Ordinal);
        Assert.Equal(Shipped, Store().Load().Theme);
    }

    [Fact]
    public void ResetTheme_PutsEveryMemberBack_AndColorsNeedARestart()
    {
        var changed = Shipped with { Colors = Shipped.Colors.SetItem("Accent.Color", "#FF8800"), GainGradient = Red, MidiColor = "#7C6BFF" };
        var store = Store();
        store.Save(changed);
        var display = new DisplayViewModel();
        display.UseTheme(store, store.Load().Theme);
        Assert.Equal("Dark Sodalite", display.ThemeName);
        Assert.Equal(Red, display.Gain.Gradient);
        Assert.Equal("#7C6BFF", display.MidiColour);
        Assert.True(display.IsThemeChanged);
        Assert.False(display.IsRestartNeeded);

        display.ResetThemeCommand.Execute(null);

        Assert.Equal(Shipped, Store().Load().Theme);
        Assert.Equal(Shipped.GainGradient, display.Gain.Gradient);
        Assert.Equal(Shipped.MidiColor, display.MidiColour);
        Assert.False(display.IsThemeChanged);
        Assert.False(display.ResetThemeCommand.CanExecute(null));
        Assert.True(display.IsRestartNeeded, "The colors in use are the changed ones until Bluestone starts again.");
    }

    [Fact]
    public void GradientAndMidiEdits_AreWrittenIntoTheThemeFile_AndEachRevertsToTheShippedValue()
    {
        var store = Store();
        var display = new DisplayViewModel();
        display.UseTheme(store, store.Load().Theme);
        Assert.False(display.IsThemeChanged);

        display.Chain.Gradient = Red;
        display.MidiColour = "#FF2E93";

        Assert.Equal(Shipped with { ChainGradient = Red, MidiColor = "#FF2E93" }, Store().Load().Theme);
        Assert.True(display.IsThemeChanged);
        Assert.False(display.IsRestartNeeded, "Gradients and the MIDI color apply at once.");

        display.Chain.ResetGradientCommand.Execute(null);
        display.ResetMidiColourCommand.Execute(null);
        Assert.Equal(Shipped, Store().Load().Theme);
        Assert.False(display.IsThemeChanged);
    }

    [Fact]
    public void ColorsInTheOldDisplaySettingsFile_AreIgnored_AndDroppedAtTheNextSave()
    {
        var path = Path.Combine(_directory, "display-settings.json");
        File.WriteAllText(path, """{ "levelStyle": "peak", "mixerGradient": [{ "colour": "#000000", "position": 0 }, { "colour": "#FF0000", "position": 1 }], "midiColour": "#FF8800" }""");
        var display = new DisplayViewModel();
        display.Use(new DisplaySettingsStore(path));
        var store = Store();
        display.UseTheme(store, store.Load().Theme);

        Assert.Equal(JunctionLevelStyle.Peak, display.LevelStyle);
        Assert.Equal(Shipped.MixerGradient, display.Mixer.Gradient);
        Assert.Equal(Shipped.MidiColor, display.MidiColour);

        display.LedFade = false;
        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("mixerGradient", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("midiColour", saved, StringComparison.Ordinal);
    }
}
