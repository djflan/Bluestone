using Bluestone.Application.Plugins;
using Bluestone.Domain.Devices;

namespace Bluestone.Presentation;

/// <summary>
/// Floating plugin editor windows, as workspace state (never saved in the project or in a plugin's state). With
/// "Floating plugin windows follow the selected track" on, an editor that is not pinned is hidden (its window only: the
/// editor, the instance, and its state stay) while its track (or the chain the Device View shows) is not selected, and
/// comes back when it is. A pinned editor stays. Each editor can also send its keys to Bluestone instead of the plugin,
/// where the platform allows it; the mouse keeps working in the editor either way. Pin and keyboard choices belong to one
/// device's editor and last for the session, through closing and reopening it.
/// </summary>
/// <remarks>
/// Requests to a worker are serialized per device and coalesced (the latest wanted visibility wins), so rapid selection
/// changes never pile up requests. Keys forwarded from an editor are tracked while held; switching keys back to the
/// plugin, hiding or closing the editor, or losing its worker releases them, so a computer-keyboard note never hangs.
/// </remarks>
public sealed class PluginEditorWindows
{
    private readonly MainViewModel _owner;
    private readonly IPluginEditorHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<DeviceId, EditorState> _states = [];

    internal PluginEditorWindows(MainViewModel owner, IPluginEditorHost host, IUiDispatcher dispatcher)
    {
        _owner = owner;
        _host = host;
        _dispatcher = dispatcher;
        host.EditorKeyPressed += (_, e) => dispatcher.Post(() => OnKey(e.Device, e.Key));
        host.StatusChanged += (_, device) => dispatcher.Post(() => OnStatusChanged(device));
        host.HostingChanged += (_, _) => dispatcher.Post(() =>
        {
            SelectionChanged();
            _owner.RefreshDevicePanels();
        });
    }

    /// <summary>A key from an editor whose keys go to Bluestone (or a release for one it held), on the UI thread.</summary>
    public event EventHandler<PluginEditorKey>? KeyForwarded;

    /// <summary>Whether this platform can send an editor's keys to Bluestone.</summary>
    public static bool KeyRoutingSupported => PluginDeviceHost.EditorKeyRoutingSupported;

    /// <summary>Whether unpinned editors follow the selection (a plugin setting).</summary>
    public bool FollowsSelection => _host.EditorsFollowSelection;

    public bool IsOpen(DeviceId device) => _host.IsEditorOpen(device);

    public bool IsPinned(DeviceId device) => _states.TryGetValue(device, out var state) && state.Pinned;

    public bool SendsKeysToBluestone(DeviceId device) => _states.TryGetValue(device, out var state) && state.KeysToHost;

    /// <summary>Whether the editor is hidden because its track is not selected.</summary>
    public bool IsHiddenByFollow(DeviceId device) => _states.TryGetValue(device, out var state) && state.Applied == false && IsOpen(device);

    /// <summary>Pins or unpins one device's editor: a pinned editor stays visible whatever is selected.</summary>
    public void SetPinned(DeviceId device, bool pinned)
    {
        State(device).Pinned = pinned;
        SelectionChanged();
        _owner.RefreshDevicePanels();
    }

    /// <summary>
    /// Sends one editor's keys to Bluestone (or back to the plugin). Returns false when that cannot be done here (the
    /// choice is then not kept), with a message.
    /// </summary>
    public async Task<bool> SetKeysToBluestoneAsync(DeviceId device, bool toBluestone)
    {
        var state = State(device);
        if (!toBluestone)
        {
            ReleaseHeld(state);
        }

        state.KeysToHost = toBluestone;
        var done = !IsOpen(device) || await _host.SetEditorKeyboardAsync(device, toBluestone) || !toBluestone;
        if (!done)
        {
            state.KeysToHost = false;
            _owner.AddMessage(MessageSeverity.Info, "Plugins", "This plugin's editor cannot send its keys to Bluestone on this system; the keys stay with the plugin.");
        }

        _owner.RefreshDevicePanels();
        return done;
    }

