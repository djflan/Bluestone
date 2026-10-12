using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Infrastructure.Projects;
using Bluestone.Signal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>Words for a device definition, as the Browser and the Device View show it.</summary>
public static class DeviceDescriptions
{
    public static string Kind(DeviceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Origin == DeviceOrigin.Plugin
            ? $"{Category(definition)} · {definition.Vendor ?? "Plugin"}"
            : $"{Category(definition)} · Built-in";
    }

    public static string Category(DeviceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Category switch
        {
            DeviceCategory.MidiEffect => "Note FX",
            DeviceCategory.Instrument => "Instrument",
            DeviceCategory.AudioEffect => "Audio FX",
            _ => "Device",
        };
    }

    /// <summary>The plugin format named by a plugin definition's ID ("VST3", "CLAP"), or null for a built-in device.</summary>
    public static string? Format(DeviceDefinitionId id)
    {
        var colon = id.Value.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && id.Value.StartsWith(Bluestone.Application.Plugins.PluginDeviceHost.DefinitionPrefix, StringComparison.Ordinal)
            ? id.Value[Bluestone.Application.Plugins.PluginDeviceHost.DefinitionPrefix.Length..].Split(':')[0].ToUpperInvariant()
            : null;
    }
}

/// <summary>The fixed widths a device panel can take in a horizontal chain (ADR 0036). The height is the same for every panel.</summary>
public enum DevicePanelSize
{
    /// <summary>A narrow vertical title only: the device is collapsed in this view.</summary>
    Collapsed,
    Small,
    Medium,
    Large,
}

/// <summary>What kind of signal a device works on, which sets its accent.</summary>
public enum DeviceRole
{
    Note,
    Instrument,
    Audio,
    Other,
    Unavailable,
}

