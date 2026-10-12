using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>What a workspace view shows. Editors in the detail region (piano roll, event list) stay tabs of that region.</summary>
public enum WorkspaceViewKind
{
    Inspector,
    Browser,
    DeviceView,
}

/// <summary>The semantic region a view belongs to (ADR 0036). Global chrome and the arranger are fixed; these regions host views.</summary>
public enum WorkspaceRegion
{
    /// <summary>The left side: the inspector and similar tools.</summary>
    Left,

    /// <summary>The right side: the Browser and similar palettes.</summary>
    Right,

    /// <summary>Below the arranger: Device Views and the editors.</summary>
    Detail,
}

/// <summary>Where a view is right now.</summary>
public enum ViewPlacement
{
    /// <summary>In its region, taking space from the rest of the workspace.</summary>
    Docked,

    /// <summary>Only an icon on the border rail.</summary>
    Collapsed,

    /// <summary>In a window of its own.</summary>
    Detached,
}

/// <summary>
/// One view instance in the workspace: an inspector, the Browser, or one of several Device Views. It owns presentation only; the
/// content it shows belongs to the project or to the view model it wraps (ADR 0036).
/// </summary>
public sealed partial class WorkspaceView : ObservableObject
{
    private readonly WorkspaceViewModel _workspace;

    internal WorkspaceView(WorkspaceViewModel workspace, string id, WorkspaceViewKind kind, object? content)
    {
        _workspace = workspace;
        Id = id;
        Kind = kind;
        Content = content;
    }

    /// <summary>Stable within the workspace, so saved layouts can find the view again.</summary>
    public string Id { get; }

    public WorkspaceViewKind Kind { get; }

    public WorkspaceRegion Region => Kind switch
    {
        WorkspaceViewKind.Inspector => WorkspaceRegion.Left,
        WorkspaceViewKind.Browser => WorkspaceRegion.Right,
        _ => WorkspaceRegion.Detail,
    };

    /// <summary>The <see cref="DeviceViewViewModel"/> or <see cref="BrowserViewModel"/> shown, or null for the inspector (the window composes it).</summary>
    public object? Content { get; }

    public DeviceViewViewModel? DeviceView => Content as DeviceViewViewModel;

    /// <summary>The inspector is tied to the main window; palettes and Device Views can have windows of their own.</summary>
    public bool CanDetach => Kind != WorkspaceViewKind.Inspector;

    /// <summary>Only Device Views come and go; the inspector and the Browser are always there, if only as an icon.</summary>
    public bool CanClose => Kind == WorkspaceViewKind.DeviceView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocked), nameof(IsCollapsed), nameof(IsDetached), nameof(IsOnRail))]
    public partial ViewPlacement Placement { get; private set; } = ViewPlacement.Docked;

    public bool IsDocked => Placement == ViewPlacement.Docked;

    public bool IsCollapsed => Placement == ViewPlacement.Collapsed;

    public bool IsDetached => Placement == ViewPlacement.Detached;

    /// <summary>Side tools always have a rail icon; Device Views get one while they are not docked as a tab.</summary>
    public bool IsOnRail => Region != WorkspaceRegion.Detail || Placement != ViewPlacement.Docked;

    [ObservableProperty]
    public partial string Title { get; internal set; } = string.Empty;

    /// <summary>A short label for the rail: "Inspector", "Browser", "Devices 2".</summary>
    [ObservableProperty]
    public partial string RailLabel { get; internal set; } = string.Empty;

    /// <summary>
    /// True while a detached view whose monitor is missing is shown collapsed instead. It is still saved as detached, with its
    /// bounds, so plugging the monitor back in restores it.
    /// </summary>
    public bool IsAwaitingDisplay { get; internal set; }

    /// <summary>A docked Device View whose tab is the one showing in the detail region.</summary>
    [ObservableProperty]
    public partial bool IsCurrentTab { get; internal set; }

    /// <summary>The window bounds for each display setup it has been detached on.</summary>
    public ImmutableDictionary<string, WindowBounds> DetachedBounds { get; internal set; } = ImmutableDictionary<string, WindowBounds>.Empty;

    /// <summary>Notes the bounds of this view's detached window on the display setup <paramref name="displaySignature"/>.</summary>
    public void RememberBounds(string displaySignature, WindowBounds bounds) =>
        DetachedBounds = DetachedBounds.SetItem(displaySignature, bounds);

    internal void SetPlacement(ViewPlacement placement)
    {
        IsAwaitingDisplay = false;
        Placement = placement;
    }

    internal void Recover() => Placement = ViewPlacement.Collapsed;

    /// <summary>A rail icon click: open a collapsed or detached view docked, or collapse a docked side tool.</summary>
    [RelayCommand]
    private void Toggle() => _workspace.Toggle(this);

    [RelayCommand]
    private void Dock() => _workspace.Dock(this);

    [RelayCommand]
    private void Collapse() => _workspace.Collapse(this);

    [RelayCommand]
    private void Detach() => _workspace.Detach(this);

    [RelayCommand]
    private void Close() => _workspace.Close(this);

    /// <summary>Brings the view forward: docks it if needed and, for a Device View, shows its tab.</summary>
    [RelayCommand]
    private void Show() => _workspace.Show(this);
}

