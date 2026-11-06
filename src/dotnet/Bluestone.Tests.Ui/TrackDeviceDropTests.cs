using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Bluestone.Application.Editing;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

public sealed class TrackDeviceDropTests
{
    private static Point At(AppUnderTest app, double x, double y) =>
        app.InWindow(app.Find<Control>("TrackDropArea"), new Point(x, y));

    private static void Drag(AppUnderTest app, Point at, RawDragEventType type, ChainDrag drag)
    {
        app.Window.DragDrop(at, type, drag.ToTransfer(), DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
        app.Settle();
    }

    private static ChainDrag Device(AppUnderTest app, string name) =>
        ChainDrag.Insert(app.ViewModel.Browser.Items.Single(i => i.Name == name).Definition!);

    [Theory]
    [InlineData(1040, 640)]
    [InlineData(1440, 900)]
    public Task DroppingADeviceInEmptyArrangement_CreatesANamedRoutedSelectedTrack_InOneUndoStep(double width, double height) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = width;
        app.Window.Height = height;
        app.Settle();
        var before = app.Session.Project;
        var drag = Device(app, "Arpeggiator");
        var at = At(app, 320, 80);

        Drag(app, at, RawDragEventType.DragEnter, drag);
        Assert.True(app.Find<Border>("DeviceDropOverlay").IsVisible);
        Assert.Equal("New track with Arpeggiator", app.Find<TextBlock>("DeviceDropText").Text);
        Assert.False(app.Find<Border>("DropOverlay").IsVisible);
        app.SaveFrame($"track-device-new-{width}");
        Drag(app, at, RawDragEventType.Drop, drag);

        var track = Assert.Single(app.Session.Project.Sequence.Tracks);
        Assert.Equal("Arpeggiator", track.Name);
        Assert.Equal(TrackRole.Hybrid, track.Role);
        Assert.Equal(track.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);
        Assert.Equal("Arpeggiator", Assert.Single(app.Session.Project.ChainOf(track.Id)!.Devices).DisplayName);
        Assert.Contains(app.Session.Project.Connections, c => c.Source == SignalNode.Track(track.Id) && c.Destination == SignalNode.Master && c.Kind == SignalKind.Audio);
        Assert.False(app.Find<Border>("DeviceDropOverlay").IsVisible);
        app.ViewModel.UndoCommand.Execute(null);
        Assert.Same(before, app.Session.Project);
        app.ViewModel.RedoCommand.Execute(null);
        Assert.Single(app.Session.Project.Sequence.Tracks);
    });

    [Theory]
    [InlineData(100)]
    [InlineData(350)]
    public Task DroppingOnATrackHeaderOrLane_ReplacesOnlyThatTracksWholeChain(double x) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Height = 900;
        app.ViewModel.AddTrackCommand.Execute(null);
        app.ViewModel.DeviceView!.InsertAt(0, app.ViewModel.Browser.Items.Single(i => i.Name == "Transpose").Definition!);
        app.ViewModel.DeviceView.InsertAt(1, app.ViewModel.Browser.Items.Single(i => i.Name == "Event Filter").Definition!);
        var first = app.Session.Project.Sequence.Tracks[0];
        app.ViewModel.AddTrackCommand.Execute(null);
        app.Settle();
        var before = app.Session.Project;
        var drag = Device(app, "Arpeggiator");
        var at = At(app, x, 25);

        Drag(app, at, RawDragEventType.DragEnter, drag);
        Assert.Equal($"Replace devices on {first.Name} with Arpeggiator", app.Find<TextBlock>("DeviceDropText").Text);
        Assert.Equal(TrackViewModel.TrackRowHeight, app.Find<Border>("DeviceDropOverlay").Height);
        app.SaveFrame("track-device-replace");
        Drag(app, at, RawDragEventType.Drop, drag);

        Assert.Equal(2, app.Session.Project.Sequence.Tracks.Length);
        Assert.Same(first, app.Session.Project.Sequence.FindTrack(first.Id));
        Assert.Equal(["Arpeggiator"], app.Session.Project.ChainOf(first.Id)!.Devices.Select(d => d.DisplayName));
        Assert.Null(app.Session.Project.ChainOf(app.Session.Project.Sequence.Tracks[1].Id));
        Assert.Equal(first.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);
        Assert.Equal(before.Connections, app.Session.Project.Connections);
        app.ViewModel.UndoCommand.Execute(null);
        Assert.Same(before, app.Session.Project);
        app.Settle();

        // Empty space below existing lanes still creates a new track, rather than editing the selection.
        Drag(app, At(app, x, 125), RawDragEventType.DragEnter, drag);
        Assert.Equal("New track with Arpeggiator", app.Find<TextBlock>("DeviceDropText").Text);
        Drag(app, At(app, x, 125), RawDragEventType.Drop, drag);
        Assert.Equal(3, app.Session.Project.Sequence.Tracks.Length);
        Assert.Equal(2, app.Session.Project.ChainOf(first.Id)!.Devices.Length);
    });

    [Fact]
    public Task NonArrangementDropsAndMovesFromADeviceChain_DoNotCreateOrReplaceTracks() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.ViewModel.AddTrackCommand.Execute(null);
        app.ViewModel.DeviceView!.InsertAt(0, app.ViewModel.Browser.Items.Single(i => i.Name == "Transpose").Definition!);
        app.Settle();
        var before = app.Session.Project;
        var vm = app.ViewModel.DeviceView;
        var at = At(app, 350, 25);

        Drag(app, at, RawDragEventType.DragEnter, Device(app, "Arpeggiator"));
        Assert.True(app.Find<Border>("DeviceDropOverlay").IsVisible);
        Drag(app, at, RawDragEventType.DragLeave, Device(app, "Arpeggiator"));
        Assert.False(app.Find<Border>("DeviceDropOverlay").IsVisible);
        Drag(app, at, RawDragEventType.Drop, ChainDrag.Move(vm.Devices[0].Id, vm));
        Assert.Same(before, app.Session.Project);

        var toolbar = app.InWindow(app.Find<Control>("WorkspaceTools"), new Point(10, 10));
        Drag(app, toolbar, RawDragEventType.DragEnter, Device(app, "Arpeggiator"));
        Assert.False(app.Find<Border>("DeviceDropOverlay").IsVisible);
        Drag(app, toolbar, RawDragEventType.Drop, Device(app, "Arpeggiator"));
        Assert.Same(before, app.Session.Project);
    });

    [Theory]
    [InlineData(100)]
    [InlineData(350)]
    public Task DropTargets_FollowFoldedGroupsExpandedAutomationAndVerticalScrolling(double x) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Height = 900;
        for (var i = 0; i < 16; i++)
        {
            app.ViewModel.AddTrackCommand.Execute(null);
        }

        var group = TrackId.New();
        var members = app.ViewModel.Tracks.Take(2).Select(t => t.Id).ToArray();
        app.Session.Execute(GroupCommands.CreateGroup(group, "Band", members, BuiltInDevices.Find));
        app.Session.Execute(GroupCommands.SetFolded(group, true));
        var expanded = app.ViewModel.Tracks[3];
        app.ViewModel.Select(expanded);
        app.Settle();
        app.ViewModel.DeviceView!.InsertAt(0, app.ViewModel.Browser.Items.Single(i => i.Name == "Transpose").Definition!);
        app.Session.Execute(AutomationCommands.AddLane(expanded.Id, AutomationLane.Create(
            AutomationTarget.ForDevice(app.ViewModel.DeviceView.Devices[0].Id, new ParameterId(1)))));
        expanded.IsAutomationExpanded = true;
        app.Settle();
        var before = app.Session.Project;
        var drag = Device(app, "Arpeggiator");

        // The two folded members occupy no rows; the expanded track's automation belongs to that track.
        Assert.All(app.ViewModel.Tracks.Skip(1).Take(2), t => Assert.False(t.IsShown));
        var automation = At(app, x, (2 * TrackViewModel.TrackRowHeight) + 10);
        Drag(app, automation, RawDragEventType.DragEnter, drag);
        Assert.Equal($"Replace devices on {expanded.Name} with Arpeggiator", app.Find<TextBlock>("DeviceDropText").Text);
        Drag(app, automation, RawDragEventType.Drop, drag);
        Assert.Equal("Arpeggiator", Assert.Single(app.Session.Project.ChainOf(expanded.Id)!.Devices).DisplayName);
        Assert.Equal(before.Sequence.FindTrack(expanded.Id)!.Automation, app.Session.Project.Sequence.FindTrack(expanded.Id)!.Automation);
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();

        var offset = TrackViewModel.TrackRowHeight + expanded.RowHeight;
        app.Find<ScrollViewer>("TimelineScroller").Offset = new Vector(0, offset);
        app.Settle();
        Assert.Equal(offset, app.Find<ScrollViewer>("TimelineScroller").Offset.Y);
        var scrolled = At(app, x, 25);
        var expected = app.ViewModel.Tracks[4];
        Drag(app, scrolled, RawDragEventType.DragEnter, drag);
        Assert.Equal($"Replace devices on {expected.Name} with Arpeggiator", app.Find<TextBlock>("DeviceDropText").Text);
        Drag(app, scrolled, RawDragEventType.Drop, drag);
        Assert.Equal("Arpeggiator", Assert.Single(app.Session.Project.ChainOf(expected.Id)!.Devices).DisplayName);
        Assert.Equal("Transpose", Assert.Single(app.Session.Project.ChainOf(expanded.Id)!.Devices).DisplayName);
        Assert.All(members, id => Assert.Null(app.Session.Project.ChainOf(id)));
    });
}
