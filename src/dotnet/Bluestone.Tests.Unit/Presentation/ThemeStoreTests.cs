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
    public void AMissingFile_GivesTheDefaults_AndIsWrittenWithNoChanges()
    {
        var loaded = Store().Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(Shipped, loaded.Theme);
        var text = File.ReadAllText(ThemeFile);
        Assert.Contains("\"name\": \"Dark Sodalite\"", text, StringComparison.Ordinal);
        Assert.Contains("\"variant\": \"dark\"", text, StringComparison.Ordinal);
        Assert.Contains("\"colors\": {}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#12151A", text, StringComparison.Ordinal);
        Assert.Equal(["dark-sodalite.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Equal(Shipped, Store().Load().Theme);
    }

    [Fact]
    public void OnlyChangesAreSaved_SoALaterShippedImprovementReachesEveryMemberNobodyChanged()
    {
        Store().Save(Shipped with { Colors = Shipped.Colors.SetItem("Accent.Color", "#FF8800"), ChainGradient = Red });
        var text = File.ReadAllText(ThemeFile);
        Assert.Contains("\"Accent.Color\": \"#FF8800\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Surface.BaseColor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mixer\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("midiColor", text, StringComparison.Ordinal);

        // A later Bluestone ships a lighter base and a new mixer gradient: they arrive; the musician's changes stay.
        var improved = Shipped with { Colors = Shipped.Colors.SetItem("Surface.BaseColor", "#202020"), MixerGradient = Red };
        var loaded = new ThemeStore(_directory, improved).Load();

        Assert.Equal(improved with { Colors = improved.Colors.SetItem("Accent.Color", "#FF8800"), ChainGradient = Red }, loaded.Theme);
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
              "peakMarkerColor": 7,
              "colors": { "Surface.BaseColor": "#010203", "Accent.Color": "aqua", "Made.UpColor": "#FFFFFF", "Overlay.ScrimColor": "#80ffffff", },
              // A comment, and the trailing comma above, don't stop the file being read.
            }
            """);

        var loaded = Store().Load();

        Assert.Equal("7 values in dark-sodalite.json couldn't be used, so they use their shipped color and are removed from the file the next time a theme change is saved: Accent.Color, Made.UpColor, variant, midiColor, peakMarkerColor, gradients.mixer, gradients.gain.", loaded.Problem);
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
    [InlineData("{ \"colors\": { \"Accent.Color\": \"\\uD800\" } }")]
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
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AFileThatCantBeRead_IsNeverWrittenOver()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix file modes to make the file unreadable.");
        File.WriteAllText(ThemeFile, """{ "colors": { "Accent.Color": "#FF8800" } }""");
        File.SetUnixFileMode(ThemeFile, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanRead(ThemeFile), "This user can read a file with no permissions (root).");
            var store = Store();
            var loaded = store.Load();

            Assert.Equal(Shipped, loaded.Theme);
            Assert.Contains("won't be saved over it", loaded.Problem, StringComparison.Ordinal);
            Assert.False(store.CanSave);
            var display = new DisplayViewModel();
            var reports = new List<string>();
            display.ThemeSaveFailed += (_, reason) => reports.Add(reason);
            display.UseTheme(store, loaded.Theme);
            display.Gain.Gradient = Red;

            Assert.Single(reports);
            Assert.Equal(Red, display.Gain.Gradient);
        }
        finally
        {
            File.SetUnixFileMode(ThemeFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal("""{ "colors": { "Accent.Color": "#FF8800" } }""", File.ReadAllText(ThemeFile));
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllText(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void ChangingADisplayPreference_NeverRewritesTheTheme_SoAHandEditSurvives()
    {
        var store = Store();
        var display = new DisplayViewModel();
        display.Use(new DisplaySettingsStore(Path.Combine(_directory, "display-settings.json")));
        display.UseTheme(store, store.Load().Theme);
        File.WriteAllText(ThemeFile, """{ "colors": { "Accent.Color": "#FF8800" } }""");

        display.LedFade = false;
        display.GlowStrength = 150;
        display.LevelStyle = JunctionLevelStyle.RmsAndPeak;

        Assert.Equal("""{ "colors": { "Accent.Color": "#FF8800" } }""", File.ReadAllText(ThemeFile));
    }

    [Fact]
    public void ResetTheme_EndsAnOpenPreview_SoTheResetIsTheLastWord()
    {
        var store = Store();
        var display = new DisplayViewModel();
        display.UseTheme(store, store.Load().Theme);
        display.Mixer.BeginGradientPreview();
        display.Mixer.Gradient = Red;
        display.BeginMidiColourPreview();
        display.MidiColour = "#FF2E93";

        display.ResetThemeCommand.Execute(null);

        Assert.False(display.Mixer.IsPreviewing);
        Assert.False(display.IsMidiColourPreviewing);
        Assert.Equal(Shipped, Store().Load().Theme);
        Assert.Equal(Shipped.MixerGradient, display.Mixer.Gradient);
    }

    [Fact]
    public void ANonObjectGradients_IsReported()
    {
        File.WriteAllText(ThemeFile, """{ "gradients": [] }""");

        var loaded = Store().Load();

        Assert.Equal(Shipped, loaded.Theme);
        Assert.EndsWith(": gradients.", loaded.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AResetThatChangesOnlyTheVariant_StillNeedsARestart()
    {
        Store().Save(Shipped with { Variant = ThemeVariant.Light });
        var store = Store();
        var display = new DisplayViewModel();
        display.UseTheme(store, store.Load().Theme);
        Assert.True(display.IsThemeChanged);

        display.ResetThemeCommand.Execute(null);

        Assert.Equal(Shipped, Store().Load().Theme);
        Assert.True(display.IsRestartNeeded, "The stock controls run under the Light variant until Bluestone starts again.");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AResetThatCantBeSaved_StaysAvailable_AndPromisesNoRestart()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix file modes to make the folder read-only.");
        var changed = Shipped with { Colors = Shipped.Colors.SetItem("Accent.Color", "#FF8800") };
        Store().Save(changed);
        var store = Store();
        var display = new DisplayViewModel();
        var reports = new List<string>();
        display.ThemeSaveFailed += (_, reason) => reports.Add(reason);
        display.UseTheme(store, store.Load().Theme);
        var writable = File.GetUnixFileMode(_directory);
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.SkipWhen(CanWrite(_directory), "This user can write into a read-only folder (root).");
            display.ResetThemeCommand.Execute(null);

            Assert.Single(reports);
            Assert.True(display.IsThemeChanged, "The failed reset can be tried again.");
            Assert.True(display.ResetThemeCommand.CanExecute(null));
            Assert.False(display.IsRestartNeeded, "The file still holds the changed colors.");
        }
        finally
        {
            File.SetUnixFileMode(_directory, writable);
        }

        display.ResetThemeCommand.Execute(null);
        Assert.Equal(Shipped, Store().Load().Theme);
        Assert.False(display.IsThemeChanged);
        Assert.True(display.IsRestartNeeded);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "probe"), string.Empty);
            File.Delete(Path.Combine(directory, "probe"));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
        display.PeakMarkerColour = "#e3c345";

        Assert.Equal(Shipped with { ChainGradient = Red, MidiColor = "#FF2E93", PeakMarkerColor = "#E3C345" }, Store().Load().Theme);
        Assert.True(display.IsThemeChanged);
        Assert.False(display.IsRestartNeeded, "Gradients and the MIDI and peak marker colors apply at once.");

        display.Chain.ResetGradientCommand.Execute(null);
        display.ResetMidiColourCommand.Execute(null);
        Assert.True(display.IsThemeChanged, "The peak marker color is part of the theme.");
        display.ResetPeakMarkerColourCommand.Execute(null);
        Assert.Equal(ThemeDefinition.DefaultPeakMarkerColor, display.PeakMarkerColour);
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
