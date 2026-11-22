using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>Settings ▸ Display: application-wide choices of what levels show and the colours they run through, saved as soon as they change.</summary>
public sealed class DisplaySettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-display-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string name = "display-settings.json") => Path.Combine(_directory, name);

    [Fact]
    public void TheStore_RoundTrips_AndAMissingDamagedOrUnknownFileGivesTheDefault()
    {
        var store = new DisplaySettingsStore(File());
        Assert.Equal(new DisplaySettings(), store.Load());

        store.Save(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak });
        Assert.Equal(JunctionLevelStyle.Peak, store.Load().LevelStyle);
        Assert.Contains("\"peak\"", System.IO.File.ReadAllText(File()), StringComparison.Ordinal);

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
        Assert.Equal(new DisplaySettings(), new DisplaySettingsStore(File()).Load());
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

        display.LevelStyle = JunctionLevelStyle.Peak;
        display.LedFade = false;
        Assert.Single(reports);
        Assert.Equal(JunctionLevelStyle.Peak, display.LevelStyle);

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
        var display = new DisplayViewModel();
        display.Use(store);
        display.LevelStyle = JunctionLevelStyle.RmsAndPeak;
        var saved = System.IO.File.ReadAllText(File());
        Assert.DoesNotContain("indicatorSize", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("ledShape", saved, StringComparison.Ordinal);
        Assert.Equal(new DisplaySettings(), store.Load());
    }

    [Fact]
    public void TheGradient_RoundTrips_AndAnInvalidOneGivesTheDefaultWithoutResettingTheOthers()
    {
        var store = new DisplaySettingsStore(File());
        Assert.Equal(SignalGradient.Default, store.Load().MixerGradient);

        var gradient = SignalGradient.Create([new("#000000", 0), new("#FF0000", 0.5), new("#FF0000", 0.5), new("#FFFFFF", 1)]);
        store.Save(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, MixerGradient = gradient });
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, MixerGradient = gradient }, store.Load());
        Assert.Contains("\"colour\": \"#FF0000\"", System.IO.File.ReadAllText(File()), StringComparison.Ordinal);

        string[] invalid =
        [
            "[{ \"colour\": \"#000000\", \"position\": 0 }]",
            "[{ \"colour\": \"#000000\", \"position\": 0 }, { \"colour\": \"blue\", \"position\": 1 }]",
            "[{ \"colour\": \"#000000\", \"position\": 0 }, { \"colour\": \"#FFFFFF\", \"position\": \"top\" }]",
            "[{ \"colour\": \"#000000\" }, { \"colour\": \"#FFFFFF\", \"position\": 1 }]",
            "\"rainbow\"",
            "{ \"stops\": [] }",
            "null",
        ];
        foreach (var json in invalid)
        {
            System.IO.File.WriteAllText(File(), $"{{ \"levelStyle\": \"peak\", \"signalGradient\": {json}, \"indicatorSize\": \"large\" }}");
            Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak }, store.Load());
        }

        // Out-of-range and unsorted positions are clamped and sorted, not rejected.
        System.IO.File.WriteAllText(File(), "{ \"signalGradient\": [{ \"colour\": \"#ffffff\", \"position\": 2 }, { \"colour\": \"#000000\", \"position\": -1 }] }");
        Assert.Equal(SignalGradient.Create([new("#000000", 0), new("#FFFFFF", 1)]), store.Load().MixerGradient);
    }

    [Fact]
    public void TheViewModel_EditsTheGradient_AndSavesEveryEdit()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.Equal(SignalGradient.Default, display.Mixer.Gradient);
        var notified = new List<string?>();
        display.Mixer.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // A new stop takes the colour already there, so the gradient looks the same.
        var before = display.Mixer.Gradient.Evaluate(0.2);
        Assert.Equal(1, display.Mixer.AddGradientStop(0.2));
        Assert.Equal(7, display.Mixer.Gradient.Stops.Length);
        Assert.Equal(new SignalGradientStop("#34C6F4", 0.2), display.Mixer.Gradient.Stops[1]);
        Assert.Equal(before, display.Mixer.Gradient.Evaluate(0.2));
        Assert.Contains(nameof(GradientEditorViewModel.Gradient), notified);
        Assert.Equal(display.Mixer.Gradient, store.Load().MixerGradient);

        Assert.True(display.Mixer.SetGradientStopColour(1, "#ff0000"));
        Assert.Equal(new SignalGradientStop("#FF0000", 0.2), store.Load().MixerGradient.Stops[1]);
        Assert.False(display.Mixer.SetGradientStopColour(1, "red"));
        Assert.False(display.Mixer.SetGradientStopColour(9, "#FF0000"));

        // Moving past a neighbour reorders; the returned index follows the stop.
        Assert.Equal(2, display.Mixer.MoveGradientStop(1, 0.5));
        Assert.Equal(new SignalGradientStop("#FF0000", 0.5), store.Load().MixerGradient.Stops[2]);
        // At a tie it keeps its order relative to the stop already there.
        Assert.Equal(5, display.Mixer.MoveGradientStop(2, 1.5));
        Assert.Equal(new SignalGradientStop("#FF0000", 1), store.Load().MixerGradient.Stops[5]);
        Assert.Equal(-1, display.Mixer.MoveGradientStop(7, 0.5));

        Assert.True(display.Mixer.RemoveGradientStop(5));
        Assert.Equal(SignalGradient.Default, store.Load().MixerGradient);
        Assert.False(display.Mixer.RemoveGradientStop(-1));

        Assert.Equal(5, display.Mixer.AddGradientStop(0.9));
        Assert.Equal(6, display.Mixer.AddGradientStop(0.95));
        Assert.Equal(-1, display.Mixer.AddGradientStop(0.5));
        Assert.Equal(8, store.Load().MixerGradient.Stops.Length);

        display.Mixer.ResetGradientCommand.Execute(null);
        Assert.Equal(SignalGradient.Default, store.Load().MixerGradient);

        // Never fewer than two stops; the other preferences are kept alongside.
        display.LevelStyle = JunctionLevelStyle.Peak;
        while (display.Mixer.RemoveGradientStop(0))
        {
        }

        Assert.Equal(2, display.Mixer.Gradient.Stops.Length);
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, MixerGradient = display.Mixer.Gradient }, store.Load());
    }
    [Fact]
    public void TheEditor_AddsInTheWidestGap_RemovesTheSelectedStop_AndKeepsTwo()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

        // Default's widest gap is 0 to 0.40, so the new stop goes at 0.20 and is selected.
        display.Mixer.AddGradientStopInLargestGapCommand.Execute(null);
        Assert.Equal(1, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(new SignalGradientStop("#34C6F4", 0.2), display.Mixer.SelectedGradientStop);
        Assert.Equal("20%", display.Mixer.SelectedGradientStopPercent);
        Assert.Equal(7, store.Load().MixerGradient.Stops.Length);

        display.Mixer.AddGradientStopInLargestGapCommand.Execute(null);
        Assert.False(display.Mixer.AddGradientStopInLargestGapCommand.CanExecute(null));

        display.Mixer.SelectedGradientStopIndex = 3;
        var removed = display.Mixer.SelectedGradientStop;
        display.Mixer.RemoveSelectedGradientStopCommand.Execute(null);
        Assert.Equal(2, display.Mixer.SelectedGradientStopIndex);
        Assert.DoesNotContain(removed, store.Load().MixerGradient.Stops);
        Assert.True(display.Mixer.AddGradientStopInLargestGapCommand.CanExecute(null));

        var notified = new List<string?>();
        display.Mixer.RemoveSelectedGradientStopCommand.CanExecuteChanged += (_, _) => notified.Add("can remove");
        while (display.Mixer.CanRemoveGradientStop)
        {
            display.Mixer.RemoveSelectedGradientStopCommand.Execute(null);
        }

        Assert.Equal(2, store.Load().MixerGradient.Stops.Length);
        Assert.False(display.Mixer.RemoveSelectedGradientStopCommand.CanExecute(null));
        Assert.Contains("can remove", notified);
        Assert.InRange(display.Mixer.SelectedGradientStopIndex, 0, 1);
    }

    [Fact]
    public void TheEditor_NudgesAndRecoloursTheSelectedStop_AndFollowsItWhenItPassesANeighbour()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

        display.Mixer.NudgeGradientStop(1, 0.01);
        Assert.Equal(1, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(0.41, store.Load().MixerGradient.Stops[1].Position);
        Assert.Equal("41%", display.Mixer.SelectedGradientStopPercent);

        // Past the 0.62 stop: the selection follows the stop to its new place.
        display.Mixer.MoveAndSelectGradientStop(1, 0.7);
        Assert.Equal(2, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(new SignalGradientStop("#34C6F4", 0.7), display.Mixer.SelectedGradientStop);
        display.Mixer.NudgeGradientStop(2, -0.05);
        Assert.Equal(0.65, store.Load().MixerGradient.Stops[2].Position);

        var notified = new List<string?>();
        display.Mixer.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        display.Mixer.SelectedGradientStopColour = "#12";
        Assert.Equal("#34C6F4", display.Mixer.SelectedGradientStopColour);
        display.Mixer.SelectedGradientStopColour = "ff8800";
        Assert.Equal("#FF8800", store.Load().MixerGradient.Stops[2].Colour);
        Assert.Contains(nameof(GradientEditorViewModel.SelectedGradientStopColour), notified);
        display.Mixer.ChooseGradientStopColourCommand.Execute("#7C6BFF");
        Assert.Equal("#7C6BFF", display.Mixer.SelectedGradientStopColour);
        Assert.DoesNotContain("#FF3640", DisplayViewModel.GradientSwatches);

        display.Mixer.ResetGradientCommand.Execute(null);
        Assert.Equal(0, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(SignalGradient.Default, store.Load().MixerGradient);
    }

    [Fact]
    public void APreview_AppliesEachEditAtOnce_ButSavesOnlyOnceItEnds()
    {
        var path = File();
        var store = new DisplaySettingsStore(path);
        var display = new DisplayViewModel();
        display.Use(store);
        display.LevelStyle = JunctionLevelStyle.Peak;
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
        Assert.Equal(0.2, store.Load().MixerGradient.Stops[1].Position);

        // Ending it again, or a preview with no edits, writes nothing.
        System.IO.File.Delete(path);
        display.Mixer.EndGradientPreview();
        display.Mixer.BeginGradientPreview();
        display.Mixer.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path));

        // Outside a preview every edit saves again.
        display.Mixer.NudgeGradientStop(1, 0.01);
        Assert.Equal(0.21, store.Load().MixerGradient.Stops[1].Position);
    }

    [Fact]
    public void Nudges_StopAtTheEndsOfTheScale()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

        display.Mixer.NudgeGradientStop(0, -0.01);
        display.Mixer.NudgeGradientStop(0, -0.05);
        Assert.Equal(0, store.Load().MixerGradient.Stops[0].Position);
        Assert.Equal("0%", display.Mixer.SelectedGradientStopPercent);

        var last = display.Mixer.Gradient.Stops.Length - 1;
        display.Mixer.NudgeGradientStop(last, 0.01);
        display.Mixer.NudgeGradientStop(last, 0.05);
        Assert.Equal(1, store.Load().MixerGradient.Stops[last].Position);
        Assert.Equal("100%", display.Mixer.SelectedGradientStopPercent);
    }

    [Fact]
    public void ALegacyFile_LoadsItsOneGradientIntoTheMixerAndChain_WithoutSaving_AndTheNextSaveDropsTheLegacyKey()
    {
        var path = File();
        var store = new DisplaySettingsStore(path);
        var legacy = SignalGradient.Create([new("#000000", 0), new("#FF0000", 1)]);
        System.IO.File.WriteAllText(path, "{ \"levelStyle\": \"peak\", \"signalGradient\": [{ \"colour\": \"#000000\", \"position\": 0 }, { \"colour\": \"#ff0000\", \"position\": 1 }] }");
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, MixerGradient = legacy, ChainGradient = legacy }, store.Load());

        var written = System.IO.File.ReadAllText(path);
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.Equal(legacy, display.Mixer.Gradient);
        Assert.Equal(legacy, display.Chain.Gradient);
        Assert.Equal(SignalGradient.GainDefault, display.Gain.Gradient);
        Assert.Equal(DisplaySettings.DefaultMidiColour, display.MidiColour);
        Assert.True(display.LedFade);
        Assert.True(display.LedWarmup);
        Assert.Equal(written, System.IO.File.ReadAllText(path));

        display.LedFade = false;
        var saved = System.IO.File.ReadAllText(path);
        Assert.DoesNotContain("signalGradient", saved, StringComparison.Ordinal);
        Assert.Contains("mixerGradient", saved, StringComparison.Ordinal);
        Assert.Contains("chainGradient", saved, StringComparison.Ordinal);
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, MixerGradient = legacy, ChainGradient = legacy, LedFade = false }, store.Load());

        // A key of its own wins over the legacy one.
        System.IO.File.WriteAllText(path, "{ \"signalGradient\": [{ \"colour\": \"#000000\", \"position\": 0 }, { \"colour\": \"#FF0000\", \"position\": 1 }], \"chainGradient\": [{ \"colour\": \"#FFFFFF\", \"position\": 0 }, { \"colour\": \"#FFFFFF\", \"position\": 1 }] }");
        Assert.Equal(legacy, store.Load().MixerGradient);
        Assert.Equal(SignalGradient.Create([new("#FFFFFF", 0), new("#FFFFFF", 1)]), store.Load().ChainGradient);
    }

    [Fact]
    public void UnreadableValues_FallBackOneByOne()
    {
        var store = new DisplaySettingsStore(File());
        var gain = SignalGradient.Create([new("#111111", 0), new("#222222", 1)]);
        System.IO.File.WriteAllText(File(), "{ \"levelStyle\": \"peak\", \"mixerGradient\": \"rainbow\", \"gainGradient\": [{ \"colour\": \"#111111\", \"position\": 0 }, { \"colour\": \"#222222\", \"position\": 1 }], \"midiColour\": \"red\", \"ledFade\": \"no\", \"ledWarmup\": false }");
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, GainGradient = gain, LedWarmup = false }, store.Load());

        System.IO.File.WriteAllText(File(), "{ \"midiColour\": \"#ff8800\", \"ledFade\": 0, \"ledWarmup\": null }");
        Assert.Equal(new DisplaySettings { MidiColour = "#FF8800" }, store.Load());

        System.IO.File.WriteAllText(File(), "[1, 2]");
        Assert.Equal(new DisplaySettings(), store.Load());
    }

    [Fact]
    public void TheThreeGradients_EditIndependently_AndEachResetsToItsOwnDefault()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.Equal([display.Mixer, display.Chain, display.Gain], display.Editors);
        Assert.Equal(SignalGradient.Default, display.Chain.DefaultGradient);
        Assert.Equal(SignalGradient.GainDefault, display.Gain.DefaultGradient);
        Assert.Equal(["#6B4BE0", "#A45CF2", "#E070C8", "#FFA3C4"], display.Gain.Gradient.Stops.Select(s => s.Colour));
        Assert.True(display.Mixer.HasClipLamp);
        Assert.False(display.Gain.HasClipLamp);

        Assert.True(display.Mixer.SetGradientStopColour(0, "#FF0000"));
        display.Chain.MoveAndSelectGradientStop(1, 0.5);
        Assert.Equal(3, display.Gain.AddGradientStop(0.9));

        var saved = store.Load();
        Assert.Equal("#FF0000", saved.MixerGradient.Stops[0].Colour);
        Assert.Equal(0.40, saved.MixerGradient.Stops[1].Position);
        Assert.Equal("#34C6F4", saved.ChainGradient.Stops[0].Colour);
        Assert.Equal(0.5, saved.ChainGradient.Stops[1].Position);
        Assert.Equal(5, saved.GainGradient.Stops.Length);
        Assert.Equal(0, display.Mixer.SelectedGradientStopIndex);
        Assert.Equal(1, display.Chain.SelectedGradientStopIndex);

        display.Gain.ResetGradientCommand.Execute(null);
        Assert.Equal(SignalGradient.GainDefault, store.Load().GainGradient);
        Assert.Equal("#FF0000", store.Load().MixerGradient.Stops[0].Colour);
        display.Mixer.ResetGradientCommand.Execute(null);
        display.Chain.ResetGradientCommand.Execute(null);
        Assert.Equal(new DisplaySettings(), store.Load());
    }

    [Fact]
    public void ADragInAnyGradient_SavesExactlyOnce_WhenTheLastPreviewEnds()
    {
        var path = File();
        var store = new DisplaySettingsStore(path);
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.False(System.IO.File.Exists(path), "Loading never saves.");

        display.Chain.BeginGradientPreview();
        display.Chain.MoveAndSelectGradientStop(1, 0.3);
        display.Gain.BeginGradientPreview();
        display.Gain.MoveAndSelectGradientStop(1, 0.5);
        display.Chain.MoveAndSelectGradientStop(1, 0.2);
        display.Chain.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path), "Nothing is written while any preview is open.");

        display.Gain.EndGradientPreview();
        Assert.Equal(0.2, store.Load().ChainGradient.Stops[1].Position);
        Assert.Equal(0.5, store.Load().GainGradient.Stops[1].Position);

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
        var display = new DisplayViewModel();
        display.Use(store);
        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        foreach (var invalid in new[] { "red", "#12", "#GGGGGG", "#3CCBF4FF", string.Empty, null })
        {
            display.MidiColour = invalid!;
            Assert.Equal(DisplaySettings.DefaultMidiColour, display.MidiColour);
        }

        Assert.Empty(notified);
        Assert.False(System.IO.File.Exists(File()));

        display.MidiColour = "ff8800";
        Assert.Equal("#FF8800", display.MidiColour);
        Assert.Equal("#FF8800", store.Load().MidiColour);
        Assert.Contains(nameof(DisplayViewModel.MidiColour), notified);

        display.ChooseMidiColourCommand.Execute("#7C6BFF");
        Assert.Equal("#7C6BFF", store.Load().MidiColour);
        display.ChooseMidiColourCommand.Execute("blue");
        Assert.Equal("#7C6BFF", display.MidiColour);
    }

    [Fact]
    public void AMidiColourPreview_AppliesEachChangeAtOnce_AppliesWithOneSave_AndCancelLeavesTheOriginal()
    {
        var path = File();
        var store = new DisplaySettingsStore(path);
        var display = new DisplayViewModel();
        display.Use(store);
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
        Assert.Equal("#E0A43A", store.Load().MidiColour);

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
        Assert.Equal(original, store.Load().MidiColour);

        // Outside a preview every change saves again.
        display.MidiColour = "#4CB873";
        Assert.Equal("#4CB873", store.Load().MidiColour);
    }

    [Fact]
    public void MidiAndGradientPreviews_HoldEachOthersSaves_AndTheLastToEndWritesBoth()
    {
        var path = File();
        var store = new DisplaySettingsStore(path);
        var display = new DisplayViewModel();
        display.Use(store);

        // A gradient drag pending while the MIDI picker is open is kept, and written once the picker closes.
        display.Mixer.BeginGradientPreview();
        display.Mixer.MoveAndSelectGradientStop(1, 0.3);
        display.BeginMidiColourPreview();
        display.Mixer.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path), "The open MIDI preview holds the gradient's save.");
        display.MidiColour = "#FF2E93";
        display.EndMidiColourPreview();
        Assert.Equal(0.3, store.Load().MixerGradient.Stops[1].Position);
        Assert.Equal("#FF2E93", store.Load().MidiColour);

        // And the other way round: a MIDI change waits for a gradient drag that outlasts it.
        System.IO.File.Delete(path);
        display.BeginMidiColourPreview();
        display.MidiColour = "#3FE0D8";
        display.Chain.BeginGradientPreview();
        display.EndMidiColourPreview();
        display.Chain.MoveAndSelectGradientStop(1, 0.2);
        Assert.False(System.IO.File.Exists(path), "The open gradient preview holds the MIDI colour's save.");
        display.Chain.EndGradientPreview();
        Assert.Equal("#3FE0D8", store.Load().MidiColour);
        Assert.Equal(0.2, store.Load().ChainGradient.Stops[1].Position);
        Assert.Equal(0.3, store.Load().MixerGradient.Stops[1].Position);
    }

    [Fact]
    public void LedFadeAndWarmup_RoundTrip()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

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
}