    /// <summary>Opens a device's editor and applies its session choices (keys, visibility) to the new window.</summary>
    public async Task<string?> OpenAsync(DeviceId device)
    {
        if (await _host.OpenEditorAsync(device) is { } problem)
        {
            return problem;
        }

        // Opening an editor that is hidden (its track not selected) brings it back; a new window starts shown.
        var state = State(device);
        if (state.KeysToHost && !await _host.SetEditorKeyboardAsync(device, true))
        {
            state.KeysToHost = false;
        }

        Want(device, state, true);
        _owner.RefreshDevicePanels();
        return null;
    }

    /// <summary>Shows or hides each open, unpinned editor for the current selection. Call when the selection changes.</summary>
    public void SelectionChanged()
    {
        var relevant = RelevantDevices();
        foreach (var chain in _owner.Project.Chains)
        {
            foreach (var device in chain.Devices)
            {
                if (!IsOpen(device.Id))
                {
                    continue;
                }

                var state = State(device.Id);
                var visible = !FollowsSelection || state.Pinned || relevant.Contains(device.Id);
                if (!visible)
                {
                    ReleaseHeld(state);
                }

                Want(device.Id, state, visible);
            }
        }
    }

    // The devices whose editors belong with the selection: those on selected tracks, and those in the chain the Device View shows.
    private HashSet<DeviceId> RelevantDevices()
    {
        var project = _owner.Project;
        var tracks = _owner.SelectedTracks.Select(t => t.Id).ToHashSet();
        var shown = _owner.DeviceView?.ChainId;
        return [.. project.Chains
            .Where(c => (c.Owner.Kind == ChainOwnerKind.Track && tracks.Contains(c.Owner.Track)) || c.Id == shown)
            .SelectMany(c => c.Devices)
            .Select(d => d.Id)];
    }

    // The latest wanted visibility wins; one request at a time per device.
    private void Want(DeviceId device, EditorState state, bool visible)
    {
        state.Wanted = visible;
        if (state.Busy || state.Applied == visible)
        {
            return;
        }

        state.Busy = true;
        _ = ApplyAsync(device, state);
    }

    private async Task ApplyAsync(DeviceId device, EditorState state)
    {
        try
        {
            while (state.Applied != state.Wanted && IsOpen(device))
            {
                var visible = state.Wanted;
                await _host.SetEditorVisibleAsync(device, visible);
                state.Applied = visible;
            }
        }
        finally
        {
            state.Busy = false;
            _owner.RefreshDevicePanels();
        }
    }

    private void OnKey(DeviceId device, PluginEditorKey key)
    {
        if (!_states.TryGetValue(device, out var state) || !state.KeysToHost)
        {
            return;
        }

        if (key.Down)
        {
            if (!state.Held.Add(key.Usage) && !key.Repeat)
            {
                return;
            }
        }
        else if (!state.Held.Remove(key.Usage))
        {
            return;
        }

        KeyForwarded?.Invoke(this, key);
    }

    private void OnStatusChanged(DeviceId device)
    {
        if (_states.TryGetValue(device, out var state) && !IsOpen(device))
        {
            // Closed, or its worker went away: release what it held; the next window starts shown.
            ReleaseHeld(state);
            state.Applied = true;
            state.Wanted = true;
        }
    }

    private void ReleaseHeld(EditorState state)
    {
        foreach (var usage in state.Held.ToList())
        {
            KeyForwarded?.Invoke(this, new PluginEditorKey(usage, 0, Down: false, Repeat: false));
        }

        state.Held.Clear();
    }

    private EditorState State(DeviceId device)
    {
        if (!_states.TryGetValue(device, out var state))
        {
            state = new EditorState();
            _states[device] = state;
        }

        return state;
    }

    private sealed class EditorState
    {
        public bool Pinned { get; set; }

        public bool KeysToHost { get; set; }

        /// <summary>The visibility the worker was last told (a new window is visible).</summary>
        public bool Applied { get; set; } = true;

        public bool Wanted { get; set; } = true;

        public bool Busy { get; set; }

        public HashSet<int> Held { get; } = [];
    }
}
