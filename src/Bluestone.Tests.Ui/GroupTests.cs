using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Group tracks in the real window (ADR 0030): the track list's context menu groups the selection, the fold button on
/// the group's header hides its members' rows in the list and the arrangement, and the group's mute silences them.
/// </summary>
public sealed class GroupTests
{
    private static ProjectSession TwoTracks()
    {
        var session = new ProjectSession();
        foreach (var (name, note) in new[] { ("Bass", 40), ("Keys", 60) })
        {
            session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), name, [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromNumber(1), new NoteNumber(note), new Velocity(100))])));
        }

        return session;
    }

    private static ListBoxItem RowOf(AppUnderTest app, string name)
    {
        var row = app.ViewModel.Tracks.Single(t => t.Name == name);
        return app.Find<ListBox>("TrackList").ContainerFromItem(row) as ListBoxItem ?? throw new InvalidOperationException($"No row for {name}.");
    }

    private static T Button<T>(ListBoxItem row, string name)
        where T : Control =>
        AppUnderTest.Within<T>(row, b => AutomationProperties.GetName(b) == name);

    /// <summary>Right-clicks a track's row over its number, away from the name field (which has its own text menu).</summary>
    private static ContextMenu RightClick(AppUnderTest app, string track)
    {
        var owner = RowOf(app, track).GetLogicalDescendants().OfType<Control>().First(c => c.ContextMenu is not null);
        var point = owner.TranslatePoint(new Point(14, TrackViewModel.TrackRowHeight / 2), app.Window)!.Value;
        app.Window.MouseDown(point, MouseButton.Right);
        app.Window.MouseUp(point, MouseButton.Right);
        app.Settle();
        Assert.True(owner.ContextMenu!.IsOpen);
        return owner.ContextMenu;
    }

    [Fact]
    public Task TheContextMenu_GroupsTheSelection_ThenFoldingAndMutingTheGroup_HideAndSilenceItsMembers() => HeadlessApp.RunAsync(async () =>
    {
        var session = TwoTracks();
        await using var app = await AppUnderTest.OpenAsync(session);
        var synth = new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, app.Port.OutputId.Value, "Synth") };
        foreach (var track in session.Project.Sequence.Tracks)
        {
            session.Execute(ProjectCommands.SetTrackOutput(track.Id, synth));
        }

        app.ViewModel.SelectAllTracksCommand.Execute(null);
        app.Settle();

        // Right-click a selected row, and choose "Group Selected Tracks".
        var menu = RightClick(app, "Bass");
        Assert.Equal(2, app.ViewModel.SelectedTracks.Count);
        var group = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Group Selected Tracks"));
        group.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        app.Settle();

        Assert.Equal(["Group 1", "Bass", "Keys"], app.ViewModel.Tracks.Select(t => t.Name));
        var band = session.Project.Sequence.Tracks[0];
        Assert.Equal(TrackRole.Group, band.Role);
        Assert.All(session.Project.Sequence.Tracks.Skip(1), t => Assert.Equal(band.Id, t.Group));
        int Notes() => app.Playback.Routing?.Graph.ExternalParts.Sum(p => p.Events.Count(e => e.Event is NoteEvent)) ?? -1;
        await AppUnderTest.WaitUntilAsync(() => Notes() == 2, "the members to play through the group");
        Assert.Equal("→ Group 1", AppUnderTest.Within<TextBlock>(RowOf(app, "Bass"), t => t.Text?.StartsWith('→') == true).Text);

        // Fold by clicking the group header's fold button.
        var groupRow = RowOf(app, "Group 1");
        app.Click(Button<Avalonia.Controls.Primitives.ToggleButton>(groupRow, "Fold group"));

        Assert.True(session.Project.Sequence.FindTrack(band.Id)!.IsFolded);
        Assert.False(RowOf(app, "Bass").IsVisible);
        Assert.False(RowOf(app, "Keys").IsVisible);
        Assert.True(groupRow.IsEffectivelyVisible);
        Assert.Equal(2, app.Find<TimelineView>("Timeline").HiddenTracks.Count);
        app.SaveFrame("group-folded");

        // Mute the group from its header.
        app.Click(Button<Avalonia.Controls.Primitives.ToggleButton>(groupRow, "Mute"));

        Assert.True(session.Project.Sequence.FindTrack(band.Id)!.IsMuted);
        Assert.All(session.Project.Sequence.Tracks.Skip(1), t => Assert.False(t.IsMuted));
        await AppUnderTest.WaitUntilAsync(() => Notes() == 0, "the muted group to silence its members");

        // Unfold: the members come back, shown as silenced by their group.
        app.Click(Button<Avalonia.Controls.Primitives.ToggleButton>(groupRow, "Fold group"));

        Assert.True(RowOf(app, "Bass").IsVisible);
        Assert.True(app.ViewModel.Tracks.Single(t => t.Name == "Bass").IsMutedByGroup);
        app.SaveFrame("group-muted");

        // The menu offers the group to put tracks in.
        menu = RightClick(app, "Keys");
        var addTo = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Add to Group"));
        addTo.IsSubMenuOpen = true;
        app.Settle();
        var choice = Assert.Single(addTo.GetLogicalChildren().OfType<MenuItem>());
        Assert.Equal("Group 1", choice.Header);
        Assert.NotNull(choice.Command);
        menu.Close();
    });
}
