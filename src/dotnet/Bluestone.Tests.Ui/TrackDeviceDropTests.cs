using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Bluestone.Application.Editing;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

public sealed class TrackDeviceDropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EmptyArrangement_ActionsAreVisibleAndClickable_WithScrollingWhenCompressed(bool compressed) => HeadlessApp.RunAsync(async () =>
    {
        var dialogs = new ImportDialog();
        await using var app = await AppUnderTest.OpenAsync(dialogs: dialogs, nativeMenuBar: true);
        app.Window.SetRenderScaling(1.1);
        app.Window.Width = 1040;
        app.Window.Height = compressed ? 640 : 671;
        app.Find<NativeMenuBar>("MenuBar").IsVisible = false;
        app.ViewModel.Workspace.Inspector.CollapseCommand.Execute(null);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.Workspace.Mixer.ShowCommand.Execute(null);
        app.ViewModel.Workspace.RightExtent = 272;
        app.ViewModel.Workspace.DetailExtent = 346;
        if (compressed)
        {
            // Both side panes open and wide leave a narrow arranger whose empty state must scroll.
            app.ViewModel.Workspace.Inspector.ShowCommand.Execute(null);
            app.ViewModel.Workspace.LeftExtent = 400;
            app.ViewModel.Workspace.RightExtent = 400;
            app.ViewModel.Workspace.DetailExtent = 1000;
        }

        app.Settle();
        Assert.Empty(app.ViewModel.Tracks);
        Assert.True(app.ViewModel.Workspace.Browser.IsDocked);
        Assert.True(app.ViewModel.Workspace.Mixer.IsDocked);
        var import = app.Window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Import MIDI…"));
        var newTrack = app.Window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "New Track"));
        var area = app.Find<Control>("TrackDropArea");

        if (compressed)
        {
            var scroll = import.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            Assert.NotNull(scroll);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "The compressed empty state should need scrolling.");
            app.Window.MouseWheel(app.InWindow(scroll, new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2)), new Vector(0, -20));
            app.Settle();
            Assert.True(scroll.Offset.Y > 0, "The empty state should scroll with the mouse wheel.");
        }

        AssertWithinArranger(app, area, import);
        // Do not call AppUnderTest.Click: its BringIntoView could mask a clipped action.
        app.ClickAt(import, new Point(import.Bounds.Width / 2, import.Bounds.Height / 2));
        await AppUnderTest.WaitUntilAsync(() => dialogs.OpenCount == 1, "the MIDI import dialog to open");
        Assert.Equal("Import MIDI File", dialogs.LastTitle);
        Assert.Empty(app.ViewModel.Tracks); // The fake dialog cancels without changing the project.
        AssertWithinArranger(app, area, newTrack);
        app.ClickAt(newTrack, new Point(newTrack.Bounds.Width / 2, newTrack.Bounds.Height / 2));
        Assert.Single(app.ViewModel.Tracks);
    });

    private static void AssertWithinArranger(AppUnderTest app, Control area, Button button)
    {
        var bounds = new Rect(app.InWindow(button, default), button.Bounds.Size);
        var available = new Rect(app.InWindow(area, default), area.Bounds.Size);
        var browser = app.Find<Panel>("BrowserDock");
        if (browser.IsEffectivelyVisible)
        {
            var browserLeft = app.InWindow(browser, default).X;
            available = available.Intersect(new Rect(0, 0, browserLeft, app.Window.Bounds.Height));
        }

        var inspector = app.Find<Panel>("InspectorDock");
        if (inspector.IsEffectivelyVisible)
        {
            var inspectorRight = app.InWindow(inspector, default).X + inspector.Bounds.Width;
            available = available.Intersect(new Rect(inspectorRight, 0, Math.Max(0, app.Window.Bounds.Width - inspectorRight), app.Window.Bounds.Height));
        }

        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(
            bounds.Left >= available.Left - 0.01 && bounds.Top >= available.Top - 0.01 &&
            bounds.Right <= available.Right + 0.01 && bounds.Bottom <= available.Bottom + 0.01,
            $"{button.Content} is not entirely in the arranger: button {bounds}, arranger {available}.");
    }

    private sealed class ImportDialog : IUserInteraction
    {
        public int OpenCount { get; private set; }

        public string? LastTitle { get; private set; }

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters)
        {
            OpenCount++;
            LastTitle = title;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(false);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Cancel);
    }

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
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public Task TallTrackReplacementPreview_StaysVisibleBeforeAndAfterScrolling(double fraction) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1040;
        app.Window.Height = 640;
        app.ViewModel.AddTrackCommand.Execute(null);
        app.ViewModel.IsEditorVisible = true;
        app.Settle();
        var expanded = app.ViewModel.Tracks[0];
        var transpose = app.ViewModel.Browser.Items.Single(i => i.Name == "Transpose").Definition!;
        for (var index = 0; index < 20; index++)
        {
            app.ViewModel.DeviceView!.InsertAt(index, transpose);
            app.Session.Execute(AutomationCommands.AddLane(expanded.Id, AutomationLane.Create(
                AutomationTarget.ForDevice(app.ViewModel.DeviceView.Devices[index].Id, BuiltInDevices.TransposeSemitones))));
        }

        expanded.IsAutomationExpanded = true;
        app.ViewModel.AddTrackCommand.Execute(null);
        app.Settle();
        var area = app.Find<Control>("TrackDropArea");
        Assert.True(expanded.RowHeight > 2 * area.Bounds.Height);
        var scroller = app.Find<ScrollViewer>("TimelineScroller");
        scroller.Offset = new Vector(0, fraction * (scroller.Extent.Height - scroller.Viewport.Height));
        app.Settle();
        var drag = Device(app, "Arpeggiator");
        var at = At(app, 350, 25);

        Drag(app, at, RawDragEventType.DragEnter, drag);

        var overlay = app.Find<Border>("DeviceDropOverlay");
        var label = app.Find<TextBlock>("DeviceDropText");
        Assert.True(overlay.IsVisible);
        Assert.Equal($"Replace devices on {expanded.Name} with Arpeggiator", label.Text);
        var areaTop = app.InWindow(area, new Point()).Y;
        var overlayTop = app.InWindow(overlay, new Point()).Y - areaTop;
        var labelTop = app.InWindow(label, new Point()).Y - areaTop;
        Assert.InRange(overlayTop, 0, area.Bounds.Height - overlay.Bounds.Height);
        Assert.True(label.Bounds.Height > 0);
        Assert.InRange(labelTop, 0, area.Bounds.Height - label.Bounds.Height);
        app.SaveFrame($"track-device-tall-preview-{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Drag(app, at, RawDragEventType.Drop, drag);
        Assert.Equal("Arpeggiator", Assert.Single(app.Session.Project.ChainOf(expanded.Id)!.Devices).DisplayName);
        Assert.Equal(2, app.Session.Project.Sequence.Tracks.Length);
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