/// <summary>
/// One Device View (ADR 0036): a workspace view showing one device chain horizontally. Unlocked, it follows the selected track;
/// locked, it keeps its track or rack whatever is selected. Several can be open at once. They all edit the project's one chain
/// through commands, so two views of the same chain always agree on devices, order, bypass, and parameters; what each view
/// collapses, selects, and scrolls to is its own.
/// </summary>
public sealed partial class DeviceViewViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly HashSet<DeviceId> _collapsed = [];
    private TrackId? _lockedTrack;
    private DeviceChainId? _lockedRack;
    private DeviceId? _restoredSelection;
    private bool _syncing;

    internal DeviceViewViewModel(MainViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    /// <summary>The view instance's stable ID in the workspace.</summary>
    public string Id { get; }

    /// <summary>The workspace view hosting this Device View.</summary>
    public WorkspaceView? Host { get; internal set; }

    public ObservableCollection<DevicePanelViewModel> Devices { get; } = [];

    /// <summary>The device selected in this view. Other views keep their own selection.</summary>
    [ObservableProperty]
    public partial DevicePanelViewModel? SelectedDevice { get; set; }

    /// <summary>Locked views keep their track or rack; unlocked views follow the selected track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText))]
    public partial bool IsLocked { get; set; }

    /// <summary>Whether there is a chain to show: a single selected track, a locked track or rack that still exists.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand), nameof(LoadPresetCommand))]
    public partial bool HasChain { get; private set; }

    /// <summary>The view is locked to a track or rack that is no longer in the project.</summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackToTrackCommand))]
    public partial bool IsEditingRack { get; private set; }

    /// <summary>The track's name, or "Rack: name" for a rack; "Devices" when there is nothing to show.</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = "Devices";

    /// <summary>What is shown instead of panels when there are none.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>What feeds the chain, under its IN node: "Track", or "2 sources" for a rack.</summary>
    [ObservableProperty]
    public partial string InputText { get; private set; } = string.Empty;

    /// <summary>Where the chain's output goes, under its OUT node: "Master", a channel or track, or "Not routed".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRouted))]
    public partial string OutputText { get; private set; } = string.Empty;

    /// <summary>Every destination of the chain's output, or what to do when there is none.</summary>
    [ObservableProperty]
    public partial string OutputDetail { get; private set; } = string.Empty;

    public bool IsRouted => OutputText is not ("Not routed" or "");

    /// <summary>Opens the Browser on instruments, aimed at this view's chain: the empty chain's invitation.</summary>
    [RelayCommand]
    private void BrowseInstruments()
    {
        Activate();
        _owner.Workspace.ShowBrowser(BrowserCategory.Instruments);
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The track colour index for the view's accent, or -1 for a rack or nothing.</summary>
    [ObservableProperty]
    public partial int AccentIndex { get; private set; } = -1;

    /// <summary>The horizontal scroll position of this view's chain.</summary>
    [ObservableProperty]
    public partial double ScrollOffset { get; set; }

    public string LockText => IsLocked ? "Locked" : "Follows selection";

    public bool IsEmpty => Devices.Count == 0;

    internal TrackId? Track { get; private set; }

    internal DeviceChainId? Rack { get; private set; }

    internal DeviceChainId? ChainId { get; private set; }

    internal DeviceDefinitionLookup Definitions => _owner.Devices.Lookup;

    /// <summary>Whether this view shows <paramref name="device"/> collapsed. Other views of the same chain are unaffected.</summary>
    public bool IsCollapsed(DeviceId device) => _collapsed.Contains(device);

    /// <summary>The definitions in the chain shown, for the Browser's recommendations.</summary>
    internal IEnumerable<DeviceDefinition?> ChainDefinitions => Devices.Select(d => d.Definition);

    partial void OnIsLockedChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        if (value)
        {
            if (Track is null && Rack is null)
            {
                // Nothing to lock to: stay unlocked rather than lock onto nothing.
                _syncing = true;
                IsLocked = false;
                _syncing = false;
                return;
            }

            _lockedTrack = Rack is null ? Track : null;
            _lockedRack = Rack;
        }
        else
        {
            _lockedTrack = null;
            _lockedRack = null;
        }

        _owner.SyncDeviceViews();
    }

    partial void OnSelectedDeviceChanged(DevicePanelViewModel? oldValue, DevicePanelViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        // Where the Browser inserts follows this view's selection.
        if (_owner.Workspace?.InsertionTarget == this)
        {
            _owner.Browser.UpdateTarget();
        }
    }

    /// <summary>Locks the view to <paramref name="rack"/>, as editing a rack from the rack list does.</summary>
    internal void LockToRack(DeviceChainId rack)
    {
        _lockedRack = rack;
        _lockedTrack = null;
        _syncing = true;
        IsLocked = true;
        _syncing = false;
        _owner.SyncDeviceViews();
    }

    /// <summary>Locks the view to <paramref name="track"/>.</summary>
    public void LockToTrack(TrackId track)
    {
        _lockedTrack = track;
        _lockedRack = null;
        _syncing = true;
        IsLocked = true;
        _syncing = false;
        _owner.SyncDeviceViews();
    }

    /// <summary>Unlocks a view showing a rack, so it follows the selected track again.</summary>
    [RelayCommand(CanExecute = nameof(IsEditingRack))]
    internal void BackToTrack() => IsLocked = false;

    [RelayCommand]
    private void ToggleLock() => IsLocked = !IsLocked;

    /// <summary>Makes this the Device View the Browser inserts into.</summary>
    [RelayCommand]
    public void Activate() => _owner.Workspace.Activate(this);

    /// <summary>Opens another Device View. Lives here so the view's menu reaches it in a detached window too.</summary>
    [RelayCommand]
    private void NewDeviceView() => _owner.Workspace.NewDeviceView();

    internal void Sync(Project project, TrackId? selected, DeviceCatalog catalog)
    {
        DeviceChain? chain = null;
        Rack = null;
        Track = null;
        IsUnavailable = false;
        if (_lockedRack is { } rackId)
        {
            if (project.FindChain(rackId) is { Owner.Kind: ChainOwnerKind.Rack } rack)
            {
                chain = rack;
                Rack = rackId;
            }
            else
            {
                IsUnavailable = true;
            }
        }
        else if (_lockedTrack is { } locked)
        {
            if (project.Sequence.FindTrack(locked) is not null)
            {
                Track = locked;
                chain = project.ChainOf(locked);
            }
            else
            {
                IsUnavailable = true;
            }
        }
        else if (selected is { } id && project.Sequence.FindTrack(id) is not null)
        {
            Track = id;
            chain = project.ChainOf(id);
        }

        ChainId = chain?.Id;
        HasChain = Track is not null || Rack is not null;
        IsEditingRack = Rack is not null;
        Heading = Rack is not null ? $"Rack: {RackName(chain!)}"
            : Track is { } shown ? project.Sequence.FindTrack(shown)!.Name
            : "Devices";
        AccentIndex = Track is { } trackId ? _owner.Tracks.FirstOrDefault(t => t.Id == trackId)?.ColorIndex ?? -1 : -1;
        EmptyText = IsUnavailable ? (_lockedRack is not null ? "The rack this view was locked to has been removed." : "The track this view was locked to has been removed.")
            : !HasChain && _owner.SelectedTracks.Count > 1 ? "Several tracks are selected. Select one to see its devices."
            : !HasChain ? "Select a track to see its devices."
            : "Drop an instrument or effect here";
        var node = Rack is { } shownRack ? SignalNode.Rack(shownRack) : Track is { } shownTrack ? SignalNode.Track(shownTrack) : (SignalNode?)null;
        InputText = node is not { } input ? string.Empty
            : Rack is not null ? Count(project.ConnectionsTo(input).Select(c => c.Source).Distinct().Count(), "source")
            : "Track";
        var destinations = node is { } output ? project.ConnectionsFrom(output).Select(c => c.Destination).Distinct().Select(d => TrackSound.NameOf(project, d)).ToList() : [];
        OutputText = destinations.Count switch
        {
            0 => "Not routed",
            1 => destinations[0],
            _ => Count(destinations.Count, "destination"),
        };
        OutputDetail = destinations.Count == 0 ? "Nothing leaves this chain yet: give it an audio output in the inspector, or a connection." : string.Join(", ", destinations);

        var devices = chain?.Devices ?? [];
        var selectedId = SelectedDevice?.Id ?? _restoredSelection;
        _restoredSelection = null;
        while (Devices.Count > devices.Length)
        {
            Devices.RemoveAt(Devices.Count - 1);
        }

        for (var i = 0; i < devices.Length; i++)
        {
            if (i == Devices.Count)
            {
                Devices.Add(new DevicePanelViewModel(this));
            }

            Devices[i].Sync(project, devices[i], i, devices.Length, catalog.Find(devices[i].Definition.Id), _owner.DeviceStatus(devices[i]), _collapsed.Contains(devices[i].Id));
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId);
        OnPropertyChanged(nameof(IsEmpty));
        SavePresetCommand.NotifyCanExecuteChanged();
        LoadPresetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Inserts a new <paramref name="definition"/> device at <paramref name="index"/> (0 for first, the device count for last) in the
    /// chain this view shows, as a drop does. Refused, with a message, when there is no chain.
    /// </summary>
    public bool InsertAt(int index, DeviceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!HasChain)
        {
            return false;
        }

        var device = DeviceInstance.Create(definition.ToReference());
        var at = Math.Clamp(index, 0, Devices.Count);
        var command = Rack is { } rack
            ? DeviceCommands.InsertDevice(rack, device, Definitions, at)
            : DeviceCommands.InsertDevice(Track!.Value, device, Definitions, at);
        if (!_owner.Execute(command))
        {
            return false;
        }

        Activate();
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == device.Id);
        return true;
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with a new device of <paramref name="definition"/> at the same place, in one
    /// undo step (see <see cref="DeviceCommands.ReplaceDevice"/>). A plugin is started in its worker first: if it cannot
    /// run, the chain is left exactly as it was and a message says why.
    /// </summary>
    public async Task<bool> ReplaceAsync(DeviceId target, DeviceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!HasChain || Devices.FirstOrDefault(d => d.Id == target) is not { } old)
        {
            return false;
        }

        var replacement = DeviceInstance.Create(definition.ToReference());
        var host = _owner.PluginHost;
        var staged = host?.Hosts(definition.Id) == true;
        if (staged)
        {
            string? reason;
            try
            {
                reason = await host!.StageAsync(replacement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reason = ex.Message;
            }

            if (reason is not null)
            {
                _owner.AddMessage(MessageSeverity.Warning, "Replace Device", $"{old.Name} was not replaced: {reason}");
                return false;
            }
        }

        if (!_owner.Execute(DeviceCommands.ReplaceDevice(target, replacement, Definitions)))
        {
            if (staged)
            {
                await host!.UnstageAsync(replacement.Id);
            }

            return false;
        }

        Activate();
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == replacement.Id);
        return true;
    }

    /// <summary>Inserts after the selected device, or at the end: where Enter or a double-click in the Browser puts a device.</summary>
    public bool Insert(DeviceDefinition definition) =>
        InsertAt(SelectedDevice is { } after ? after.Index + 1 : Devices.Count, definition);

    /// <summary>
    /// Moves a device in this view's chain to the gap at <paramref name="gap"/> (0 before the first device, the count after the
    /// last), as dragging it to an insertion marker does. Dropping it beside itself changes nothing.
    /// </summary>
    public bool MoveToGap(DeviceId device, int gap)
    {
        var from = Devices.ToList().FindIndex(d => d.Id == device);
        if (from < 0)
        {
            return false;
        }

        var to = Math.Clamp(gap > from ? gap - 1 : gap, 0, Devices.Count - 1);
        return to == from || _owner.Execute(DeviceCommands.MoveDevice(device, to, Definitions));
    }

    /// <summary>
    /// The gap a drop at <paramref name="x"/> lands in, given the horizontal centres of the panels in order: before the first panel
    /// whose centre is right of the pointer, or after the last.
    /// </summary>
    /// <summary>
    /// Where a drop at <paramref name="x"/> goes: onto device <c>Index</c> to replace it when <paramref name="canReplace"/>
    /// and the pointer is over the middle half of that device's panel; otherwise into the gap <c>Index</c> (see
    /// <see cref="GapAt"/>). The outer quarters of each panel stay insertion zones, so the edges are never ambiguous.
    /// </summary>
    /// <param name="panels">Each device panel's left and right edges, in chain order.</param>
    /// <param name="x">The pointer, in the same coordinates.</param>
    /// <param name="canReplace">Whether the dragged item may replace a device (a Browser item, not a chain device being moved).</param>
    public static (bool Replace, int Index) DropTargetAt(IReadOnlyList<(double Left, double Right)> panels, double x, bool canReplace)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if (canReplace)
        {
            for (var i = 0; i < panels.Count; i++)
            {
                var (left, right) = panels[i];
                var quarter = (right - left) / 4;
                if (x >= left + quarter && x <= right - quarter)
                {
                    return (true, i);
                }
            }
        }

        return (false, GapAt([.. panels.Select(p => (p.Left + p.Right) / 2)], x));
    }

    public static int GapAt(IReadOnlyList<double> centers, double x)
    {
        ArgumentNullException.ThrowIfNull(centers);
        var gap = 0;
        while (gap < centers.Count && centers[gap] <= x)
        {
            gap++;
        }

        return gap;
    }

    /// <summary>This view's binding and presentation, for local view state.</summary>
    internal DeviceViewState Capture() => new()
    {
        LockedTrack = _lockedTrack?.Value,
        LockedRack = _lockedRack?.Value,
        Collapsed = [.. _collapsed.Select(d => d.Value).Order()],
        SelectedDevice = SelectedDevice?.Id.Value,
        ScrollOffset = ScrollOffset,
    };

    internal void Restore(DeviceViewState state)
    {
        _lockedTrack = state.LockedTrack is { } track ? new TrackId(track) : null;
        _lockedRack = state.LockedRack is { } rack ? new DeviceChainId(rack) : null;
        _syncing = true;
        IsLocked = _lockedTrack is not null || _lockedRack is not null;
        _syncing = false;
        _collapsed.Clear();
        _collapsed.UnionWith(state.Collapsed.Select(d => new DeviceId(d)));
        _restoredSelection = state.SelectedDevice is { } selected ? new DeviceId(selected) : null;
        ScrollOffset = Math.Max(0, state.ScrollOffset);
    }

    [RelayCommand(CanExecute = nameof(HasChain))]
    private async Task SavePresetAsync()
    {
        if (ChainId is not { } chain || await _owner.UserInteraction.PickSaveFileAsync("Save Chain Preset", "Chain.bluestone-chain", FileFilters.ChainPreset) is not { } path)
        {
            return;
        }

        var preset = DeviceCommands.SavePreset(_owner.Project, chain, Path.GetFileNameWithoutExtension(path));
        await File.WriteAllBytesAsync(path, ChainPresetSerializer.Serialize(preset)).ConfigureAwait(true);
        _owner.AddMessage(MessageSeverity.Info, "Presets", $"Saved {preset.Devices.Length} devices as \"{preset.Name}\".");
    }

    [RelayCommand(CanExecute = nameof(HasChain))]
    private async Task LoadPresetAsync()
    {
        if (!HasChain || await _owner.UserInteraction.PickOpenFileAsync("Load Chain Preset", [FileFilters.ChainPreset]) is not { } path)
        {
            return;
        }

        try
        {
            var preset = ChainPresetSerializer.Deserialize(await File.ReadAllBytesAsync(path).ConfigureAwait(true));
            _owner.Execute(Rack is { } rack ? DeviceCommands.LoadPreset(rack, preset, Definitions) : DeviceCommands.LoadPreset(Track!.Value, preset, Definitions));
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            _owner.AddMessage(MessageSeverity.Warning, "Presets", $"{Path.GetFileName(path)} could not be loaded: {ex.Message}");
        }
    }

    internal static string RackName(DeviceChain rack) => rack.Name.Length > 0 ? rack.Name : "Rack";

    internal void SetCollapsed(DevicePanelViewModel device, bool collapsed)
    {
        if (collapsed ? _collapsed.Add(device.Id) : _collapsed.Remove(device.Id))
        {
            Activate();
        }
    }

    internal void Select(DevicePanelViewModel device)
    {
        SelectedDevice = device;
        Activate();
    }

    internal void Remove(DevicePanelViewModel device) => _owner.Execute(DeviceCommands.RemoveDevice(device.Id, Definitions));

    internal void Move(DevicePanelViewModel device, int index) => _owner.Execute(DeviceCommands.MoveDevice(device.Id, index, Definitions));

    internal void SetBypassed(DevicePanelViewModel device, bool bypassed) => _owner.Execute(DeviceCommands.SetBypassed(device.Id, bypassed, Definitions));

    internal void SetParameter(DevicePanelViewModel device, ParameterId parameter, ControlValue value) => _owner.Execute(DeviceCommands.SetParameter(device.Id, parameter, value));

    internal void ShowRouting(DevicePanelViewModel device)
    {
        Activate();
        _owner.Connections.RouteFrom(device.Id);
    }

    internal Task RestartAsync(DevicePanelViewModel device) => _owner.RestartDeviceAsync(device.Id);

    internal Task OpenEditorAsync(DevicePanelViewModel device) => _owner.OpenDeviceEditorAsync(device.Id);
}

