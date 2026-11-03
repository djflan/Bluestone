using Avalonia.Automation;
using Avalonia.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The status line at the bottom of the main window: what Bluestone is doing now, with an activity indicator, otherwise
/// the latest message; clicking it opens Messages, which keeps the history.
/// </summary>
public sealed class StatusLineTests
{
    [Fact]
    public Task TheStatusLine_ShowsWhatBluestoneIsDoing_ThenHowItEnded_AndOpensMessages() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var text = app.Find<TextBlock>("LastMessage");
        var indicator = app.Find<ProgressBar>("StatusActivity");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));

        var scan = app.ViewModel.Activities.Begin("Plugins", "Scanning plugins (3 of 12)…");
        app.Settle();

        Assert.Equal("Scanning plugins (3 of 12)…", text.Text);
        Assert.True(indicator.IsEffectivelyVisible);
        app.SaveFrame("status-line-working");

        scan.Complete("12 plugins available from 9 modules.");
        app.Settle();

        Assert.Equal("12 plugins available from 9 modules.", text.Text);
        Assert.False(indicator.IsEffectivelyVisible);

        app.Click(app.Find<Button>("StatusLine"));
        app.Settle();

        Assert.Equal(LowerPane.Messages, app.ViewModel.LowerPane);
        Assert.Contains(app.ViewModel.Messages, m => m.Source == "Plugins" && m.Text == "12 plugins available from 9 modules.");
    });
}
