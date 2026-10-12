using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The device-centric workspace in the real window (ADR 0036): the Device View and Browser are there, a Browser item dropped on a
/// chain lands at the marked gap, views detach into their own windows, and the design tokens and code palette agree. Frames are
/// saved at laptop and desktop sizes for looking at.
/// </summary>
public sealed class DeviceViewTests
{
    private static ProjectSession TwoTracks()
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Lead", TrackRole.Instrument)));
        session.Execute(ProjectCommands.AddTrack(Track.Create("Pad", TrackRole.Instrument)));
        return session;
    }

    private static DeviceDefinition Definition(string name) => Signal.DeviceCatalog.BuiltIn.Definitions.Single(d => d.Name == name);

    private static async Task<AppUnderTest> OpenWithChainAsync(double width, double height)
    {
        var app = await AppUnderTest.OpenAsync(TwoTracks());
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, Definition("Transpose"));
        view.InsertAt(1, Definition("Event Filter"));
        view.InsertAt(2, Definition("Arpeggiator"));
        app.Settle();
        return app;
    }

    [Fact]
    public Task TheDeviceViewAndBrowser_AreInTheWorkspace_AndShowTheSelectedTracksChain() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenWithChainAsync(1440, 900);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();

        var deviceView = app.Find<ContentControl>("DockedDeviceView");
        var chain = AppUnderTest.Within<ItemsControl>(deviceView, c => c.Name == "Chain");
        Assert.Equal(3, chain.ItemCount);
        Assert.True(app.Find<BrowserView>("DockedBrowser").IsEffectivelyVisible);
        var knob = deviceView.GetVisualDescendants().OfType<Knob>().First(k => k.IsEffectivelyVisible);
        Assert.Equal(Palette.SignalNote, knob.Accent);
        var switches = deviceView.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(t => t.Classes.Contains("paramSwitch") && t.IsEffectivelyVisible).ToList();
        Assert.Equal(4, switches.Count);
        app.Click(switches[0]);
        Assert.Equal(0, app.ViewModel.DeviceView!.Devices[1].Parameters[0].Value);
        Assert.Equal("Lead", AppUnderTest.Within<TextBlock>(deviceView, t => t.Name == "HeadingText").Text);

        app.ViewModel.Select(app.ViewModel.Tracks[1]);
        app.Settle();
        Assert.Equal("Pad", AppUnderTest.Within<TextBlock>(deviceView, t => t.Name == "HeadingText").Text);
        Assert.Equal(0, chain.ItemCount);
        app.SaveFrame("device-view-empty-chain");
    });

    [Fact]
    public Task AKnob_NudgesASteppedParameterOneStep_AndAContinuousOneFinely() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenWithChainAsync(1440, 900);
        var deviceView = app.Find<ContentControl>("DockedDeviceView");
        var vm = app.ViewModel.DeviceView!;
        Knob KnobFor(int device, ParameterId id) => deviceView.GetVisualDescendants().OfType<Knob>().Single(k => k.DataContext == vm.Devices[device].Parameters.Single(p => p.Id == id));
        double ValueOf(int device, ParameterId id) => vm.Devices[device].Parameters.Single(p => p.Id == id).Value;

        // Arpeggiator Rate: 0 to 3 in three steps, default 2. The arrow keys and the wheel move one position, with or without Shift.
        var rate = KnobFor(2, BuiltInDevices.ArpeggiatorRate);
        rate.Focus();
        app.Press(Key.Left);
        app.Settle();
        Assert.Equal(1, ValueOf(2, BuiltInDevices.ArpeggiatorRate));
        app.Press(Key.Right, RawInputModifiers.Shift);
        app.Settle();
        Assert.Equal(2, ValueOf(2, BuiltInDevices.ArpeggiatorRate));
        app.Window.MouseWheel(app.InWindow(rate, new Point(rate.Bounds.Width / 2, rate.Bounds.Height / 2)), new Vector(0, 1));
        app.Settle();
        Assert.Equal(3, ValueOf(2, BuiltInDevices.ArpeggiatorRate));

        // Transpose: one semitone per press, not two.
        var semitones = KnobFor(0, BuiltInDevices.TransposeSemitones);
        semitones.Focus();
        app.Press(Key.Right);
        app.Settle();
        Assert.Equal(1, ValueOf(0, BuiltInDevices.TransposeSemitones));

        // Arpeggiator Gate is continuous: 2% of its range, or 0.2% with Shift.
        var gate = KnobFor(2, BuiltInDevices.ArpeggiatorGate);
        Assert.Equal(0, gate.Step);
        var before = ValueOf(2, BuiltInDevices.ArpeggiatorGate);
        gate.Focus();
        app.Press(Key.Right, RawInputModifiers.Shift);
        app.Settle();
        Assert.InRange(ValueOf(2, BuiltInDevices.ArpeggiatorGate) - before, 0.0005, 0.005);
    });

    [Fact]
    public Task ABrowserItemDroppedOnTheChain_IsInsertedAtTheGapUnderThePointer() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenWithChainAsync(1440, 900);
        var deviceView = app.Find<ContentControl>("DockedDeviceView");
        var chain = AppUnderTest.Within<ItemsControl>(deviceView, c => c.Name == "Chain");
        var first = (Control)chain.ContainerFromIndex(0)!;
        var second = (Control)chain.ContainerFromIndex(1)!;

        // Between the first and second panels: right of the first's centre, left of the second's.
        var left = first.TranslatePoint(new Point(first.Bounds.Width * 0.9, 60), app.Window)!.Value;
        var right = second.TranslatePoint(new Point(second.Bounds.Width * 0.1, 60), app.Window)!.Value;
        var point = new Point((left.X + right.X) / 2, left.Y);
        var transfer = ChainDrag.Insert(Definition("Transpose")).ToTransfer();

        app.Window.DragDrop(point, RawDragEventType.DragEnter, transfer, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
        app.Settle();
        Assert.True(AppUnderTest.Within<Control>(deviceView, c => c.Name == "InsertionMarker").IsVisible);
        app.SaveFrame("device-view-insertion-marker");

        app.Window.DragDrop(point, RawDragEventType.Drop, transfer, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
        app.Settle();

        var chainState = app.Session.Project.ChainOf(app.ViewModel.Tracks[0].Id)!;
        Assert.Equal(["Transpose", "Transpose", "Event Filter", "Arpeggiator"], chainState.Devices.Select(d => d.DisplayName));
        Assert.Equal(1, app.ViewModel.DeviceView!.SelectedDevice!.Index);
    });

    [Fact]
    public Task ADeviceDraggedAlongTheChain_MovesToTheGap() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenWithChainAsync(1440, 900);
        var deviceView = app.Find<ContentControl>("DockedDeviceView");
        var chain = AppUnderTest.Within<ItemsControl>(deviceView, c => c.Name == "Chain");
        var last = (Control)chain.ContainerFromIndex(2)!;
        var vm = app.ViewModel.DeviceView!;
        var transfer = ChainDrag.Move(vm.Devices[0].Id, vm).ToTransfer();
        var point = last.TranslatePoint(new Point(last.Bounds.Width * 0.9, 60), app.Window)!.Value;

        app.Window.DragDrop(point, RawDragEventType.DragEnter, transfer, DragDropEffects.Move, RawInputModifiers.None);
        app.Window.DragDrop(point, RawDragEventType.Drop, transfer, DragDropEffects.Move, RawInputModifiers.None);
        app.Settle();

        Assert.Equal(["Event Filter", "Arpeggiator", "Transpose"], vm.Devices.Select(d => d.Name));
    });

    [Fact]
    public Task CollapsingADeviceInOneView_LeavesItExpandedInAnother_AndADetachedViewGetsItsOwnWindow() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenWithChainAsync(1440, 900);
        var first = app.ViewModel.DeviceView!;
        var second = app.ViewModel.Workspace.NewDeviceView();
        second.DeviceView!.IsLocked = true;
        second.DetachCommand.Execute(null);
        app.Settle();

        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        Assert.True(window.IsVisible);
        first.Devices[1].IsCollapsed = true;
        app.Settle();

        Assert.False(second.DeviceView.Devices[1].IsCollapsed);
        Assert.Equal(first.Devices.Select(d => d.Id), second.DeviceView.Devices.Select(d => d.Id));
        window.Width = 900;
        window.Height = 340;
        app.SaveFrame("device-view-detached", window);
        app.SaveFrame("device-view-collapsed-in-main");

        window.Close();
        app.Settle();
        Assert.True(second.IsDocked);
    });

    [Theory]
    [InlineData(1280, 800, "laptop")]
    [InlineData(1920, 1080, "desktop")]
    public Task TheWorkspace_RendersAtCommonWindowSizes(double width, double height, string name) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenWithChainAsync(width, height);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.DeviceView!.SelectedDevice = app.ViewModel.DeviceView.Devices[1];
        app.Settle();

        var path = app.SaveFrame($"workspace-{name}");
        Assert.True(File.Exists(path));
        var deviceView = app.Find<ContentControl>("DockedDeviceView");
        Assert.True(deviceView.Bounds.Height >= 200, $"The Device View is {deviceView.Bounds.Height} high.");
        AssertPanelsFit(deviceView);

        app.ViewModel.Workspace.Browser.CollapseCommand.Execute(null);
        app.ViewModel.Workspace.Inspector.CollapseCommand.Execute(null);
        app.Settle();
        app.SaveFrame($"workspace-{name}-tools-collapsed");
        Assert.NotEmpty(app.ViewModel.Workspace.RightRail);
    });

    /// <summary>Every device panel is wholly inside the Device View's viewport, and every control is wholly inside its panel.</summary>
    private static void AssertPanelsFit(Control deviceView)
    {
        var scroller = AppUnderTest.Within<ScrollViewer>(deviceView, c => c.Name == "ChainScroller");
        var panels = deviceView.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("devicePanel") && b.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(panels);
        foreach (var panel in panels)
        {
            var box = panel.TranslatePoint(default, scroller)!.Value;
            Assert.True(box.Y >= 0 && box.Y + panel.Bounds.Height <= scroller.Viewport.Height + 0.5, $"A panel is cut off: top {box.Y}, height {panel.Bounds.Height}, viewport {scroller.Viewport.Height}.");
            foreach (var control in panel.GetVisualDescendants().OfType<Control>().Where(c => c is Knob or Avalonia.Controls.Primitives.ToggleButton or Button && c.IsEffectivelyVisible))
            {
                var at = control.TranslatePoint(default, panel)!.Value;
                Assert.True(
                    at.X >= 0 && at.Y >= 0 && at.X + control.Bounds.Width <= panel.Bounds.Width + 0.5 && at.Y + control.Bounds.Height <= panel.Bounds.Height + 0.5,
                    $"{control.GetType().Name} {Avalonia.Automation.AutomationProperties.GetName(control)} at {at} ({control.Bounds.Size}) overflows its {panel.Bounds.Size} panel.");
            }
        }
    }

    [Fact]
    public Task TheCodePalette_MatchesTheDesignTokens() => HeadlessApp.RunAsync(() =>
    {
        var tokens = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://Bluestone.Desktop/Theme/Tokens.axaml"));
        Color Token(string key) => Assert.IsType<Color>(tokens[key]);
        var pairs = new (Color Code, string Token)[]
        {
            (Palette.Base, "Surface.BaseColor"),
            (Palette.Panel, "Surface.PanelColor"),
            (Palette.Raised, "Surface.RaisedColor"),
            (Palette.Divider, "Line.DividerColor"),
            (Palette.Border, "Line.BorderColor"),
            (Palette.Field, "Surface.FieldColor"),
            (Palette.Text, "Text.PrimaryColor"),
            (Palette.TextSecondary, "Text.SecondaryColor"),
            (Palette.TextMuted, "Text.MutedColor"),
            (Palette.Accent, "Accent.Color"),
            (Palette.Focus, "State.FocusColor"),
            (Palette.Record, "State.ErrorColor"),
            (Palette.Cycle, "Transport.CycleColor"),
            (Palette.Lane, "Lane.BackgroundColor"),
            (Palette.AutomationLane, "Lane.AutomationColor"),
            (Palette.Ruler, "Lane.RulerColor"),
            (Palette.SignalNote, "Signal.NoteColor"),
            (Palette.SignalInstrument, "Signal.InstrumentColor"),
            (Palette.SignalAudio, "Signal.AudioColor"),
            (Palette.SignalOther, "Signal.OtherColor"),
        };

        Assert.All(pairs, p => Assert.Equal(Token(p.Token), p.Code));
        return Task.CompletedTask;
    });
}