/// <summary>
/// One device as a Device View shows it: a fixed-height panel with its status, its essential controls, and how it handles the
/// signal bundle. The device itself lives in the project; this is one view's presentation of it (collapsed, selected).
/// </summary>
public sealed partial class DevicePanelViewModel : ObservableObject
{
    /// <summary>How many parameters each panel width shows; the rest are in the device's own editor.</summary>
    public static int ParametersShown(DevicePanelSize size) => size switch
    {
        DevicePanelSize.Small => 4,
        DevicePanelSize.Medium => 6,
        DevicePanelSize.Large => 8,
        _ => 0,
    };

    private readonly DeviceViewViewModel _view;
    private bool _syncing;

    internal DevicePanelViewModel(DeviceViewViewModel view) => _view = view;

    public DeviceId Id { get; private set; }

    public int Index { get; private set; }

    public DeviceDefinition? Definition { get; private set; }

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>"Instrument · Surge Synth Team", "Note FX · Built-in", or "Unknown device".</summary>
    [ObservableProperty]
    public partial string Kind { get; private set; } = string.Empty;

    /// <summary>"VST3" or "CLAP" for a plugin; empty for a built-in device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlugin))]
    public partial string Format { get; private set; } = string.Empty;

    public bool IsPlugin => Format.Length > 0;

