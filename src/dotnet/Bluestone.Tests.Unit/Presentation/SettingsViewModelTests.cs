using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>The Settings surface's state: which page is shown, what the search keeps, and that opening and closing are its own.</summary>
public sealed class SettingsViewModelTests
{
    [Fact]
    public void Opening_ShowsTheAskedPage_OrThePageShownLast_AndClosingKeepsIt()
    {
        var settings = new SettingsViewModel();
        Assert.False(settings.IsOpen);

        settings.Open();
        Assert.True(settings.IsOpen);
        Assert.Equal(SettingsPage.Audio, settings.SelectedCategory?.Page);

        settings.Open(SettingsPage.Plugins);
        settings.CloseCommand.Execute(null);
        settings.Open();

        Assert.Equal(SettingsPage.Plugins, settings.SelectedCategory?.Page);
    }

    [Fact]
    public void TheSearch_MatchesNamesDescriptionsAndKeywords_AndOpeningAPageClearsIt()
    {
        var settings = new SettingsViewModel();

        settings.SearchText = "sample rate";
        Assert.Equal([SettingsPage.Audio], settings.Categories.Select(c => c.Page));

        settings.SearchText = "VST3";
        Assert.Equal([SettingsPage.Plugins], settings.Categories.Select(c => c.Page));
        Assert.Equal(SettingsPage.Plugins, settings.SelectedCategory?.Page);

        settings.SearchText = "xyzzy";
        Assert.True(settings.IsEmpty);
        Assert.Null(settings.SelectedCategory);

        settings.Open(SettingsPage.Audio);
        Assert.Equal(string.Empty, settings.SearchText);
        Assert.Equal(2, settings.Categories.Count);
        Assert.Equal(SettingsPage.Audio, settings.SelectedCategory?.Page);
    }

    [Fact]
    public void APageWithoutContent_SaysItIsNotAvailable()
    {
        var settings = new SettingsViewModel();
        settings.Open(SettingsPage.Plugins);

        Assert.False(settings.SelectedCategory!.IsAvailable);
        Assert.Equal("Plugin hosting is not available in this session.", settings.SelectedCategory.UnavailableText);
    }
}
