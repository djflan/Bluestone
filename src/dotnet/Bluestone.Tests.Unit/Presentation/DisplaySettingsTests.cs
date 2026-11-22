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
        Assert.Equal(SignalGradient.Default, store.Load().SignalGradient);

        var gradient = SignalGradient.Create([new("#000000", 0), new("#FF0000", 0.5), new("#FF0000", 0.5), new("#FFFFFF", 1)]);
        store.Save(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, SignalGradient = gradient });
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, SignalGradient = gradient }, store.Load());
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
        Assert.Equal(SignalGradient.Create([new("#000000", 0), new("#FFFFFF", 1)]), store.Load().SignalGradient);
    }

    [Fact]
    public void TheViewModel_EditsTheGradient_AndSavesEveryEdit()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.Equal(SignalGradient.Default, display.SignalGradient);
        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // A new stop takes the colour already there, so the gradient looks the same.
        var before = display.SignalGradient.Evaluate(0.2);
        Assert.Equal(1, display.AddGradientStop(0.2));
        Assert.Equal(7, display.SignalGradient.Stops.Length);
        Assert.Equal(new SignalGradientStop("#34C6F4", 0.2), display.SignalGradient.Stops[1]);
        Assert.Equal(before, display.SignalGradient.Evaluate(0.2));
        Assert.Contains(nameof(DisplayViewModel.SignalGradient), notified);
        Assert.Equal(display.SignalGradient, store.Load().SignalGradient);

        Assert.True(display.SetGradientStopColour(1, "#ff0000"));
        Assert.Equal(new SignalGradientStop("#FF0000", 0.2), store.Load().SignalGradient.Stops[1]);
        Assert.False(display.SetGradientStopColour(1, "red"));
        Assert.False(display.SetGradientStopColour(9, "#FF0000"));

        // Moving past a neighbour reorders; the returned index follows the stop.
        Assert.Equal(2, display.MoveGradientStop(1, 0.5));
        Assert.Equal(new SignalGradientStop("#FF0000", 0.5), store.Load().SignalGradient.Stops[2]);
        // At a tie it keeps its order relative to the stop already there.
        Assert.Equal(5, display.MoveGradientStop(2, 1.5));
        Assert.Equal(new SignalGradientStop("#FF0000", 1), store.Load().SignalGradient.Stops[5]);
        Assert.Equal(-1, display.MoveGradientStop(7, 0.5));

        Assert.True(display.RemoveGradientStop(5));
        Assert.Equal(SignalGradient.Default, store.Load().SignalGradient);
        Assert.False(display.RemoveGradientStop(-1));

        Assert.Equal(5, display.AddGradientStop(0.9));
        Assert.Equal(6, display.AddGradientStop(0.95));
        Assert.Equal(-1, display.AddGradientStop(0.5));
        Assert.Equal(8, store.Load().SignalGradient.Stops.Length);

        display.ResetGradientCommand.Execute(null);
        Assert.Equal(SignalGradient.Default, store.Load().SignalGradient);

        // Never fewer than two stops; the other preferences are kept alongside.
        display.LevelStyle = JunctionLevelStyle.Peak;
        while (display.RemoveGradientStop(0))
        {
        }

        Assert.Equal(2, display.SignalGradient.Stops.Length);
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, SignalGradient = display.SignalGradient }, store.Load());
    }
    [Fact]
    public void TheEditor_AddsInTheWidestGap_RemovesTheSelectedStop_AndKeepsTwo()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

        // Default's widest gap is 0 to 0.40, so the new stop goes at 0.20 and is selected.
        display.AddGradientStopInLargestGapCommand.Execute(null);
        Assert.Equal(1, display.SelectedGradientStopIndex);
        Assert.Equal(new SignalGradientStop("#34C6F4", 0.2), display.SelectedGradientStop);
        Assert.Equal("20%", display.SelectedGradientStopPercent);
        Assert.Equal(7, store.Load().SignalGradient.Stops.Length);

        display.AddGradientStopInLargestGapCommand.Execute(null);
        Assert.False(display.AddGradientStopInLargestGapCommand.CanExecute(null));

        display.SelectedGradientStopIndex = 3;
        var removed = display.SelectedGradientStop;
        display.RemoveSelectedGradientStopCommand.Execute(null);
        Assert.Equal(2, display.SelectedGradientStopIndex);
        Assert.DoesNotContain(removed, store.Load().SignalGradient.Stops);
        Assert.True(display.AddGradientStopInLargestGapCommand.CanExecute(null));

        var notified = new List<string?>();
        display.RemoveSelectedGradientStopCommand.CanExecuteChanged += (_, _) => notified.Add("can remove");
        while (display.CanRemoveGradientStop)
        {
            display.RemoveSelectedGradientStopCommand.Execute(null);
        }

        Assert.Equal(2, store.Load().SignalGradient.Stops.Length);
        Assert.False(display.RemoveSelectedGradientStopCommand.CanExecute(null));
        Assert.Contains("can remove", notified);
        Assert.InRange(display.SelectedGradientStopIndex, 0, 1);
    }

    [Fact]
    public void TheEditor_NudgesAndRecoloursTheSelectedStop_AndFollowsItWhenItPassesANeighbour()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

        display.NudgeGradientStop(1, 0.01);
        Assert.Equal(1, display.SelectedGradientStopIndex);
        Assert.Equal(0.41, store.Load().SignalGradient.Stops[1].Position);
        Assert.Equal("41%", display.SelectedGradientStopPercent);

        // Past the 0.62 stop: the selection follows the stop to its new place.
        display.MoveAndSelectGradientStop(1, 0.7);
        Assert.Equal(2, display.SelectedGradientStopIndex);
        Assert.Equal(new SignalGradientStop("#34C6F4", 0.7), display.SelectedGradientStop);
        display.NudgeGradientStop(2, -0.05);
        Assert.Equal(0.65, store.Load().SignalGradient.Stops[2].Position);

        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        display.SelectedGradientStopColour = "#12";
        Assert.Equal("#34C6F4", display.SelectedGradientStopColour);
        display.SelectedGradientStopColour = "ff8800";
        Assert.Equal("#FF8800", store.Load().SignalGradient.Stops[2].Colour);
        Assert.Contains(nameof(DisplayViewModel.SelectedGradientStopColour), notified);
        display.ChooseGradientStopColourCommand.Execute("#7C6BFF");
        Assert.Equal("#7C6BFF", display.SelectedGradientStopColour);
        Assert.DoesNotContain("#FF3640", DisplayViewModel.GradientSwatches);

        display.ResetGradientCommand.Execute(null);
        Assert.Equal(0, display.SelectedGradientStopIndex);
        Assert.Equal(SignalGradient.Default, store.Load().SignalGradient);
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
        display.PropertyChanged += (_, e) => notified += e.PropertyName == nameof(DisplayViewModel.SignalGradient) ? 1 : 0;

        display.BeginGradientPreview();
        display.MoveAndSelectGradientStop(1, 0.3);
        display.MoveAndSelectGradientStop(1, 0.2);
        Assert.Equal(0.2, display.SignalGradient.Stops[1].Position);
        Assert.Equal(2, notified);
        Assert.Equal(before, System.IO.File.ReadAllText(path));

        display.EndGradientPreview();
        Assert.Equal(0.2, store.Load().SignalGradient.Stops[1].Position);

        // Ending it again, or a preview with no edits, writes nothing.
        System.IO.File.Delete(path);
        display.EndGradientPreview();
        display.BeginGradientPreview();
        display.EndGradientPreview();
        Assert.False(System.IO.File.Exists(path));

        // Outside a preview every edit saves again.
        display.NudgeGradientStop(1, 0.01);
        Assert.Equal(0.21, store.Load().SignalGradient.Stops[1].Position);
    }

    [Fact]
    public void Nudges_StopAtTheEndsOfTheScale()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);

        display.NudgeGradientStop(0, -0.01);
        display.NudgeGradientStop(0, -0.05);
        Assert.Equal(0, store.Load().SignalGradient.Stops[0].Position);
        Assert.Equal("0%", display.SelectedGradientStopPercent);

        var last = display.SignalGradient.Stops.Length - 1;
        display.NudgeGradientStop(last, 0.01);
        display.NudgeGradientStop(last, 0.05);
        Assert.Equal(1, store.Load().SignalGradient.Stops[last].Position);
        Assert.Equal("100%", display.SelectedGradientStopPercent);
    }
}
