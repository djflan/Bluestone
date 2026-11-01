using Avalonia.Controls;
using Avalonia.LogicalTree;
using Bluestone.Desktop.Views;

namespace Bluestone.Tests.Ui;

/// <summary>The start-up splash says what start-up is doing, and a failed start offers a way out instead of hanging.</summary>
public sealed class SplashWindowTests
{
    [Fact]
    public Task Report_ShowsTheStepAndItsProgress() => HeadlessApp.RunAsync(async () =>
    {
        var splash = new SplashWindow();
        splash.Show();
        splash.Report("Scanning plugins (2 of 4)…", "Surge XT.vst3", 0.25);
        await splash.RenderedAsync();

        var texts = splash.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("BLUESTONE", texts);
        Assert.Contains("PRE-RELEASE VERSION", texts);
        Assert.NotNull(splash.GetLogicalDescendants().OfType<Image>().Single().Source);
        Assert.Contains("Scanning plugins (2 of 4)…", texts);
        Assert.Contains("Surge XT.vst3", texts);
        var progress = splash.GetLogicalDescendants().OfType<ProgressBar>().Single();
        Assert.False(progress.IsIndeterminate);
        Assert.Equal(0.25, progress.Value);

        splash.Report("Loading device profiles…");
        Assert.True(progress.IsIndeterminate);
        splash.Close();
    });

    [Fact]
    public Task ShowFailure_ExplainsAndQuitRaisesQuitRequested() => HeadlessApp.RunAsync(() =>
    {
        var splash = new SplashWindow();
        var quit = false;
        splash.QuitRequested += (_, _) => quit = true;
        splash.Show();
        splash.ShowFailure("The profiles folder could not be read.");

        Assert.Contains("The profiles folder could not be read.", splash.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.False(splash.GetLogicalDescendants().OfType<ProgressBar>().Single().IsVisible);
        var button = splash.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Quit"));
        Assert.True(button.IsVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(quit);
        splash.Close();
        return Task.CompletedTask;
    });
}
