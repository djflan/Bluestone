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
}