    [ObservableProperty]
    public partial DeviceRole Role { get; private set; }

    /// <summary>How the device is doing, as text: "Built-in", "Running", "Not installed", "Crashed".</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>The device's name, with its status when it needs attention: "[Vital: Crashed]".</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool NeedsAttention { get; private set; }

    /// <summary>A plugin that crashed or stopped responding can be restarted from its last saved state.</summary>
    [ObservableProperty]
    public partial bool CanRestart { get; private set; }

    /// <summary>A running VST3 or CLAP plugin can show its own editor, in a window of its worker process.</summary>
    [ObservableProperty]
    public partial bool CanOpenEditor { get; private set; }

    /// <summary>For a plugin, which worker process it runs in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHosting))]
    public partial string Hosting { get; private set; } = string.Empty;

    public bool HasHosting => Hosting.Length > 0;

    /// <summary>For a failed plugin, the other devices that went down with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string Detail { get; private set; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty]
    public partial bool IsBypassed { get; set; }

    /// <summary>Collapsed in this Device View only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size))]
    public partial bool IsCollapsed { get; set; }

    /// <summary>Selected in this Device View only.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>The panel's width class: collapsed in this view, otherwise from how much the device has to show.</summary>
    public DevicePanelSize Size => IsCollapsed ? DevicePanelSize.Collapsed : NaturalSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size))]
    public partial DevicePanelSize NaturalSize { get; private set; } = DevicePanelSize.Small;

    /// <summary>Takes note events in (an instrument or note effect): the panel shows a note input.</summary>
    [ObservableProperty]
    public partial bool UsesNotes { get; private set; }

    /// <summary>Puts audio out (an instrument or audio effect): the panel shows an audio output.</summary>
    [ObservableProperty]
    public partial bool MakesAudio { get; private set; }

    /// <summary>Where this device's output is also sent ("→ Strings, MU2000 B"), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRouting))]
    public partial string RoutingIndicator { get; private set; } = string.Empty;

    public bool HasRouting => RoutingIndicator.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveLeftCommand))]
    public partial bool CanMoveLeft { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveRightCommand))]
    public partial bool CanMoveRight { get; private set; }

    /// <summary>Every parameter the device declares.</summary>
    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    /// <summary>The parameters the panel shows at its width: the first ones the device declares.</summary>
    public ObservableCollection<ParameterViewModel> VisibleParameters { get; } = [];

    /// <summary>"+12 more in editor", or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreParameters))]
    public partial string MoreParametersText { get; private set; } = string.Empty;

    public bool HasMoreParameters => MoreParametersText.Length > 0;

    internal void Sync(Project project, DeviceInstance device, int index, int count, DeviceDefinition? definition, DeviceStatus status, bool collapsed)
    {
        _syncing = true;
        try
        {
            Id = device.Id;
            Index = index;
            Definition = definition;
            Name = device.DisplayName;
            Kind = definition is null ? "Unknown device" : DeviceDescriptions.Kind(definition);
            Format = DeviceDescriptions.Format(device.Definition.Id) ?? string.Empty;
            Status = status.Text;
            NeedsAttention = status.NeedsAttention;
            CanRestart = status.CanRestart;
            CanOpenEditor = status.CanOpenEditor;
            Hosting = status.Hosting;
            Detail = status.Detail;
            Title = status.NeedsAttention ? $"[{device.DisplayName}: {status.Text}]" : device.DisplayName;
            IsBypassed = device.IsBypassed;
            IsCollapsed = collapsed;
            CanMoveLeft = index > 0;
            CanMoveRight = index < count - 1;
            Role = definition is null || status.NeedsAttention ? DeviceRole.Unavailable : definition.Category switch
            {
                DeviceCategory.MidiEffect => DeviceRole.Note,
                DeviceCategory.Instrument => DeviceRole.Instrument,
                DeviceCategory.AudioEffect => DeviceRole.Audio,
                _ => DeviceRole.Other,
            };
            UsesNotes = definition?.Consumes.HasFlag(SignalKinds.Events) == true;
            MakesAudio = definition?.Produces.HasFlag(SignalKinds.Audio) == true;
            var destinations = project.ConnectionsFrom(SignalNode.Device(device.Id)).Select(c => SignalRoutingValidator.Describe(project, c.Destination)).ToList();
            RoutingIndicator = destinations.Count == 0 ? string.Empty : "→ " + string.Join(", ", destinations);

            var descriptors = definition?.Parameters ?? [];
            if (!Parameters.Select(p => p.Id).SequenceEqual(descriptors.Select(d => d.Id)))
            {
                Parameters.Clear();
                foreach (var descriptor in descriptors)
                {
                    Parameters.Add(new ParameterViewModel(this, descriptor));
                }
            }

            foreach (var parameter in Parameters)
            {
                parameter.Sync(device.ValueOf(parameter.Id) ?? parameter.Descriptor.DefaultValue);
            }

            NaturalSize = Parameters.Count switch
            {
                <= 4 when !IsPlugin => DevicePanelSize.Small,
                <= 6 => DevicePanelSize.Medium,
                _ => DevicePanelSize.Large,
            };
            var shown = ParametersShown(NaturalSize);
            if (!VisibleParameters.SequenceEqual(Parameters.Take(shown)))
            {
                VisibleParameters.Clear();
                foreach (var parameter in Parameters.Take(shown))
                {
                    VisibleParameters.Add(parameter);
                }
            }

            var more = Parameters.Count - VisibleParameters.Count;
            MoreParametersText = more <= 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"+{more} more{(CanOpenEditor ? " in editor" : string.Empty)}");
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsBypassedChanged(bool value)
    {
        if (!_syncing)
        {
            _view.SetBypassed(this, value);
        }
    }

    partial void OnIsCollapsedChanged(bool value)
    {
        if (!_syncing)
        {
            _view.SetCollapsed(this, value);
        }
    }

    [RelayCommand]
    private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    [RelayCommand]
    private void ToggleBypassed() => IsBypassed = !IsBypassed;

    [RelayCommand]
    private void Select() => _view.Select(this);

    [RelayCommand]
    private void Remove() => _view.Remove(this);

    [RelayCommand(CanExecute = nameof(CanMoveLeft))]
    private void MoveLeft() => _view.Move(this, Index - 1);

    [RelayCommand(CanExecute = nameof(CanMoveRight))]
    private void MoveRight() => _view.Move(this, Index + 1);

    /// <summary>Opens the routing inspector with this device's output as the source.</summary>
    [RelayCommand]
    private void ShowRouting() => _view.ShowRouting(this);

    [RelayCommand]
    private Task RestartAsync() => _view.RestartAsync(this);

    [RelayCommand]
    private Task OpenEditorAsync() => _view.OpenEditorAsync(this);

    internal void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (!_syncing)
        {
            _view.SetParameter(this, parameter, value);
        }
    }
}

