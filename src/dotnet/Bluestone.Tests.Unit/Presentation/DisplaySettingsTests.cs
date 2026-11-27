using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>Settings ▸ Display: application-wide choices of what levels show and the colours they run through, saved as soon as they change.</summary>
public sealed class DisplaySettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-display-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly ThemeDefinition Shipped = ThemeDefinition.DarkSodalite([new("Surface.BaseColor", "#12151A"), new("Accent.Color", "#3CCBF4")]);

    private string File(string name = "display-settings.json") => Path.Combine(_directory, name);

    private string ThemeFile => Path.Combine(_directory, "dark-sodalite.json");

    private ThemeStore Themes() => new(_directory, Shipped);

    // What the theme file holds now (the gradients and the MIDI color are saved there, ADR 0053).
    private ThemeDefinition Saved => Themes().Load().Theme;

    // A view model on a display settings store and the theme, as Bluestone sets it up.
    private DisplayViewModel Display(DisplaySettingsStore store)
    {
        var display = new DisplayViewModel();
        display.Use(store);
        var themes = Themes();
        display.UseTheme(themes, themes.Load().Theme);
        return display;
    }

    [Fact]
    public void TheStore_RoundTrips_AndAMissingDamagedOrUnknownFileGivesTheDefault()
    {
        var store = new DisplaySettingsStore(File());
        Assert.Equal(new DisplaySettings(), store.Load());
        Assert.Equal(JunctionLevelStyle.Peak, store.Load().LevelStyle);
        Assert.Equal(JunctionLevelStyle.Peak, new DisplayViewModel().LevelStyle);

        store.Save(new DisplaySettings { LevelStyle = JunctionLevelStyle.RmsAndPeak });
        Assert.Equal(JunctionLevelStyle.RmsAndPeak, store.Load().LevelStyle);
        Assert.Contains("\"rmsAndPeak\"", System.IO.File.ReadAllText(File()), StringComparison.Ordinal);
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.Equal(JunctionLevelStyle.RmsAndPeak, display.LevelStyle);
        display.LedFade = false;
        Assert.Equal(JunctionLevelStyle.RmsAndPeak, store.Load().LevelStyle);

        System.IO.File.WriteAllText(File(), "{ not json");
        Assert.Equal(new DisplaySettings(), store.Load());
        System.IO.File.WriteAllText(File(), "{ \"levelStyle\": \"loud\" }");
        Assert.Equal(new DisplaySettings(), store.Load());
        System.IO.File.WriteAllText(File(), "{ \"levelStyle\": 7 }");
        Assert.Equal(new DisplaySettings(), store.Load());
    }

    [Fact]
    public void TheViewModel_LoadsWithoutSaving_AndSavesEveryChange()
    {
        var store = new DisplaySettingsStore(File());
        store.Save(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak });
        var written = System.IO.File.GetLastWriteTimeUtc(File());
        var display = new DisplayViewModel();

        display.Use(store);
        Assert.Equal(JunctionLevelStyle.Peak, display.LevelStyle);
        Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(File()));

        display.LevelStyleChoice = DisplayViewModel.LevelStyles.Single(l => l.Style == JunctionLevelStyle.RmsAndPeak);
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.RmsAndPeak }, new DisplaySettingsStore(File()).Load());
    }

    [Fact]
    public void AFailedSave_IsReportedOnce_UntilASaveSucceedsAgain()
    {
        // A file where the settings folder should be: every save fails with an IOException.
        var blocker = Path.Combine(_directory, "blocked");
        System.IO.File.WriteAllText(blocker, string.Empty);
        var display = new DisplayViewModel();
        display.Use(new DisplaySettingsStore(Path.Combine(blocker, "display-settings.json")));
        var reports = new List<string>();
        display.SaveFailed += (_, reason) => reports.Add(reason);

        display.LevelStyle = JunctionLevelStyle.RmsAndPeak;
        display.LedFade = false;
        Assert.Single(reports);
        Assert.Equal(JunctionLevelStyle.RmsAndPeak, display.LevelStyle);

        System.IO.File.Delete(blocker);
        display.LedWarmup = false;
        Assert.Single(reports);
        Assert.False(new DisplaySettingsStore(Path.Combine(blocker, "display-settings.json")).Load().LedWarmup);

        Directory.Delete(blocker, recursive: true);
        System.IO.File.WriteAllText(blocker, string.Empty);
        display.LedWarmup = true;
        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public void OldFiles_WithTheRemovedIndicatorSettings_StillLoad_AndKeepWhatStillApplies()
    {
        // A file from before the gradient, with every indicator key one meter design replaced: they are ignored.
        var store = new DisplaySettingsStore(File());
        System.IO.File.WriteAllText(File(), "{ \"indicatorPosition\": \"top\", \"levelStyle\": \"peak\", \"indicatorSize\": \"large\", \"ledShape\": \"round\", \"largeIndicatorPosition\": \"bottom\" }");
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak }, store.Load());

        // Unknown values in them do not matter either, and saving drops them.
        System.IO.File.WriteAllText(File(), "{ \"indicatorSize\": 7, \"ledShape\": \"star\", \"levelStyle\": \"peak\" }");
        var display = Display(store);
        display.LevelStyle = JunctionLevelStyle.RmsAndPeak;
        var saved = System.IO.File.ReadAllText(File());
        Assert.DoesNotContain("indicatorSize", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("ledShape", saved, StringComparison.Ordinal);
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.RmsAndPeak }, store.Load());
    }

    [Fact]
    public void TheViewModel_EditsTheGradient_AndSavesEveryEdit()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);
        Assert.Equal(SignalGradient.Default, display.Mixer.Gradient);
        var notified = new List<string?>();
        display.Mixer.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // A new stop takes the colour already there, so the gradient looks the same.
        var before = display.Mixer.Gradient.Evaluate(0.2);
        Assert.Equal(2, display.Mixer.AddGradientStop(0.2));
        Assert.Equal(7, display.Mixer.Gradient.Stops.Length);
        Assert.Equal(new SignalGradientStop("#3BD7E2", 0.2), display.Mixer.Gradient.Stops[2]);
        Assert.Equal(before, display.Mixer.Gradient.Evaluate(0.2));
        Assert.Contains(nameof(GradientEditorViewModel.Gradient), notified);
        Assert.Equal(display.Mixer.Gradient, Saved.MixerGradient);

        Assert.True(display.Mixer.SetGradientStopColour(2, "#ff0000"));
        Assert.Equal(new SignalGradientStop("#FF0000", 0.2), Saved.MixerGradient.Stops[2]);
        Assert.False(display.Mixer.SetGradientStopColour(2, "red"));
        Assert.False(display.Mixer.SetGradientStopColour(9, "#FF0000"));

        // Moving past a neighbour reorders; the returned index follows the stop.
        Assert.Equal(4, display.Mixer.MoveGradientStop(2, 0.6));
        Assert.Equal(new SignalGradientStop("#FF0000", 0.6), Saved.MixerGradient.Stops[4]);
        // At a tie it keeps its order relative to the stop already there.
        Assert.Equal(5, display.Mixer.MoveGradientStop(4, 1.5));
        Assert.Equal(new SignalGradientStop("#FF0000", 1), Saved.MixerGradient.Stops[5]);
        Assert.Equal(-1, display.Mixer.MoveGradientStop(7, 0.5));

        Assert.True(display.Mixer.RemoveGradientStop(5));
        Assert.Equal(SignalGradient.Default, Saved.MixerGradient);
        Assert.False(display.Mixer.RemoveGradientStop(-1));

        Assert.Equal(5, display.Mixer.AddGradientStop(0.9));
        Assert.Equal(6, display.Mixer.AddGradientStop(0.95));
        Assert.Equal(-1, display.Mixer.AddGradientStop(0.5));
        Assert.Equal(8, Saved.MixerGradient.Stops.Length);

        display.Mixer.ResetGradientCommand.Execute(null);
        Assert.Equal(SignalGradient.Default, Saved.MixerGradient);

        // Never fewer than two stops; the other preferences are kept alongside.
        display.LevelStyle = JunctionLevelStyle.Peak;
        while (display.Mixer.RemoveGradientStop(0))
        {
        }

        Assert.Equal(2, display.Mixer.Gradient.Stops.Length);
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak }, store.Load());
        Assert.Equal(display.Mixer.Gradient, Saved.MixerGradient);
    }
    [Fact]
    public void TheEditor_AddsInTheWidestGap_RemovesTheSelectedStop_AndKeepsTwo()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);

        // Default's widest gap is purple to pink, so its midpoint is selected.
        display.Mixer.AddGradientStopInLargestGapCommand.Execute(null);
        Assert.Equal(5, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal("#DB45C9", display.Mixer.SelectedGradientStop.Colour);
        Assert.Equal(0.8595, display.Mixer.SelectedGradientStop.Position, precision: 6);
        Assert.Equal("86%", display.Mixer.SelectedGradientStopPercent);
        Assert.Equal(7, Saved.MixerGradient.Stops.Length);

        display.Mixer.AddGradientStopInLargestGapCommand.Execute(null);
        Assert.False(display.Mixer.AddGradientStopInLargestGapCommand.CanExecute(null));

        display.Mixer.SelectedGradientStopIndex = 3;
        var removed = display.Mixer.SelectedGradientStop;
        display.Mixer.RemoveSelectedGradientStopCommand.Execute(null);
        Assert.Equal(2, display.Mixer.SelectedGradientStopIndex);
        Assert.DoesNotContain(removed, Saved.MixerGradient.Stops);
        Assert.True(display.Mixer.AddGradientStopInLargestGapCommand.CanExecute(null));

        var notified = new List<string?>();
        display.Mixer.RemoveSelectedGradientStopCommand.CanExecuteChanged += (_, _) => notified.Add("can remove");
        while (display.Mixer.CanRemoveGradientStop)
        {
            display.Mixer.RemoveSelectedGradientStopCommand.Execute(null);
        }

        Assert.Equal(2, Saved.MixerGradient.Stops.Length);
        Assert.False(display.Mixer.RemoveSelectedGradientStopCommand.CanExecute(null));
        Assert.Contains("can remove", notified);
        Assert.InRange(display.Mixer.SelectedGradientStopIndex, 0, 1);
    }

    [Fact]
    public void TheEditor_NudgesAndRecoloursTheSelectedStop_AndFollowsItWhenItPassesANeighbour()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);

        display.Mixer.NudgeGradientStop(1, 0.01);
        Assert.Equal(1, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(0.146, Saved.MixerGradient.Stops[1].Position);
        Assert.Equal("15%", display.Mixer.SelectedGradientStopPercent);

        // Past the cyan and violet stops: the selection follows the stop to its new place.
        display.Mixer.MoveAndSelectGradientStop(1, 0.7);
        Assert.Equal(3, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(new SignalGradientStop("#3FE0D8", 0.7), display.Mixer.SelectedGradientStop);
        display.Mixer.NudgeGradientStop(3, -0.05);
        Assert.Equal(0.65, Saved.MixerGradient.Stops[3].Position);

        var notified = new List<string?>();
        display.Mixer.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        display.Mixer.SelectedGradientStopColour = "#12";
        Assert.Equal("#3FE0D8", display.Mixer.SelectedGradientStopColour);
        display.Mixer.SelectedGradientStopColour = "ff8800";
        Assert.Equal("#FF8800", Saved.MixerGradient.Stops[3].Colour);
        Assert.Contains(nameof(GradientEditorViewModel.SelectedGradientStopColour), notified);
        display.Mixer.ChooseGradientStopColourCommand.Execute("#7C6BFF");
        Assert.Equal("#7C6BFF", display.Mixer.SelectedGradientStopColour);
        Assert.DoesNotContain("#FF3640", DisplayViewModel.GradientSwatches);

        display.Mixer.ResetGradientCommand.Execute(null);
        Assert.Equal(0, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(SignalGradient.Default, Saved.MixerGradient);
    }

    [Fact]
    public void APreview_AppliesEachEditAtOnce_ButSavesOnlyOnceItEnds()
    {
        // Gradients and the MIDI color are saved in the theme's file, so that is the file a preview holds.
        var path = ThemeFile;
        var display = Display(new DisplaySettingsStore(File()));
        display.LevelStyle = JunctionLevelStyle.RmsAndPeak;
        var before = System.IO.File.ReadAllText(path);
        var notified = 0;
        display.Mixer.PropertyChanged += (_, e) => notified += e.PropertyName == nameof(GradientEditorViewModel.Gradient) ? 1 : 0;

        display.Mixer.BeginGradientPreview();
        display.Mixer.MoveAndSelectGradientStop(1, 0.3);
        display.Mixer.MoveAndSelectGradientStop(1, 0.2);
        Assert.Equal(0.2, display.Mixer.Gradient.Stops[1].Position);
        Assert.Equal(2, notified);
        Assert.Equal(before, System.IO.File.ReadAllText(path));

        display.Mixer.EndGradientPreview();
        Assert.Equal(0.2, Saved.MixerGradient.Stops[1].Position);

        // Ending it again, or a preview with no edits, writes nothing.
        System.IO.File.Delete(path);
        display.Mixer.EndGradientPreview();
        display.Mixer.BeginGradientPreview();
        display.Mixer.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path));

        // Outside a preview every edit saves again.
        display.Mixer.NudgeGradientStop(1, 0.01);
        Assert.Equal(0.21, Saved.MixerGradient.Stops[1].Position);
    }

    [Fact]
    public void Nudges_StopAtTheEndsOfTheScale()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);

        display.Mixer.NudgeGradientStop(0, -0.01);
        display.Mixer.NudgeGradientStop(0, -0.05);
        Assert.Equal(0, Saved.MixerGradient.Stops[0].Position);
        Assert.Equal("0%", display.Mixer.SelectedGradientStopPercent);

        var last = display.Mixer.Gradient.Stops.Length - 1;
        display.Mixer.NudgeGradientStop(last, 0.01);
        display.Mixer.NudgeGradientStop(last, 0.05);
        Assert.Equal(1, Saved.MixerGradient.Stops[last].Position);
        Assert.Equal("100%", display.Mixer.SelectedGradientStopPercent);
    }

    [Fact]
    public void UnreadableValues_FallBackOneByOne()
    {
        var store = new DisplaySettingsStore(File());
        System.IO.File.WriteAllText(File(), "{ \"levelStyle\": \"peak\", \"ledFade\": \"no\", \"ledWarmup\": false }");
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, LedWarmup = false }, store.Load());

        System.IO.File.WriteAllText(File(), "{ \"ledFade\": 0, \"ledWarmup\": null }");
        Assert.Equal(new DisplaySettings(), store.Load());

        System.IO.File.WriteAllText(File(), "[1, 2]");
        Assert.Equal(new DisplaySettings(), store.Load());
    }

    [Fact]
    public void TheThreeGradients_EditIndependently_AndEachResetsToItsOwnDefault()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);
        Assert.Equal([display.Mixer, display.Chain, display.Gain], display.Editors);
        Assert.Equal(SignalGradient.Default, display.Chain.DefaultGradient);
        Assert.Equal(SignalGradient.GainDefault, display.Gain.DefaultGradient);
        Assert.Equal(["#6B4BE0", "#A45CF2", "#E070C8", "#FFA3C4"], display.Gain.Gradient.Stops.Select(s => s.Colour));
        Assert.True(display.Mixer.HasClipLamp);
        Assert.False(display.Gain.HasClipLamp);

        Assert.True(display.Mixer.SetGradientStopColour(0, "#FF0000"));
        display.Chain.MoveAndSelectGradientStop(1, 0.5);
        Assert.Equal(3, display.Gain.AddGradientStop(0.9));

        var saved = Saved;
        Assert.Equal("#FF0000", saved.MixerGradient.Stops[0].Colour);
        Assert.Equal(0.136, saved.MixerGradient.Stops[1].Position);
        Assert.Equal("#B2E0F4", saved.ChainGradient.Stops[0].Colour);
        Assert.Equal(0.5, saved.ChainGradient.Stops[2].Position);
        Assert.Equal(5, saved.GainGradient.Stops.Length);
        Assert.Equal(0, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(2, display.Chain.SelectedGradientStopIndex);

        display.Gain.ResetGradientCommand.Execute(null);
        Assert.Equal(SignalGradient.GainDefault, Saved.GainGradient);
        Assert.Equal("#FF0000", Saved.MixerGradient.Stops[0].Colour);
        display.Mixer.ResetGradientCommand.Execute(null);
        display.Chain.ResetGradientCommand.Execute(null);
        Assert.Equal(Shipped, Saved);
    }

    [Fact]
    public void ADragInAnyGradient_SavesExactlyOnce_WhenTheLastPreviewEnds()
    {
        // Gradients and the MIDI color are saved in the theme's file, so that is the file a preview holds.
        var path = ThemeFile;
        var display = Display(new DisplaySettingsStore(File()));
        System.IO.File.Delete(path);
        Assert.False(System.IO.File.Exists(path), "Loading never saves.");

        display.Chain.BeginGradientPreview();
        display.Chain.MoveAndSelectGradientStop(1, 0.3);
        display.Gain.BeginGradientPreview();
        display.Gain.MoveAndSelectGradientStop(1, 0.5);
        display.Chain.MoveAndSelectGradientStop(1, 0.2);
        display.Chain.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path), "Nothing is written while any preview is open.");

        display.Gain.EndGradientPreview();
        Assert.Equal(0.2, Saved.ChainGradient.Stops[1].Position);
        Assert.Equal(0.5, Saved.GainGradient.Stops[1].Position);

        // That was the only write: ending again, or a preview with no edits, writes nothing.
        System.IO.File.Delete(path);
        display.Gain.EndGradientPreview();
        display.Mixer.BeginGradientPreview();
        display.Mixer.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void TheMidiColour_TakesSixHexDigits_IgnoresAnythingElse_AndSaves()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);
        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        foreach (var invalid in new[] { "red", "#12", "#GGGGGG", "#3CCBF4FF", string.Empty, null })
        {
            display.MidiColour = invalid!;
            Assert.Equal(ThemeDefinition.DefaultMidiColor, display.MidiColour);
        }

        Assert.Empty(notified);
        Assert.False(System.IO.File.Exists(File()));

        display.MidiColour = "ff8800";
        Assert.Equal("#FF8800", display.MidiColour);
        Assert.Equal("#FF8800", Saved.MidiColor);
        Assert.Contains(nameof(DisplayViewModel.MidiColour), notified);

        display.ChooseMidiColourCommand.Execute("#7C6BFF");
        Assert.Equal("#7C6BFF", Saved.MidiColor);
        display.ChooseMidiColourCommand.Execute("blue");
        Assert.Equal("#7C6BFF", display.MidiColour);
    }

    [Fact]
    public void AMidiColourPreview_AppliesEachChangeAtOnce_AppliesWithOneSave_AndCancelLeavesTheOriginal()
    {
        // Gradients and the MIDI color are saved in the theme's file, so that is the file a preview holds.
        var path = ThemeFile;
        var display = Display(new DisplaySettingsStore(File()));
        System.IO.File.Delete(path);
        display.MidiColour = "#7C6BFF";
        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // Apply: every change shows at once, nothing is written until the preview ends, then the final colour once.
        System.IO.File.Delete(path);
        display.BeginMidiColourPreview();
        Assert.True(display.IsMidiColourPreviewing);
        display.MidiColour = "#FF2E93";
        display.MidiColour = "#E0A43A";
        Assert.Equal("#E0A43A", display.MidiColour);
        Assert.Equal(2, notified.Count(n => n == nameof(DisplayViewModel.MidiColour)));
        Assert.False(System.IO.File.Exists(path), "Nothing is written during a preview.");
        display.EndMidiColourPreview();
        Assert.False(display.IsMidiColourPreviewing);
        Assert.Equal("#E0A43A", Saved.MidiColor);

        // That was the only write: ending again, or a preview with no change, writes nothing; a second begin is no nesting.
        System.IO.File.Delete(path);
        display.EndMidiColourPreview();
        display.BeginMidiColourPreview();
        display.BeginMidiColourPreview();
        display.EndMidiColourPreview();
        Assert.False(System.IO.File.Exists(path));

        // Cancel: the picker puts the original back before ending, and the saved colour is the original.
        var original = display.MidiColour;
        display.BeginMidiColourPreview();
        display.MidiColour = "#3D5BFF";
        display.ChooseMidiColourCommand.Execute("#B65CFF");
        Assert.Equal("#B65CFF", display.MidiColour);
        display.MidiColour = original;
        display.EndMidiColourPreview();
        Assert.Equal(original, display.MidiColour);
        Assert.Equal(original, Saved.MidiColor);

        // Outside a preview every change saves again.
        display.MidiColour = "#4CB873";
        Assert.Equal("#4CB873", Saved.MidiColor);
    }

    [Fact]
    public void MidiAndGradientPreviews_HoldEachOthersSaves_AndTheLastToEndWritesBoth()
    {
        // Gradients and the MIDI color are saved in the theme's file, so that is the file a preview holds.
        var path = ThemeFile;
        var display = Display(new DisplaySettingsStore(File()));
        System.IO.File.Delete(path);

        // A gradient drag pending while the MIDI picker is open is kept, and written once the picker closes.
        display.Mixer.BeginGradientPreview();
        display.Mixer.MoveAndSelectGradientStop(1, 0.3);
        display.BeginMidiColourPreview();
        display.Mixer.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path), "The open MIDI preview holds the gradient's save.");
        display.MidiColour = "#FF2E93";
        display.EndMidiColourPreview();
        Assert.Equal(0.3, Saved.MixerGradient.Stops[1].Position);
        Assert.Equal("#FF2E93", Saved.MidiColor);

        // And the other way round: a MIDI change waits for a gradient drag that outlasts it.
        System.IO.File.Delete(path);
        display.BeginMidiColourPreview();
        display.MidiColour = "#3FE0D8";
        display.Chain.BeginGradientPreview();
        display.EndMidiColourPreview();
        display.Chain.MoveAndSelectGradientStop(1, 0.2);
        Assert.False(System.IO.File.Exists(path), "The open gradient preview holds the MIDI colour's save.");
        display.Chain.EndGradientPreview();
        Assert.Equal("#3FE0D8", Saved.MidiColor);
        Assert.Equal(0.2, Saved.ChainGradient.Stops[1].Position);
        Assert.Equal(0.3, Saved.MixerGradient.Stops[1].Position);
    }

    [Fact]
    public void LedFadeAndWarmup_RoundTrip()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);

        display.LedFade = false;
        Assert.Equal(new DisplaySettings { LedFade = false }, store.Load());
        display.LedWarmup = false;
        Assert.Equal(new DisplaySettings { LedFade = false, LedWarmup = false }, store.Load());

        var reloaded = new DisplayViewModel();
        reloaded.Use(store);
        Assert.False(reloaded.LedFade);
        Assert.False(reloaded.LedWarmup);
        reloaded.LedFade = true;
        Assert.Equal(new DisplaySettings { LedWarmup = false }, store.Load());
    }

    [Fact]
    public void LedGlowAndStrength_RoundTrip_AndStrengthIsClamped()
    {
        var store = new DisplaySettingsStore(File());
        var display = Display(store);

        display.LedGlow = false;
        display.GlowStrength = 150;
        Assert.Equal(new DisplaySettings { LedGlow = false, GlowStrength = 150 }, store.Load());

        display.GlowStrength = 500;
        Assert.Equal(DisplaySettings.MaxGlowStrength, display.GlowStrength);
        Assert.Equal(DisplaySettings.MaxGlowStrength, store.Load().GlowStrength);
        display.GlowStrength = -5;
        Assert.Equal(DisplaySettings.MinGlowStrength, display.GlowStrength);

        var reloaded = new DisplayViewModel();
        reloaded.Use(store);
        Assert.False(reloaded.LedGlow);
        Assert.Equal(DisplaySettings.MinGlowStrength, reloaded.GlowStrength);
    }

    [Theory]
    [InlineData("""{ "glowStrength": 900 }""", DisplaySettings.MaxGlowStrength)]
    [InlineData("""{ "glowStrength": "loud" }""", DisplaySettings.DefaultGlowStrength)]
    [InlineData("""{ "glowStrength": 1.5 }""", DisplaySettings.DefaultGlowStrength)]
    public void GlowStrength_LoadsClampedOrDefault(string json, int expected)
    {
        var path = File();
        System.IO.File.WriteAllText(path, json);
        var settings = new DisplaySettingsStore(path).Load();
        Assert.Equal(expected, settings.GlowStrength);
        Assert.True(settings.LedGlow);
    }
}
