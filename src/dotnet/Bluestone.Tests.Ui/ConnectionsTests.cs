using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The Connections section (the routing inspector) in the real window: connections are added and removed with the
/// mouse, both ends show them, and a connection that would feed back is refused with a message (decision 22 in docs/roadmap.md).
/// </summary>
public sealed class ConnectionsTests
{
    private static void Expand(AppUnderTest app, Expander section)
    {
        if (!section.IsExpanded)
        {
            app.Click(section.GetVisualDescendants().OfType<ToggleButton>().First());
        }

        Assert.True(section.IsExpanded);
    }

    private static List<string?> Rows(Expander section) =>
        [.. section.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text?.StartsWith("Events ", StringComparison.Ordinal) == true).Select(t => t.Text)];

    [Fact]
    public Task AConnection_MadeWithTheMouse_IsInTheProject_ShowsAtBothEnds_AndIsRemovedWithItsButton() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(TrackInspectorTests.Tracks("Lead", "Bass"));
        var (lead, bass) = (app.Session.Project.Sequence.Tracks[0].Id, app.Session.Project.Sequence.Tracks[1].Id);
        TrackInspectorTests.SelectTrack(app, 0);
        var section = app.Find<Expander>("ConnectionsSection");
        Expand(app, section);

        app.Choose(AppUnderTest.ComboNamed(section, "Connection destination"), d => d is DestinationOption { Name: "Track: Bass" });
        app.Choose(AppUnderTest.ComboNamed(section, "Connection channel"), c => c is ChannelOption { Channel.Number: 2 });
        app.Click(AppUnderTest.Within<Button>(section, b => Equals(b.Content, "Connect")));

        var connection = Assert.Single(app.Session.Project.Connections);
        Assert.Equal(SignalKind.Events, connection.Kind);
        Assert.Equal(SignalNode.Track(lead), connection.Source);
        Assert.Equal(SignalNode.Track(bass), connection.Destination);
        Assert.Equal(MidiChannel.FromNumber(2), connection.Mapping.Force);
        Assert.Equal(["Events → track \"Bass\""], Rows(section));
        app.SaveFrame("connections");

        TrackInspectorTests.SelectTrack(app, 1);

        Assert.Equal(["Events ← track \"Lead\""], Rows(section));

        app.Click(AppUnderTest.Within<Button>(section, b => AutomationProperties.GetName(b) == "Disconnect"));

        Assert.Empty(app.Session.Project.Connections);
        Assert.Empty(Rows(section));

        app.Click(AppUnderTest.Within<Button>(app.Window, b => AutomationProperties.GetName(b) == "Undo"));

        Assert.Single(app.Session.Project.Connections);
        Assert.Equal(["Events ← track \"Lead\""], Rows(section));
    });

    [Fact]
    public Task AConnectionThatWouldFeedBack_IsRefused_AndTheMessageSaysWhy() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(TrackInspectorTests.Tracks("Lead", "Bass"));
        TrackInspectorTests.SelectTrack(app, 0);
        var section = app.Find<Expander>("ConnectionsSection");
        Expand(app, section);
        app.Choose(AppUnderTest.ComboNamed(section, "Connection destination"), d => d is DestinationOption { Name: "Track: Bass" });
        app.Click(AppUnderTest.Within<Button>(section, b => Equals(b.Content, "Connect")));
        TrackInspectorTests.SelectTrack(app, 1);

        app.Choose(AppUnderTest.ComboNamed(section, "Connection destination"), d => d is DestinationOption { Name: "Track: Lead" });
        app.Click(AppUnderTest.Within<Button>(section, b => Equals(b.Content, "Connect")));

        Assert.Single(app.Session.Project.Connections);
        var message = app.ViewModel.Messages[^1];
        Assert.Equal(MessageSeverity.Warning, message.Severity);
        Assert.Contains("feedback", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(message.Text, app.Find<TextBlock>("LastMessage").Text);
        Assert.Equal("Track: Lead", app.ViewModel.Connections.Destination?.Name);
        app.SaveFrame("connection-refused");
    });
}