/// <summary>One parameter, shown and edited in its own units, stored normalized.</summary>
public sealed partial class ParameterViewModel : ObservableObject
{
    private readonly DevicePanelViewModel _device;
    private bool _syncing;

    internal ParameterViewModel(DevicePanelViewModel device, ParameterDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor;
    }

    public ParameterDescriptor Descriptor { get; }

    public ParameterId Id => Descriptor.Id;

    public string Name => Descriptor.Name;

    public double Minimum => Descriptor.Minimum;

    public double Maximum => Descriptor.Maximum;

    /// <summary>The slider's step: one position for a stepped parameter, otherwise 1% of the range.</summary>
    public double Step => Descriptor.Steps > 0 ? (Descriptor.Maximum - Descriptor.Minimum) / Descriptor.Steps : (Descriptor.Maximum - Descriptor.Minimum) / 100;

    /// <summary>A knob's step: one position for a stepped parameter, or 0 for a continuous one, which a knob nudges finely.</summary>
    public double KnobStep => Descriptor.Steps > 0 ? Step : 0;

    [ObservableProperty]
    public partial double Value { get; set; }

    /// <summary>An on/off parameter (one step), shown as a toggle rather than a knob.</summary>
    public bool IsSwitch => Descriptor.Steps == 1;

    /// <summary>A switch's state; setting it writes the maximum or minimum.</summary>
    public bool IsOn
    {
        get => Value > (Minimum + Maximum) / 2;
        set => Value = value ? Maximum : Minimum;
    }