/// <summary>
/// The semantic workspace (ADR 0036): fixed global chrome and arranger, a left and a right side region, a detail region below the
/// arranger, and border rails for collapsed views. Views share one hosting model and each has a home region; this is deliberately
/// not a free-form docking system.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public const double DefaultLeftExtent = 248;
    public const double DefaultRightExtent = 272;
    public const double DefaultDetailExtent = 330;
    public const double MinimumSideExtent = 200;
    public const double MaximumSideExtent = 520;
    public const double MinimumDetailExtent = 180;

    private readonly MainViewModel _owner;
    private int _nextDeviceView = 1;

    internal WorkspaceViewModel(MainViewModel owner, BrowserViewModel browser)
    {
        _owner = owner;
        Inspector = Add(new WorkspaceView(this, "inspector", WorkspaceViewKind.Inspector, null) { Title = "Inspector", RailLabel = "Inspector" });
        Browser = Add(new WorkspaceView(this, "browser", WorkspaceViewKind.Browser, browser) { Title = "Browser", RailLabel = "Browser" });
        var first = Add(CreateDeviceView(null));
        Retitle();
        ActiveDeviceView = first.DeviceView;
        Refresh();
    }

    /// <summary>Every open view, in the order they were opened.</summary>
    public ObservableCollection<WorkspaceView> Views { get; } = [];

    public WorkspaceView Inspector { get; }

    public WorkspaceView Browser { get; }

    /// <summary>The left border rail: the inspector, and Device Views that are collapsed or detached.</summary>
    public ObservableCollection<WorkspaceView> LeftRail { get; } = [];

    /// <summary>The right border rail: the Browser.</summary>
    public ObservableCollection<WorkspaceView> RightRail { get; } = [];

    /// <summary>The Device Views docked in the detail region, shown as tabs beside the editors.</summary>
    public ObservableCollection<WorkspaceView> DetailTabs { get; } = [];

    public IEnumerable<DeviceViewViewModel> DeviceViews => Views.Select(v => v.DeviceView).OfType<DeviceViewViewModel>();

    /// <summary>The Device View used last: where Browser insertions without a drop go, and whose chain the routing inspector shows.</summary>
    [ObservableProperty]
    public partial DeviceViewViewModel? ActiveDeviceView { get; private set; }

    /// <summary>The Device View shown in the detail region: the active one when it is docked, otherwise the first docked one.</summary>
    [ObservableProperty]
    public partial WorkspaceView? DetailDeviceView { get; private set; }

    [ObservableProperty]
    public partial double LeftExtent { get; set; } = DefaultLeftExtent;

    [ObservableProperty]
    public partial double RightExtent { get; set; } = DefaultRightExtent;

    [ObservableProperty]
    public partial double DetailExtent { get; set; } = DefaultDetailExtent;

    /// <summary>The main window's bounds for each display setup, kept so that one setup does not overwrite another's.</summary>
    public ImmutableDictionary<string, WindowBounds> MainWindowBounds { get; set; } = ImmutableDictionary<string, WindowBounds>.Empty;

    /// <summary>Raised when any view's placement changes, so the window can open, close, and lay out regions and windows.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>
    /// Raised after a saved layout was restored (at startup or when another project is opened), so the window can put itself
    /// and the detached windows where that layout has them.
    /// </summary>
    public event EventHandler? LayoutRestored;

    /// <summary>
    /// True while a saved layout is being applied. Windows closed meanwhile belong to the layout being replaced (already saved),
    /// so their bounds must not be noted on views that now hold the restored layout's bounds.
    /// </summary>
    public bool IsRestoring { get; private set; }

    partial void OnDetailDeviceViewChanged(WorkspaceView? value) => SyncTabs();

    /// <summary>Marks which Device View tab is showing, after the detail region's tab or content changed.</summary>
    internal void SyncTabs()
    {
        foreach (var view in Views)
        {
            view.IsCurrentTab = view == DetailDeviceView && _owner.LowerPane == LowerPane.Devices;
        }
    }

    partial void OnLeftExtentChanged(double value)
    {
        var clamped = Math.Clamp(value, MinimumSideExtent, MaximumSideExtent);
        if (clamped != value)
        {
            LeftExtent = clamped;
        }
    }

    partial void OnRightExtentChanged(double value)
    {
        var clamped = Math.Clamp(value, MinimumSideExtent, MaximumSideExtent);
        if (clamped != value)
        {
            RightExtent = clamped;
        }
    }

    partial void OnDetailExtentChanged(double value)
    {
        if (value < MinimumDetailExtent)
        {
            DetailExtent = MinimumDetailExtent;
        }
    }

    /// <summary>Opens another Device View, docked as a tab and following the selection, and makes it active.</summary>
    public WorkspaceView NewDeviceView()
    {
        var view = Add(CreateDeviceView(null));
        Retitle();
        Show(view);
        _owner.SyncDeviceViews();
        return view;
    }

    [RelayCommand]
    private void AddDeviceView() => NewDeviceView();

    /// <summary>
    /// Brings the selected track's chain into view, as the inspector's Instrument and Devices links do: the active Device View
    /// when it shows that track, otherwise the first one following the selection (docked again if it was collapsed), otherwise a
    /// new one. Selects <paramref name="device"/> there when given. A detached view is used where it is.
    /// </summary>
    public DeviceViewViewModel? RevealSelectedChain(DeviceId? device = null)
    {
        var view = ActiveDeviceView is { IsLocked: false, HasChain: true, IsEditingRack: false } active ? active
            : DeviceViews.FirstOrDefault(v => !v.IsLocked) ?? NewDeviceView().DeviceView;
        if (view?.Host is not { } host)
        {
            return null;
        }

        if (!host.IsDetached)
        {
            Show(host);
        }

        Activate(view);
        if (device is { } id && view.Devices.FirstOrDefault(d => d.Id == id) is { } panel)
        {
            view.SelectedDevice = panel;
        }

        return view;
    }

    /// <summary>Shows the Browser on its instruments, with the selected track's chain as where Return inserts.</summary>
    public void BrowseInstruments()
    {
        RevealSelectedChain();
        ShowBrowser(BrowserCategory.Instruments);
    }

    /// <summary>Docks the Browser unless it is open already (docked or in its own window) and shows <paramref name="category"/>.</summary>
    public void ShowBrowser(BrowserCategory category)
    {
        if (!Browser.IsDocked && !Browser.IsDetached)
        {
            Show(Browser);
        }

        _owner.Browser.ShowCategory(category);
    }

    /// <summary>Makes <paramref name="view"/> the one Browser insertions go to; called when the musician uses it.</summary>
    public void Activate(DeviceViewViewModel view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (ActiveDeviceView == view)
        {
            return;
        }

        ActiveDeviceView = view;
        if (view.Host is { IsDocked: true } host)
        {
            DetailDeviceView = host;
        }

        _owner.SyncDeviceViews();
    }

    internal void Toggle(WorkspaceView view)
    {
        if (view.IsDocked && view.Region != WorkspaceRegion.Detail)
        {
            Collapse(view);
        }
        else
        {
            Show(view);
        }
    }

    internal void Show(WorkspaceView view)
    {
        Dock(view);
        if (view.DeviceView is { } deviceView)
        {
            DetailDeviceView = view;
            Activate(deviceView);
            _owner.ShowDetail(LowerPane.Devices);
        }
    }

    internal void Dock(WorkspaceView view)
    {
        if (view.Region != WorkspaceRegion.Detail)
        {
            // A side region shows one tool at a time; the others go back to their icons.
            foreach (var other in Views.Where(v => v != view && v.Region == view.Region && v.IsDocked))
            {
                other.SetPlacement(ViewPlacement.Collapsed);
            }
        }

        Place(view, ViewPlacement.Docked);
    }

    internal void Collapse(WorkspaceView view) => Place(view, ViewPlacement.Collapsed);

    internal void Detach(WorkspaceView view)
    {
        if (view.CanDetach)
        {
            Place(view, ViewPlacement.Detached);
        }
    }

    internal void Close(WorkspaceView view)
    {
        if (!view.CanClose)
        {
            Collapse(view);
            return;
        }

        Views.Remove(view);
        if (view.DeviceView == ActiveDeviceView)
        {
            ActiveDeviceView = DeviceViews.FirstOrDefault();
        }

        Retitle();
        Refresh();
        _owner.SyncDeviceViews();
    }

    /// <summary>The workspace as it should be saved locally.</summary>
    public WorkspaceState Capture(BrowserState browser) => new()
    {
        Views = [.. Views.Select(v => new ViewState
        {
            Id = v.Id,
            Kind = v.Kind,
            Placement = v.IsAwaitingDisplay ? ViewPlacement.Detached : v.Placement,
            DetachedBounds = v.DetachedBounds,
            Device = v.DeviceView?.Capture(),
        })],
        ActiveDeviceView = ActiveDeviceView?.Id,
        LeftExtent = LeftExtent,
        RightExtent = RightExtent,
        DetailExtent = DetailExtent,
        IsDetailVisible = _owner.IsEditorVisible,
        DetailPane = _owner.LowerPane,
        Browser = browser,
        MainWindow = MainWindowBounds,
    };

    /// <summary>
    /// Restores a saved workspace on the displays attached now. A detached view whose window would land on no display (its
    /// monitor is missing) comes back as a rail icon, but keeps its saved window for when the monitor returns.
    /// </summary>
    public void Restore(WorkspaceState state, IReadOnlyList<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(screens);
        IsRestoring = true;
        try
        {
            Apply(state.Upgrade(screens), screens);
        }
        finally
        {
            IsRestoring = false;
        }

        LayoutRestored?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(WorkspaceState state, IReadOnlyList<ScreenArea> screens)
    {
        foreach (var view in Views.Where(v => v.CanClose).ToList())
        {
            Views.Remove(view);
        }

        _nextDeviceView = 1;
        ActiveDeviceView = null;
        LeftExtent = state.LeftExtent;
        RightExtent = state.RightExtent;
        DetailExtent = state.DetailExtent;
        MainWindowBounds = state.MainWindow;
        foreach (var saved in state.Views)
        {
            var view = saved.Kind switch
            {
                WorkspaceViewKind.Inspector => Inspector,
                WorkspaceViewKind.Browser => Browser,
                _ => Add(CreateDeviceView(saved.Id)),
            };
            view.DetachedBounds = saved.DetachedBounds;
            view.SetPlacement(saved.Placement);
            if (saved.Placement == ViewPlacement.Detached && (!view.CanDetach || LayoutRecovery.Restore(saved.DetachedBounds, screens) is null))
            {
                view.IsAwaitingDisplay = view.CanDetach;
                view.Recover();
            }

            if (saved.Device is { } device)
            {
                view.DeviceView?.Restore(device);
            }
        }

        if (!DeviceViews.Any())
        {
            Add(CreateDeviceView(null));
        }

        Retitle();
        ActiveDeviceView = DeviceViews.FirstOrDefault(v => v.Id == state.ActiveDeviceView) ?? DeviceViews.First();
        Refresh();
        _owner.IsInspectorVisible = Inspector.IsDocked;
        _owner.SyncDeviceViews();
        _owner.LowerPane = state.DetailPane;
        _owner.IsEditorVisible = state.IsDetailVisible;
    }

    /// <summary>
    /// Re-checks the views waiting for a missing display after the attached displays changed: a view whose saved window lands on
    /// one of <paramref name="screens"/> again is detached once more, back where it was.
    /// </summary>
    public void ReattachAwaitingViews(IReadOnlyList<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        foreach (var view in Views.Where(v => v.IsAwaitingDisplay && LayoutRecovery.Restore(v.DetachedBounds, screens) is not null).ToList())
        {
            Place(view, ViewPlacement.Detached);
        }
    }

    /// <summary>Called when the inspector is shown or hidden by its own toggle, so the rail matches.</summary>
    internal void SyncInspector(bool visible)
    {
        if (visible != Inspector.IsDocked)
        {
            Place(Inspector, visible ? ViewPlacement.Docked : ViewPlacement.Collapsed);
        }
    }

    private WorkspaceView CreateDeviceView(string? id)
    {
        var number = _nextDeviceView++;
        id ??= string.Create(CultureInfo.InvariantCulture, $"device-view-{number}");
        if (id.StartsWith("device-view-", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan("device-view-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            _nextDeviceView = Math.Max(_nextDeviceView, n + 1);
        }

        var content = new DeviceViewViewModel(_owner, id);
        var view = new WorkspaceView(this, id, WorkspaceViewKind.DeviceView, content);
        content.Host = view;
        return view;
    }

    private WorkspaceView Add(WorkspaceView view)
    {
        Views.Add(view);
        Refresh();
        return view;
    }

    private void Place(WorkspaceView view, ViewPlacement placement)
    {
        if (view.Placement == placement && !view.IsAwaitingDisplay)
        {
            return;
        }

        view.SetPlacement(placement);
        if (view == Inspector)
        {
            _owner.IsInspectorVisible = placement == ViewPlacement.Docked;
        }

        if (view.DeviceView is { } deviceView && placement != ViewPlacement.Collapsed && ActiveDeviceView is null)
        {
            ActiveDeviceView = deviceView;
        }

        Refresh();
        _owner.SyncDeviceViews();
    }

    private void Retitle()
    {
        var devices = Views.Where(v => v.Kind == WorkspaceViewKind.DeviceView).ToList();
        for (var i = 0; i < devices.Count; i++)
        {
            devices[i].RailLabel = devices.Count == 1 ? "Devices" : string.Create(CultureInfo.InvariantCulture, $"Devices {i + 1}");
        }
    }

    private void Refresh()
    {
        Mirror(LeftRail, Views.Where(v => v.IsOnRail && (v.Region == WorkspaceRegion.Left || v.Region == WorkspaceRegion.Detail)));
        Mirror(RightRail, Views.Where(v => v.IsOnRail && v.Region == WorkspaceRegion.Right));
        Mirror(DetailTabs, Views.Where(v => v.Region == WorkspaceRegion.Detail && v.IsDocked));
        DetailDeviceView = DetailTabs.FirstOrDefault(v => v.DeviceView == ActiveDeviceView) ?? DetailTabs.FirstOrDefault();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Mirror(ObservableCollection<WorkspaceView> target, IEnumerable<WorkspaceView> source)
    {
        var wanted = source.ToList();
        if (target.SequenceEqual(wanted))
        {
            return;
        }

        target.Clear();
        foreach (var view in wanted)
        {
            target.Add(view);
        }
    }

    /// <summary>The Device View to insert into from the Browser: the last one used, if it is open and shows a chain. Never a guess.</summary>
    internal DeviceViewViewModel? InsertionTarget =>
        ActiveDeviceView is { HasChain: true, Host.IsCollapsed: false } target ? target : null;

    /// <summary>Re-binds every Device View after the project or the selection changed.</summary>
    internal void Sync(Domain.Projects.Project project, TrackId? selected, Signal.DeviceCatalog catalog)
    {
        foreach (var view in DeviceViews)
        {
            view.Sync(project, selected, catalog);
        }

        foreach (var view in Views.Where(v => v.DeviceView is not null))
        {
            view.Title = view.DeviceView!.Heading;
        }
    }

    /// <summary>Shows a rack in the active Device View, locked to it. A detached view stays in its own window.</summary>
    internal void ShowRack(DeviceChainId rack)
    {
        var view = ActiveDeviceView ?? NewDeviceView().DeviceView!;
        view.LockToRack(rack);
        if (view.Host is { IsDetached: false } host)
        {
            Show(host);
        }
    }
}
