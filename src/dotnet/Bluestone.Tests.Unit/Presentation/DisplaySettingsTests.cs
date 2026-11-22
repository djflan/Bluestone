using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>Settings ▸ Display: one application-wide choice of where junction indicators sit, saved as soon as it changes.</summary>
public sealed class DisplaySettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-display-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string name = "display-settings.json") => Path.Combine(_directory, name);

    [Fact]
    public void TheStore_RoundTrips_AndAMissingDamagedOrUnknownFileGivesTheDefault()
    {
        var store = new DisplaySettingsStore(File());
        Assert.Equal(JunctionIndicatorPosition.LowerHalf, store.Load().IndicatorPosition);

        store.Save(new DisplaySettings { IndicatorPosition = JunctionIndicatorPosition.UpperHalf });
        Assert.Equal(JunctionIndicatorPosition.UpperHalf, store.Load().IndicatorPosition);
        Assert.Contains("\"upperHalf\"", System.IO.File.ReadAllText(File()), StringComparison.Ordinal);

        System.IO.File.WriteAllText(File(), "{ not json");
        Assert.Equal(JunctionIndicatorPosition.LowerHalf, store.Load().IndicatorPosition);
        System.IO.File.WriteAllText(File(), "{ \"indicatorPosition\": \"belowHeader\" }");
        Assert.Equal(JunctionIndicatorPosition.LowerHalf, store.Load().IndicatorPosition);
        System.IO.File.WriteAllText(File(), "{ \"indicatorPosition\": \"sideways\" }");
        Assert.Equal(JunctionIndicatorPosition.LowerHalf, store.Load().IndicatorPosition);

        // One unknown value does not reset the others.
        System.IO.File.WriteAllText(File(), "{ \"indicatorPosition\": \"top\", \"levelStyle\": \"loud\" }");
        Assert.Equal(new DisplaySettings { IndicatorPosition = JunctionIndicatorPosition.Top }, store.Load());
        store.Save(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak });
        Assert.Equal(JunctionLevelStyle.Peak, store.Load().LevelStyle);
        Assert.Contains("\"peak\"", System.IO.File.ReadAllText(File()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewModel_LoadsWithoutSaving_AndSavesEveryChange()
    {
        var store = new DisplaySettingsStore(File());
        store.Save(new DisplaySettings { IndicatorPosition = JunctionIndicatorPosition.Top });
        var written = System.IO.File.GetLastWriteTimeUtc(File());
        var display = new DisplayViewModel();

        display.Use(store);
        Assert.Equal(JunctionIndicatorPosition.Top, display.IndicatorPosition);
        Assert.Equal("Top of the device", display.IndicatorPositionChoice.Name);
        Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(File()));

        display.IndicatorPositionChoice = DisplayViewModel.Positions.Single(p => p.Position == JunctionIndicatorPosition.Bottom);
        Assert.Equal(JunctionIndicatorPosition.Bottom, new DisplaySettingsStore(File()).Load().IndicatorPosition);
        Assert.Equal(4, DisplayViewModel.Positions.Count);

        display.LevelStyleChoice = DisplayViewModel.LevelStyles.Single(l => l.Style == JunctionLevelStyle.Peak);
        Assert.Equal(new DisplaySettings { IndicatorPosition = JunctionIndicatorPosition.Bottom, LevelStyle = JunctionLevelStyle.Peak }, new DisplaySettingsStore(File()).Load());
    }

    [Fact]
    public void IndicatorSizeAndLedShape_RoundTrip_AndOldOrUnknownValuesGiveCompactBars()
    {
        var store = new DisplaySettingsStore(File());
        System.IO.File.WriteAllText(File(), "{ \"indicatorPosition\": \"top\" }");
        Assert.Equal(new DisplaySettings { IndicatorPosition = JunctionIndicatorPosition.Top }, store.Load());

        store.Save(new DisplaySettings { IndicatorSize = SignalIndicatorSize.Large, LedShape = LedShape.Round });
        Assert.Equal(new DisplaySettings { IndicatorSize = SignalIndicatorSize.Large, LedShape = LedShape.Round }, store.Load());
        Assert.Contains("\"large\"", System.IO.File.ReadAllText(File()), StringComparison.Ordinal);

        System.IO.File.WriteAllText(File(), "{ \"indicatorSize\": \"huge\", \"ledShape\": \"star\", \"levelStyle\": \"peak\" }");
        Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak }, store.Load());
        System.IO.File.WriteAllText(File(), "{ \"indicatorSize\": 7, \"ledShape\": 9 }");
        Assert.Equal(new DisplaySettings(), store.Load());
    }

    [Fact]
    public void TheViewModel_OffersLedShapesOnlyAboveCompact_AndSavesBoth()
    {
        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);
        Assert.Equal(SignalIndicatorSize.Compact, display.IndicatorSize);
        Assert.False(display.HasLedShape);

        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        display.IndicatorSizeChoice = DisplayViewModel.Sizes.Single(s => s.Size == SignalIndicatorSize.Medium);
        Assert.True(display.HasLedShape);
        Assert.Contains(nameof(DisplayViewModel.HasLedShape), notified);

        display.LedShapeChoice = DisplayViewModel.LedShapes.Single(l => l.Shape == LedShape.Round);
        Assert.Equal(new DisplaySettings { IndicatorSize = SignalIndicatorSize.Medium, LedShape = LedShape.Round }, new DisplaySettingsStore(File()).Load());
    }

    [Fact]
    public void MediumAndLarge_HaveTheirOwnTopMiddleOrBottom_AndCompactKeepsItsFour()
    {
        Assert.Equal([JunctionIndicatorPosition.Top, JunctionIndicatorPosition.Middle, JunctionIndicatorPosition.Bottom], DisplayViewModel.LargePositions.Select(p => p.Position));
        Assert.DoesNotContain(DisplayViewModel.Positions, p => p.Position == JunctionIndicatorPosition.Middle);

        var store = new DisplaySettingsStore(File());
        var display = new DisplayViewModel();
        display.Use(store);
        display.IndicatorPosition = JunctionIndicatorPosition.UpperHalf;
        Assert.Equal(JunctionIndicatorPosition.UpperHalf, display.SeamIndicatorPosition);
        Assert.True(display.IsCompact);

        // At Medium the seams take the Medium and Large position (Middle by default), and Compact's choice is kept for later.
        var notified = new List<string?>();
        display.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        display.IndicatorSize = SignalIndicatorSize.Medium;
        Assert.False(display.IsCompact);
        Assert.Equal(JunctionIndicatorPosition.Middle, display.SeamIndicatorPosition);
        Assert.Contains(nameof(DisplayViewModel.SeamIndicatorPosition), notified);

        display.LargeIndicatorPositionChoice = DisplayViewModel.LargePositions.Single(p => p.Position == JunctionIndicatorPosition.Bottom);
        Assert.Equal(JunctionIndicatorPosition.Bottom, display.SeamIndicatorPosition);
        Assert.Equal(
            new DisplaySettings { IndicatorPosition = JunctionIndicatorPosition.UpperHalf, IndicatorSize = SignalIndicatorSize.Medium, LargeIndicatorPosition = JunctionIndicatorPosition.Bottom },
            store.Load());

        display.IndicatorSize = SignalIndicatorSize.Compact;
        Assert.Equal(JunctionIndicatorPosition.UpperHalf, display.SeamIndicatorPosition);

        // A position that does not apply to its size reads as that size's default.
        System.IO.File.WriteAllText(File(), "{ \"indicatorPosition\": \"middle\", \"largeIndicatorPosition\": \"upperHalf\" }");
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
            Assert.Equal(new DisplaySettings { LevelStyle = JunctionLevelStyle.Peak, IndicatorSize = SignalIndicatorSize.Large }, store.Load());
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
}