    /// <summary>The value's position in its range, 0 to 1, for a knob.</summary>
    public double Normalized => Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;

    /// <summary>The value a reset (double-click) returns to.</summary>
    public double DefaultPlain => Descriptor.ToPlain(Descriptor.DefaultValue);

    public string ValueText => IsSwitch ? (IsOn ? "On" : "Off")
        : Descriptor.Steps > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Value:0.##} {Descriptor.Unit}").Trim()
        : string.Create(CultureInfo.InvariantCulture, $"{Value:0.###} {Descriptor.Unit}").Trim();

    internal void Sync(ControlValue stored)
    {
        _syncing = true;
        try
        {
            Value = Descriptor.ToPlain(stored);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(Normalized));
        OnPropertyChanged(nameof(IsOn));
        if (!_syncing)
        {
            _device.SetParameter(Id, Descriptor.ToStored(Math.Clamp(value, Minimum, Maximum)));
        }
    }
}

/// <summary>
/// A device's status in words, and whether it needs the musician's attention. For a plugin, <c>Hosting</c> says which
/// worker process it is in and <c>Detail</c> what else went down with it when it failed.
/// </summary>
public sealed record DeviceStatus(string Text, bool NeedsAttention = false, bool CanRestart = false, bool CanOpenEditor = false, string Hosting = "", string Detail = "")
{
    public static DeviceStatus Of(DeviceInstance device, DeviceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Find(device.Definition.Id) is not { } definition)
        {
            return new("Not installed", NeedsAttention: true);
        }

        if (device.IsBypassed)
        {
            return new("Bypassed");
        }

        return definition.Origin == DeviceOrigin.Plugin ? new("Plugin") : new("Built-in");
    }
}
