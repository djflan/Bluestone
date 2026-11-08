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
}
