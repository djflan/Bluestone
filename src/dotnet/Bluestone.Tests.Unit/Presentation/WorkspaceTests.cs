using System.Collections.Immutable;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>
/// The semantic workspace and its Device Views (ADR 0036): follow and lock, several views over one chain with their own
/// presentation, insertion and reordering, Browser targeting, and local layout state that never touches the project.
/// </summary>
public sealed class WorkspaceTests : IAsyncLifetime
{
    private static readonly ScreenArea Laptop = new(0, 0, 1440, 900);
    private static readonly ScreenArea SideMonitor = new(1440, 0, 2560, 1440);

    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-workspace-").FullName;
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _endpoints;
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;

    public WorkspaceTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _endpoints = new EndpointDirectory([_provider]);
        var playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _endpoints, new NoUi(), new ImmediateDispatcher())
        {
            LayoutStore = new WorkspaceStore(Path.Combine(_directory, "settings")),
        };
    }

    public async ValueTask InitializeAsync()
    {
        await _vm.InitializeAsync();
        _session.Execute(ProjectCommands.AddTrack(Track.Create("Keys", TrackRole.Instrument)));
        _session.Execute(ProjectCommands.AddTrack(Track.Create("Bass", TrackRole.Instrument)));
        await Settle();
        _vm.Select(_vm.Tracks[0]);
        await Settle();
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _endpoints.Dispose();
        _provider.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    private TrackViewModel Keys => _vm.Tracks[0];

    private TrackViewModel Bass => _vm.Tracks[1];

    private DeviceViewViewModel First => _vm.Workspace.DeviceViews.First();

    private static DeviceDefinition Definition(string name) => DeviceCatalog.BuiltIn.Definitions.Single(d => d.Name == name);

    private BrowserItem Item(string name) => _vm.Browser.Items.Single(i => i.Name == name);

    [Fact]
    public async Task AnUnlockedDeviceView_FollowsTheSelectedTrack()
    {
        Assert.False(First.IsLocked);
        Assert.Equal("Keys", First.Heading);

        _vm.Select(Bass);
        await Settle();

        Assert.Equal("Bass", First.Heading);
        Assert.Equal("Follows selection", First.LockText);
    }

    [Fact]
    public async Task ALockedDeviceView_KeepsItsTrack_AndSaysSoWhenTheTrackIsRemoved()
    {
        First.IsLocked = true;
        _vm.Select(Bass);
        await Settle();

        Assert.Equal("Keys", First.Heading);
        Assert.True(First.IsLocked);

        _session.Execute(ProjectCommands.RemoveTrack(Keys.Id));
        await Settle();

        Assert.True(First.IsUnavailable);
        Assert.False(First.HasChain);
        Assert.Contains("locked to has been removed", First.EmptyText, StringComparison.Ordinal);
        Assert.False(First.InsertAt(0, Definition("Transpose")));
    }

    [Fact]
    public async Task LockingWithNothingShown_StaysUnlocked()
    {
        _session.Execute(ProjectCommands.RemoveTracks([Keys.Id, Bass.Id]));
        await Settle();

        First.IsLocked = true;

        Assert.False(First.IsLocked);
    }

    [Fact]
    public async Task TwoDeviceViews_ShareTheChain_ButNotTheirCollapseSelectionOrScroll()
    {
        Assert.True(First.InsertAt(0, Definition("Transpose")));
        Assert.True(First.InsertAt(1, Definition("Arpeggiator")));
        var second = _vm.Workspace.NewDeviceView().DeviceView!;
        await Settle();

        Assert.Equal(2, _vm.Workspace.DeviceViews.Count());
        Assert.Equal(First.Devices.Select(d => d.Id), second.Devices.Select(d => d.Id));
        Assert.Single(_session.Project.Chains);

        First.Devices[0].IsCollapsed = true;
        First.SelectedDevice = First.Devices[1];
        First.ScrollOffset = 120;
        await Settle();

        Assert.True(First.Devices[0].IsCollapsed);
        Assert.Equal(DevicePanelSize.Collapsed, First.Devices[0].Size);
        Assert.False(second.Devices[0].IsCollapsed);
        Assert.Null(second.SelectedDevice);
        Assert.Equal(0, second.ScrollOffset);

        // Device state is the project's: a bypass or parameter change in one view shows in the other.
        second.Devices[0].IsBypassed = true;
        second.Devices[0].Parameters.Single(p => p.Name == "Semitones").Value = 7;
        await Settle();

        Assert.True(First.Devices[0].IsBypassed);
        Assert.Equal(7, First.Devices[0].Parameters.Single(p => p.Name == "Semitones").Value);
        Assert.True(First.Devices[0].IsCollapsed);
        Assert.False(second.Devices[0].IsCollapsed);
    }

    [Fact]
    public async Task ADropInsertsAtItsGap_AndADraggedDeviceMovesToItsGap()
    {
        First.InsertAt(0, Definition("Transpose"));
        First.InsertAt(1, Definition("Arpeggiator"));
        First.InsertAt(1, Definition("Event Filter"));
        await Settle();
        Assert.Equal(["Transpose", "Event Filter", "Arpeggiator"], First.Devices.Select(d => d.Name));

        Assert.True(First.MoveToGap(First.Devices[0].Id, 3));
        await Settle();
        Assert.Equal(["Event Filter", "Arpeggiator", "Transpose"], First.Devices.Select(d => d.Name));

        Assert.True(First.MoveToGap(First.Devices[2].Id, 0));
        await Settle();
        Assert.Equal(["Transpose", "Event Filter", "Arpeggiator"], First.Devices.Select(d => d.Name));

        var history = _session.History.UndoLabel;
        Assert.True(First.MoveToGap(First.Devices[1].Id, 2));
        Assert.Equal(history, _session.History.UndoLabel);
        Assert.False(First.MoveToGap(DeviceId.New(), 0));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(50, 0)]
    [InlineData(150, 1)]
    [InlineData(250, 2)]
    [InlineData(400, 3)]
    public void GapAt_IsBeforeTheFirstPanelWhoseCentreIsRightOfThePointer(double x, int gap) =>
        Assert.Equal(gap, DeviceViewViewModel.GapAt([100, 200, 300], x));

    [Fact]
    public void GapAt_InAnEmptyChain_IsTheOnlyGap() => Assert.Equal(0, DeviceViewViewModel.GapAt([], 40));

    [Fact]
    public async Task TheBrowser_InsertsIntoTheMostRecentlyActiveDeviceView_AfterItsSelectedDevice()
    {
        First.InsertAt(0, Definition("Transpose"));
        First.InsertAt(1, Definition("Arpeggiator"));
        var second = _vm.Workspace.NewDeviceView().DeviceView!;
        second.LockToTrack(Bass.Id);
        await Settle();
        Assert.Same(second, _vm.Workspace.ActiveDeviceView);
        Assert.Contains("Bass", _vm.Browser.TargetText, StringComparison.Ordinal);

        Assert.True(_vm.Browser.InsertIntoActive(Item("Event Filter")));
        await Settle();
        Assert.Equal(["Event Filter"], second.Devices.Select(d => d.Name));

        First.Activate();
        First.SelectedDevice = First.Devices[0];
        Assert.True(_vm.Browser.InsertIntoActive(Item("Event Filter")));
        await Settle();

        Assert.Equal(["Transpose", "Event Filter", "Arpeggiator"], First.Devices.Select(d => d.Name));
        Assert.Contains("Keys", _vm.Browser.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBrowser_DoesNotGuess_WhenNoDeviceViewShowsAChain()
    {
        _session.Execute(ProjectCommands.RemoveTracks([Keys.Id, Bass.Id]));
        await Settle();

        Assert.False(_vm.Browser.HasTarget);
        Assert.False(_vm.Browser.InsertIntoActive(Item("Transpose")));
        Assert.Empty(_session.Project.Chains);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("was not inserted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBrowser_DoesNotInsertIntoACollapsedDeviceView()
    {
        First.Host!.CollapseCommand.Execute(null);
        await Settle();

        Assert.False(_vm.Browser.HasTarget);
        Assert.False(_vm.Browser.InsertIntoActive(Item("Transpose")));
        Assert.Empty(_session.Project.Chains);
    }

    [Fact]
    public async Task TheBrowser_Recommends_ButDoesNotForbid()
    {
        // An empty chain recommends note devices and instruments, but everything that is a device can still be inserted.
        Assert.True(Item("Transpose").IsRecommended);
        Assert.All(_vm.Browser.Items.Where(i => i.Kind == BrowserItemKind.Device), i => Assert.True(i.IsInsertable));

        _vm.Browser.SearchText = "arp";
        Assert.Equal(["Arpeggiator"], _vm.Browser.Items.Where(i => i.IsInsertable).Select(i => i.Name));
        _vm.Browser.SelectedCategory = _vm.Browser.Categories.Single(c => c.Category == BrowserCategory.Instruments);
        Assert.True(_vm.Browser.IsEmpty);
        Assert.False(string.IsNullOrEmpty(_vm.Browser.EmptyText));
        await Settle();
    }

    [Fact]
    public async Task ADeviceWhosePluginIsMissing_IsShownAsUnavailable_AndKeepsItsPlace()
    {
        var missing = DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId("vst3:00000000000000000000000000000000"), "Vintage Synth"));
        _session.Execute(DeviceCommands.InsertDevice(Keys.Id, DeviceInstance.Create(Definition("Transpose").ToReference()), DeviceCatalog.BuiltIn.Lookup, 0));
        var chain = _session.Project.ChainOf(Keys.Id)!;
        _session.Execute(new ProjectCommand("Add a missing plugin", p => p.WithChain(chain with { Devices = chain.Devices.Add(missing) })));
        await Settle();

        var panel = First.Devices[1];
        Assert.Equal("Vintage Synth", panel.Name);
        Assert.Equal(DeviceRole.Unavailable, panel.Role);
        Assert.Equal("Not installed", panel.Status);
        Assert.True(panel.NeedsAttention);
        Assert.Empty(panel.Parameters);
        Assert.Null(panel.Definition);
        Assert.Equal(DeviceRole.Note, First.Devices[0].Role);
    }

    [Fact]
    public async Task TheWorkspace_RoundTripsThroughItsSerializedState()
    {
        First.InsertAt(0, Definition("Transpose"));
        First.LockToTrack(Keys.Id);
        First.Devices[0].IsCollapsed = true;
        First.ScrollOffset = 64;
        var second = _vm.Workspace.NewDeviceView();
        second.RememberBounds(DisplayConfiguration.Signature([Laptop, SideMonitor]), new WindowBounds(1600, 100, 900, 320));
        second.DetachCommand.Execute(null);
        _vm.Workspace.Browser.CollapseCommand.Execute(null);
        _vm.Workspace.LeftExtent = 300;
        _vm.Browser.SearchText = "arp";
        await Settle();

        var json = WorkspaceStore.Serialize(_vm.CaptureLayout());
        var state = WorkspaceStore.Deserialize(json)!;

        Assert.Equal(json, WorkspaceStore.Serialize(state));
        Assert.Equal(3 + 1, state.Views.Length);
        var device = state.Views.Single(v => v.Id == First.Id).Device!;
        Assert.Equal(Keys.Id.Value, device.LockedTrack);
        Assert.Single(device.Collapsed);
        Assert.Equal(64, device.ScrollOffset);
        Assert.Equal(ViewPlacement.Detached, state.Views.Single(v => v.Id == second.Id).Placement);
        Assert.Equal(ViewPlacement.Collapsed, state.Views.Single(v => v.Kind == WorkspaceViewKind.Browser).Placement);
        Assert.Equal("arp", state.Browser.Search);

        await using var restored = NewViewModel();
        await restored.InitializeAsync();
        restored.Screens = () => [Laptop, SideMonitor];
        restored.ApplyLayout(state);

        var views = restored.Workspace.DeviceViews.ToList();
        Assert.Equal(2, views.Count);
        Assert.True(views[0].IsLocked);
        Assert.Equal(64, views[0].ScrollOffset);
        Assert.True(views[1].Host!.IsDetached);
        Assert.True(restored.Workspace.Browser.IsCollapsed);
        Assert.Equal(300, restored.Workspace.LeftExtent);
        Assert.Equal("arp", restored.Browser.SearchText);
    }

    [Fact]
    public async Task ADetachedViewWhoseMonitorIsMissing_ComesBackAsARailIcon_AndKeepsItsWindowForLater()
    {
        var dual = DisplayConfiguration.Signature([Laptop, SideMonitor]);
        var view = _vm.Workspace.NewDeviceView();
        view.RememberBounds(dual, new WindowBounds(1600, 100, 900, 320));
        view.DetachCommand.Execute(null);
        await Settle();
        var state = _vm.CaptureLayout();

        await using var laptopOnly = NewViewModel();
        await laptopOnly.InitializeAsync();
        laptopOnly.Screens = () => [Laptop];
        laptopOnly.ApplyLayout(state);

        var recovered = laptopOnly.Workspace.Views.Single(v => v.Id == view.Id);
        Assert.True(recovered.IsCollapsed);
        Assert.True(recovered.IsAwaitingDisplay);
        Assert.Contains(recovered, laptopOnly.Workspace.LeftRail);

        // Saving while on one display keeps the two-monitor layout.
        var saved = laptopOnly.CaptureLayout().Views.Single(v => v.Id == view.Id);
        Assert.Equal(ViewPlacement.Detached, saved.Placement);
        Assert.Equal(new WindowBounds(1600, 100, 900, 320), saved.DetachedBounds[dual]);
    }

    [Fact]
    public async Task SavingTheLayout_DoesNotEditTheProject()
    {
        var before = _session.Project;
        var dirty = _session.IsDirty;
        _vm.Workspace.NewDeviceView();
        First.ScrollOffset = 30;
        _vm.Workspace.Inspector.CollapseCommand.Execute(null);
        _vm.SaveLayout();
        await Settle();

        Assert.Same(before, _session.Project);
        Assert.Equal(dirty, _session.IsDirty);
        Assert.True(File.Exists(_vm.LayoutStore!.DefaultsPath));
    }

    [Fact]
    public void TheDefaults_KeepTheLayout_ButNotThisProjectsBindings()
    {
        var track = Guid.NewGuid();
        var state = new WorkspaceState
        {
            Views = [new ViewState { Id = "device-view-1", Kind = WorkspaceViewKind.DeviceView, Placement = ViewPlacement.Docked, Device = new DeviceViewState { LockedTrack = track, Collapsed = [Guid.NewGuid()], ScrollOffset = 50 } }],
            LeftExtent = 333,
        };

        var defaults = state.AsDefaults();

        Assert.Equal(333, defaults.LeftExtent);
        Assert.Equal(ViewPlacement.Docked, defaults.Views[0].Placement);
        Assert.Null(defaults.Views[0].Device!.LockedTrack);
        Assert.Empty(defaults.Views[0].Device!.Collapsed);
    }

    [Fact]
    public void TheStore_KeepsPerProjectState_AndFallsBackToTheUserDefaults()
    {
        var store = new WorkspaceStore(Path.Combine(_directory, "store"));
        var project = Path.Combine(_directory, "Song.bluestone");
        Assert.Null(store.Load(project));

        store.Save(project, new WorkspaceState { LeftExtent = 310, ActiveDeviceView = "device-view-2" });

        Assert.Equal("device-view-2", store.Load(project)!.ActiveDeviceView);
        Assert.Equal(310, store.Load(Path.Combine(_directory, "Other.bluestone"))!.LeftExtent);
        Assert.Equal(310, store.Load(null)!.LeftExtent);
        Assert.Null(WorkspaceStore.Deserialize("{ not json"));
        Assert.Null(WorkspaceStore.Deserialize("""{ "version": 99 }"""));
    }

    [Fact]
    public void LayoutRecovery_NeverRestoresOffScreen_AndPrefersTheLayoutForThisDisplaySetup()
    {
        var dual = DisplayConfiguration.Signature([Laptop, SideMonitor]);
        var single = DisplayConfiguration.Signature([Laptop]);
        var saved = ImmutableDictionary<string, WindowBounds>.Empty
            .Add(dual, new WindowBounds(1500, 50, 1800, 1000))
            .Add(single, new WindowBounds(20, 30, 1200, 800));

        Assert.Equal(new WindowBounds(1500, 50, 1800, 1000), LayoutRecovery.Restore(saved, [Laptop, SideMonitor]));
        Assert.Equal(new WindowBounds(20, 30, 1200, 800), LayoutRecovery.Restore(saved, [Laptop]));
        Assert.Null(LayoutRecovery.Restore(ImmutableDictionary<string, WindowBounds>.Empty.Add(dual, new WindowBounds(1500, 50, 1800, 1000)), [Laptop]));
        Assert.Null(LayoutRecovery.Restore(saved, []));

        Assert.False(LayoutRecovery.IsReachable(new WindowBounds(-5000, 0, 800, 600), [Laptop]));
        Assert.False(LayoutRecovery.IsReachable(new WindowBounds(100, -400, 800, 600), [Laptop]));
        Assert.True(LayoutRecovery.IsReachable(new WindowBounds(1000, 100, 800, 600), [Laptop]));

        var fitted = LayoutRecovery.Fit(new WindowBounds(1000, 100, 2000, 1200), [Laptop]);
        Assert.Equal(new WindowBounds(0, 0, 1440, 900), fitted);
    }

    [Fact]
    public void DisplayConfiguration_DoesNotDependOnTheOrderTheDisplaysAreListedIn() =>
        Assert.Equal(DisplayConfiguration.Signature([Laptop, SideMonitor]), DisplayConfiguration.Signature([SideMonitor, Laptop]));

    [Fact]
    public async Task ClosingADeviceView_KeepsAtLeastTheOthers_AndTheChainIsUntouched()
    {
        First.InsertAt(0, Definition("Transpose"));
        var second = _vm.Workspace.NewDeviceView();
        second.CloseCommand.Execute(null);
        await Settle();

        Assert.Single(_vm.Workspace.DeviceViews);
        Assert.Same(First, _vm.Workspace.ActiveDeviceView);
        Assert.Single(_session.Project.ChainOf(Keys.Id)!.Devices);
        Assert.False(_vm.Workspace.Inspector.CanClose);
    }

    [Fact]
    public void ParametersShown_GrowWithThePanelWidth() =>
        Assert.True(DevicePanelViewModel.ParametersShown(DevicePanelSize.Small) < DevicePanelViewModel.ParametersShown(DevicePanelSize.Medium)
            && DevicePanelViewModel.ParametersShown(DevicePanelSize.Medium) < DevicePanelViewModel.ParametersShown(DevicePanelSize.Large)
            && DevicePanelViewModel.ParametersShown(DevicePanelSize.Collapsed) == 0);

    private MainViewModel NewViewModel()
    {
        var playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        return new MainViewModel(_session, playback, _endpoints, new NoUi(), new ImmediateDispatcher());
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
