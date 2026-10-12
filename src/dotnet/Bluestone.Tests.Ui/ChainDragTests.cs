using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Drags that start from a real gesture (a Browser item, a device panel's grip) and what the platform's drag source is given.
/// On macOS that is AppKit: every dragged item needs at least one type it can put on the pasteboard, and Avalonia.Native drops
/// in-process formats before handing the item over, so an item with only <see cref="ChainDrag.Format"/> makes the drag throw
/// as it starts. A recording drag source stands in for the platform's and checks the same contract; the round trip into a
/// Device View is replayed through the headless window. Native AppKit is not exercised here.
/// </summary>
public sealed class ChainDragTests
{
    /// <summary>The prefix Avalonia.Native gives application formats on the macOS pasteboard.</summary>
    private const string MacApplicationPrefix = "net.avaloniaui.app.uti.";

    private static readonly PluginIdentity Sine = new("bluestone-reference", "bluestone.reference", "reference.sine", "Reference Sine", "Bluestone", PluginKind.Instrument, "1.0.0");

    private static readonly PluginIdentity Gain = new("bluestone-reference", "bluestone.reference", "reference.gain", "Reference Gain", "Bluestone", PluginKind.AudioEffect, "1.0.0");

    private static DeviceDefinition BuiltIn(string name) => Signal.DeviceCatalog.BuiltIn.Definitions.Single(d => d.Name == name);

    [Fact]
    public Task AnInsertOrMoveTransfer_HasATypeAMacPasteboardCanCarry_ThatIsNoPayload_AndKeepsTheTypedDrag() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(TwoTracks());
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var vm = app.ViewModel.DeviceView!;
        vm.InsertAt(0, BuiltIn("Transpose"));
        app.Settle();

        var instrument = PluginDeviceHost.DefinitionOf(Sine);
        var insert = ChainDrag.Insert(instrument);
        var move = ChainDrag.Move(vm.Devices[0].Id, vm);
        foreach (var (drag, token) in new[] { (insert, "insert"), (move, "move") })
        {
            using var transfer = drag.ToTransfer();
            AssertNativeContract(transfer, Forbidden(drag));
            Assert.Same(drag, transfer.TryGetValue(ChainDrag.Format));
            Assert.Equal(token, transfer.TryGetValue(ChainDrag.NativeFormat));
        }
    });

    [Fact]
    public Task AnInstrumentDraggedFromTheBrowser_GivesThePlatformACarryableTransfer_AndLandsAtTheMarkedGap_AndUndoesAndRedoes() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        using var source = RecordingDragSource.Install();
        var browser = app.ViewModel.Browser;
        var sine = browser.Items.Single(i => i.Name == Sine.DisplayName);
        var before = browser.Items.ToList();

        var released = StartBrowserDrag(app, sine);
        var drag = Assert.Single(source.Drags);
        Assert.Equal(DragDropEffects.Copy, drag.Allowed);
        Assert.Equal(Sine.DisplayName, drag.Chain.Definition!.Name);

        var effect = source.DropAt(drag, app.Window, AfterLastPanel(app, Docked(app)));
        Assert.Equal(DragDropEffects.Copy, effect);

        // While the platform's session is live the Browser keeps its list, though the chain now has an instrument.
        Assert.Equal(before, browser.Items);
        RecordingDragSource.Finish(drag, effect);
        FinishGesture(app.Window, released);
        Assert.NotEqual(before, browser.Items);

        var after = new[] { "Transpose", "Event Filter", "Arpeggiator", Sine.DisplayName };
        Assert.Equal(after, ChainNames(app));
        var inserted = ChainIds(app);
        Assert.Equal(Sine.DisplayName, app.ViewModel.DeviceView!.SelectedDevice!.Name);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(["Transpose", "Event Filter", "Arpeggiator"], ChainNames(app));
        Assert.Equal(inserted.Take(3), ChainIds(app));

        app.ViewModel.RedoCommand.Execute(null);
        app.Settle();
        Assert.Equal(after, ChainNames(app));
        Assert.Equal(inserted, ChainIds(app));
    });

    [Fact]
    public Task ADeviceDraggedByItsGrip_GivesThePlatformACarryableTransfer_AndMovesToTheGap_AndUndoesAndRedoes() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        using var source = RecordingDragSource.Install();
        var vm = app.ViewModel.DeviceView!;
        var ids = ChainIds(app);

        var released = StartGripDrag(app.Window, Docked(app), vm.Devices[0]);
        var drag = Assert.Single(source.Drags);
        Assert.Equal(DragDropEffects.Move, drag.Allowed);
        Assert.Equal(ids[0], drag.Chain.Device);

        var effect = source.DropAt(drag, app.Window, AfterLastPanel(app, Docked(app)));
        RecordingDragSource.Finish(drag, effect);
        FinishGesture(app.Window, released);

        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(["Event Filter", "Arpeggiator", "Transpose"], ChainNames(app));
        Assert.Equal([ids[1], ids[2], ids[0]], ChainIds(app));

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(ids, ChainIds(app));

        app.ViewModel.RedoCommand.Execute(null);
        app.Settle();
        Assert.Equal([ids[1], ids[2], ids[0]], ChainIds(app));
    });

    [Fact]
    public Task ACancelledBrowserDrag_LeavesTheChainAsItWas_AndTheBrowserRanksAgain() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        using var source = RecordingDragSource.Install();
        var browser = app.ViewModel.Browser;
        var ids = ChainIds(app);
        var canUndo = app.Session.History.CanUndo;

        var released = StartBrowserDrag(app, browser.Items.Single(i => i.Name == Sine.DisplayName));
        var drag = Assert.Single(source.Drags);
        RecordingDragSource.Finish(drag, DragDropEffects.None);
        FinishGesture(app.Window, released);

        Assert.Equal(ids, ChainIds(app));
        Assert.Equal(canUndo, app.Session.History.CanUndo);

        // The drag is over: a chain change re-ranks the Browser at once (an audio effect now suits the chain best).
        app.ViewModel.DeviceView!.InsertAt(3, PluginDeviceHost.DefinitionOf(Sine));
        app.Settle();
        Assert.Equal(Gain.DisplayName, browser.Items[0].Name);
    });

    [Fact]
    public Task ABrowserDragDroppedOutsideAnyDeviceView_ChangesNothing() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        using var source = RecordingDragSource.Install();
        var ids = ChainIds(app);
        var sine = app.ViewModel.Browser.Items.Single(i => i.Name == Sine.DisplayName);

        var released = StartBrowserDrag(app, sine);
        var drag = Assert.Single(source.Drags);
        var list = AppUnderTest.Within<ListBox>(app.Find<BrowserView>("DockedBrowser"), l => l.Name == "ItemList");
        var effect = source.DropAt(drag, app.Window, app.InWindow(list, new Point(list.Bounds.Width / 2, list.Bounds.Height / 2)));
        RecordingDragSource.Finish(drag, effect);
        FinishGesture(app.Window, released);

        Assert.Equal(DragDropEffects.None, effect);
        Assert.Equal(ids, ChainIds(app));
    });

    [Fact]
    public Task ADetachedDeviceView_TakesABrowserDrag_AndReordersFromItsOwnGrip() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        var detached = app.ViewModel.Workspace.NewDeviceView();
        detached.DeviceView!.IsLocked = true;
        detached.DetachCommand.Execute(null);
        app.Settle();
        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        window.Width = 1200;
        window.Height = 340;
        window.UpdateLayout();
        app.Settle();
        var control = window.GetVisualDescendants().OfType<DeviceViewControl>().Single();
        using var source = RecordingDragSource.Install();

        var dragged = StartBrowserDrag(app, app.ViewModel.Browser.Items.Single(i => i.Name == Sine.DisplayName));
        var insert = Assert.Single(source.Drags);
        var inserted = source.DropAt(insert, window, AfterLastPanel(window, control));
        RecordingDragSource.Finish(insert, inserted);
        FinishGesture(app.Window, dragged);
        window.UpdateLayout();
        app.Settle();

        Assert.Equal(DragDropEffects.Copy, inserted);
        Assert.Equal(["Transpose", "Event Filter", "Arpeggiator", Sine.DisplayName], ChainNames(app));

        var ids = ChainIds(app);
        var gripped = StartGripDrag(window, control, detached.DeviceView.Devices[0]);
        var move = source.Drags[1];
        Assert.Equal(DragDropEffects.Move, move.Allowed);
        var moved = source.DropAt(move, window, AfterLastPanel(window, control));
        RecordingDragSource.Finish(move, moved);
        FinishGesture(window, gripped);

        Assert.Equal(DragDropEffects.Move, moved);
        Assert.Equal([ids[1], ids[2], ids[3], ids[0]], ChainIds(app));
        window.Close();
        app.Settle();
    });

    [Fact]
    public Task ADeviceDraggedFromAnotherViewOfTheSameChain_IsOfferedAsAMove_AndOneFromAnotherChainIsRefused() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        var detached = app.ViewModel.Workspace.NewDeviceView();
        detached.DeviceView!.IsLocked = true;
        detached.DetachCommand.Execute(null);
        app.Settle();
        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        window.Width = 1200;
        window.Height = 340;
        window.UpdateLayout();
        app.Settle();
        var control = window.GetVisualDescendants().OfType<DeviceViewControl>().Single();
        using var source = RecordingDragSource.Install();
        var ids = ChainIds(app);

        // The same chain in another view: the source allows only Move, so the target must offer Move for the drop to land.
        var gripped = StartGripDrag(window, control, detached.DeviceView.Devices[0]);
        var move = Assert.Single(source.Drags);
        var moved = source.DropAt(move, app.Window, AfterLastPanel(app, Docked(app)));
        RecordingDragSource.Finish(move, moved);
        FinishGesture(window, gripped);

        Assert.Equal(DragDropEffects.Move, moved);
        Assert.Equal([ids[1], ids[2], ids[0]], ChainIds(app));

        // Another chain: a device cannot move between chains, so nothing is offered and nothing changes.
        app.ViewModel.Workspace.DeviceViews.First().LockToTrack(app.ViewModel.Tracks[0].Id);
        detached.DeviceView.LockToTrack(app.ViewModel.Tracks[1].Id);
        detached.DeviceView.InsertAt(0, BuiltIn("Transpose"));
        window.UpdateLayout();
        app.Settle();
        Assert.Equal("Pad", detached.DeviceView.Heading);
        var lead = ChainIds(app);

        var other = StartGripDrag(window, control, detached.DeviceView.Devices[0]);
        var refused = source.Drags[1];
        var effect = source.DropAt(refused, app.Window, AfterLastPanel(app, Docked(app)));
        RecordingDragSource.Finish(refused, effect);
        FinishGesture(window, other);

        Assert.Equal(DragDropEffects.None, effect);
        Assert.Equal(lead, ChainIds(app));
        window.Close();
        app.Settle();
    });

    /// <summary>
    /// What Avalonia.Native would hand AppKit for <paramref name="transfer"/>: per item, the formats that are not in-process.
    /// Each item needs at least one, with a pasteboard type name and a string value, and none may carry anything in
    /// <paramref name="forbidden"/> (names and IDs: the companion is a token, not a payload).
    /// </summary>
    internal static void AssertNativeContract(IDataTransfer transfer, IEnumerable<string> forbidden)
    {
        Assert.NotEmpty(transfer.Items);
        foreach (var item in transfer.Items)
        {
            var native = item.Formats.Where(f => f.Kind != DataFormatKind.InProcess).ToList();
            Assert.NotEmpty(native);
            Assert.DoesNotContain(DataFormat.Text, item.Formats);
            foreach (var format in native)
            {
                Assert.False(string.IsNullOrWhiteSpace(format.ToSystemName(MacApplicationPrefix)));
                var value = Assert.IsType<string>(item.TryGetRaw(format));
                Assert.NotEmpty(value);
                Assert.All(forbidden, f => Assert.DoesNotContain(f, value, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static IEnumerable<string> Forbidden(ChainDrag drag)
    {
        if (drag.Definition is { } definition)
        {
            yield return definition.Name;
            yield return definition.Id.Value;
        }

        if (drag.Device is { } device)
        {
            yield return device.Value.ToString("D");
            yield return device.Value.ToString("N");
        }

        if (drag.Source is { } source)
        {
            yield return source.Heading;
        }
    }

    [Fact]
    public Task ABrowserPluginDroppedOverAPanelsMiddle_ShowsTheReplaceOverlay_ReplacesItInPlace_AndUndoes() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenWithChainAsync(plugins);
        using var source = RecordingDragSource.Install();
        var docked = Docked(app);
        var ids = ChainIds(app);
        var sine = app.ViewModel.Browser.Items.Single(i => i.Name == Sine.DisplayName);

        var released = StartBrowserDrag(app, sine);
        var drag = Assert.Single(source.Drags);
        var middle = PanelMiddle(app.Window, docked, 1);
        app.Window.DragDrop(middle, RawDragEventType.DragEnter, drag.Transfer, drag.Allowed);
        app.Window.DragDrop(middle, RawDragEventType.DragOver, drag.Transfer, drag.Allowed);
        var overlay = AppUnderTest.Within<Border>(docked, b => b.Name == "ReplaceOverlay");
        Assert.True(overlay.IsVisible);
        Assert.False(AppUnderTest.Within<Border>(docked, b => b.Name == "InsertionMarker").IsVisible);
        Assert.Equal($"Event Filter with {Sine.DisplayName}", AppUnderTest.Within<TextBlock>(docked, t => t.Name == "ReplaceDetail").Text);
        app.SaveFrame("replace-overlay");

        // Near a panel's edge the same drag inserts instead: the gap marker shows and the overlay goes.
        var nearEdge = AfterLastPanel(app, docked);
        app.Window.DragDrop(nearEdge, RawDragEventType.DragOver, drag.Transfer, drag.Allowed);
        Assert.False(overlay.IsVisible);
        Assert.True(AppUnderTest.Within<Border>(docked, b => b.Name == "InsertionMarker").IsVisible);
        app.SaveFrame("insert-marker");

        var effect = source.DropAt(drag, app.Window, middle);
        RecordingDragSource.Finish(drag, effect);
        FinishGesture(app.Window, released);
        await AppUnderTest.WaitUntilAsync(() => ChainNames(app).Contains(Sine.DisplayName), "the replacement");

        Assert.Equal(["Transpose", Sine.DisplayName, "Arpeggiator"], ChainNames(app));
        Assert.Equal(ids[0], ChainIds(app)[0]);
        Assert.NotEqual(ids[1], ChainIds(app)[1]);
        Assert.Equal("Replace Device", app.Session.History.UndoLabel);
        Assert.False(overlay.IsVisible);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(ids, ChainIds(app));
    });

    [Fact]
    public Task ReplacingBetweenBuiltInsAndPlugins_KeepsThePosition_AndAFailedPluginChangesNothing() => HeadlessApp.RunAsync(async () =>
    {
        var broken = new PluginIdentity("bluestone-reference", "bluestone.reference", "reference.missing", "Missing Synth", "Bluestone", PluginKind.Instrument, "1.0.0");
        await using var plugins = Plugins();
        await using var app = await AppUnderTest.OpenAsync(TwoTracks(), s => new PluginDeviceHost(plugins, s, [Sine, Gain, broken]));
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, BuiltIn("Transpose"));
        view.InsertAt(1, BuiltIn("Arpeggiator"));
        app.Settle();

        // Built-in to plugin, then plugin to plugin (the same plugin: a fresh instance), then plugin to built-in.
        Assert.True(await view.ReplaceAsync(view.Devices[1].Id, PluginDeviceHost.DefinitionOf(Sine)));
        app.Settle();
        Assert.Equal(["Transpose", Sine.DisplayName], ChainNames(app));
        var first = ChainIds(app)[1];
        Assert.True(await view.ReplaceAsync(first, PluginDeviceHost.DefinitionOf(Sine)));
        app.Settle();
        Assert.NotEqual(first, ChainIds(app)[1]);
        Assert.True(await view.ReplaceAsync(ChainIds(app)[1], BuiltIn("Arpeggiator")));
        app.Settle();
        Assert.Equal(["Transpose", "Arpeggiator"], ChainNames(app));

        // The menu path: the device selected in the Browser replaces the chosen device.
        app.ViewModel.Browser.SelectedItem = Assert.Single(app.ViewModel.Browser.Items, i => i.Name == "Transpose");
        await view.Devices[1].ReplaceWithBrowserSelectionCommand.ExecuteAsync(null);
        app.Settle();
        Assert.Equal(["Transpose", "Transpose"], ChainNames(app));
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(["Transpose", "Arpeggiator"], ChainNames(app));

        // A plugin that cannot start leaves the chain exactly as it was, and says so.
        var before = app.Session.Project;
        Assert.False(await view.ReplaceAsync(ChainIds(app)[1], PluginDeviceHost.DefinitionOf(broken)));
        app.Settle();
        Assert.Same(before, app.Session.Project);
        Assert.Contains(app.ViewModel.Messages, m => m.Text.Contains("was not replaced", StringComparison.Ordinal));
    });

    private static Point PanelMiddle(TopLevel window, DeviceViewControl control, int index)
    {
        var chain = AppUnderTest.Within<ItemsControl>(control, c => c.Name == "Chain");
        var panel = ((Control)chain.ContainerFromIndex(index)!).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("devicePanel"));
        return panel.TranslatePoint(new Point(panel.Bounds.Width / 2, 60), window)!.Value;
    }

    private static ProjectSession TwoTracks()
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Lead", TrackRole.Instrument)));
        session.Execute(ProjectCommands.AddTrack(Track.Create("Pad", TrackRole.Instrument)));
        return session;
    }

    private static PluginHostManager Plugins()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
        return new PluginHostManager(new PluginHostOptions { DataDirectory = directory, RequestTimeout = TimeSpan.FromSeconds(15) });
    }

    /// <summary>The main window with the Browser shown (listing a plugin instrument and audio effect) and three built-in devices on Lead.</summary>
    private static async Task<AppUnderTest> OpenWithChainAsync(PluginHostManager manager)
    {
        var app = await AppUnderTest.OpenAsync(TwoTracks(), s => new PluginDeviceHost(manager, s, [Sine, Gain]));
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, BuiltIn("Transpose"));
        view.InsertAt(1, BuiltIn("Event Filter"));
        view.InsertAt(2, BuiltIn("Arpeggiator"));
        app.Settle();
        return app;
    }

    private static DeviceViewControl Docked(AppUnderTest app) =>
        app.Find<ContentControl>("DockedDeviceView").GetVisualDescendants().OfType<DeviceViewControl>().Single();

    private static string[] ChainNames(AppUnderTest app) =>
        [.. app.Session.Project.ChainOf(app.ViewModel.Tracks[0].Id)!.Devices.Select(d => d.DisplayName)];

    private static DeviceId[] ChainIds(AppUnderTest app) =>
        [.. app.Session.Project.ChainOf(app.ViewModel.Tracks[0].Id)!.Devices.Select(d => d.Id)];

    private static Point AfterLastPanel(AppUnderTest app, DeviceViewControl control) => AfterLastPanel(app.Window, control);

    /// <summary>A point just right of the centre of the last panel in <paramref name="control"/>: the gap after the last device.</summary>
    private static Point AfterLastPanel(TopLevel window, DeviceViewControl control)
    {
        var chain = AppUnderTest.Within<ItemsControl>(control, c => c.Name == "Chain");
        var last = (Control)chain.ContainerFromIndex(chain.ItemCount - 1)!;
        return last.TranslatePoint(new Point(last.Bounds.Width * 0.9, 60), window)!.Value;
    }

    /// <summary>Presses on <paramref name="item"/> in the docked Browser and moves past the drag threshold.</summary>
    private static Point StartBrowserDrag(AppUnderTest app, BrowserItem item)
    {
        var list = AppUnderTest.Within<ListBox>(app.Find<BrowserView>("DockedBrowser"), l => l.Name == "ItemList");
        list.ScrollIntoView(item);
        app.Settle();
        var row = (Control)list.ContainerFromItem(item)!;
        return Press(app.Window, row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), app.Window)!.Value);
    }

    /// <summary>Presses on <paramref name="panel"/>'s name (its grip) in <paramref name="control"/> and moves past the drag threshold.</summary>
    private static Point StartGripDrag(TopLevel window, DeviceViewControl control, DevicePanelViewModel panel)
    {
        var grip = control.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("grip") && t.IsEffectivelyVisible && ReferenceEquals(t.DataContext, panel));
        return Press(window, grip.TranslatePoint(new Point(Math.Min(10, grip.Bounds.Width / 2), grip.Bounds.Height / 2), window)!.Value);
    }

    /// <summary>Presses at <paramref name="at"/>, moves under the threshold and then past it, and returns where the pointer is.</summary>
    private static Point Press(TopLevel window, Point at)
    {
        var end = at + new Point(12, 6);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Point(3, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        return end;
    }

    /// <summary>The button comes up once the platform's session has ended.</summary>
    private static void FinishGesture(TopLevel window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        window.UpdateLayout();
    }

    /// <summary>A started drag: what the platform was given, and the session's pending result.</summary>
    internal sealed record StartedDrag(IDataTransfer Transfer, DragDropEffects Allowed, ChainDrag Chain, TaskCompletionSource<DragDropEffects> Result);

    /// <summary>
    /// Stands in for the platform's drag source while installed. It checks each transfer against the native contract as it is
    /// handed over, then leaves the session open (as AppKit does) until the test drops it somewhere or cancels it. Avalonia's
    /// reference assemblies make <see cref="IPlatformDragSource"/> unimplementable and hide the service locator, so it is a
    /// runtime proxy bound through reflection. Disposing restores whatever drag source was bound before.
    /// </summary>
    internal sealed class RecordingDragSource : IDisposable
    {
        private readonly IPlatformDragSource? _previous;

        private RecordingDragSource(IPlatformDragSource? previous) => _previous = previous;

        public List<StartedDrag> Drags { get; } = [];

        public static RecordingDragSource Install()
        {
            var current = Locator("Current");
            var previous = (IPlatformDragSource?)current.GetType().GetMethod("GetService", [typeof(Type)])!.Invoke(current, [typeof(IPlatformDragSource)]);
            var source = new RecordingDragSource(previous);
            var proxy = DispatchProxy.Create<IPlatformDragSource, DragSourceProxy>();
            ((DragSourceProxy)(object)proxy).Recorder = source;
            Bind(proxy);
            return source;
        }

        private static object Locator(string property) =>
            typeof(AvaloniaLocator).GetProperty(property, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            ?? throw new InvalidOperationException($"AvaloniaLocator.{property} is not set.");

        private static void Bind(IPlatformDragSource? dragSource)
        {
            var mutable = Locator("CurrentMutable");
            var registration = mutable.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(IPlatformDragSource)).Invoke(mutable, null)!;
            registration.GetType().GetMethod("ToConstant")!.MakeGenericMethod(typeof(IPlatformDragSource)).Invoke(registration, [dragSource]);
        }

        internal Task<DragDropEffects> Start(IDataTransfer dataTransfer, DragDropEffects allowedEffects)
        {
            var chain = dataTransfer.TryGetValue(ChainDrag.Format);
            Assert.NotNull(chain);
            AssertNativeContract(dataTransfer, Forbidden(chain));
            var drag = new StartedDrag(dataTransfer, allowedEffects, chain, new TaskCompletionSource<DragDropEffects>());
            Drags.Add(drag);
            return drag.Result.Task;
        }

        /// <summary>Carries <paramref name="drag"/> into <paramref name="window"/> at <paramref name="at"/> and drops it there: the effect the target accepted, or none.</summary>
        public DragDropEffects DropAt(StartedDrag drag, TopLevel window, Point at)
        {
            var offered = DragDropEffects.None;
            var accepted = false;
            void OnOver(object? sender, DragEventArgs e) => offered = e.Handled ? e.DragEffects : DragDropEffects.None;
            void OnDrop(object? sender, DragEventArgs e) => accepted = e.Handled;
            window.AddHandler(DragDrop.DragEnterEvent, OnOver, RoutingStrategies.Bubble, handledEventsToo: true);
            window.AddHandler(DragDrop.DragOverEvent, OnOver, RoutingStrategies.Bubble, handledEventsToo: true);
            window.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
            try
            {
                window.DragDrop(at, RawDragEventType.DragEnter, drag.Transfer, drag.Allowed);
                window.DragDrop(at, RawDragEventType.DragOver, drag.Transfer, drag.Allowed);
                window.DragDrop(at, RawDragEventType.Drop, drag.Transfer, drag.Allowed);
            }
            finally
            {
                window.RemoveHandler(DragDrop.DragEnterEvent, OnOver);
                window.RemoveHandler(DragDrop.DragOverEvent, OnOver);
                window.RemoveHandler(DragDrop.DropEvent, OnDrop);
            }

            return accepted ? offered & drag.Allowed : DragDropEffects.None;
        }

        /// <summary>Ends the platform's session for <paramref name="drag"/> with <paramref name="effect"/> (none: cancelled).</summary>
        public static void Finish(StartedDrag drag, DragDropEffects effect)
        {
            drag.Result.SetResult(effect);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            foreach (var drag in Drags)
            {
                drag.Result.TrySetResult(DragDropEffects.None);
            }

            Bind(_previous);
        }
    }

    /// <summary>The <see cref="IPlatformDragSource"/> the app sees while a <see cref="RecordingDragSource"/> is installed (DispatchProxy needs it public and unsealed).</summary>
    public class DragSourceProxy : DispatchProxy
    {
        internal RecordingDragSource? Recorder { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("DoDragDropAsync", targetMethod?.Name);
            Assert.NotNull(args);
            return Recorder!.Start(Assert.IsAssignableFrom<IDataTransfer>(args[1]), Assert.IsType<DragDropEffects>(args[2]));
        }
    }
}
